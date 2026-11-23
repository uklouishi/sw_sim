using System.Collections.Concurrent;
using WsSim.Core.Cards;
using WsSim.Core.Game;

namespace WsSim.Core.Hosting;

/// <summary>引擎线程发给界面的事件。界面每帧用 <see cref="GameHost.TryDequeue"/> 取出，按顺序处理。</summary>
public abstract record HostEvent(GameView View);

/// <summary>局面变了（AI 正要做选择）。界面刷新即可。</summary>
public sealed record StateUpdated(GameView View, Decision Decision) : HostEvent(View);

/// <summary>轮到由界面操作的玩家做选择。用 <see cref="GameHost.Submit"/> 回答。</summary>
public sealed record DecisionRequired(GameView View, Decision Decision) : HostEvent(View);

/// <summary>
/// 局面变了一步（卡移动、方向改变、洗牌、攻击宣告、阶段开始），只在 <see cref="GameHostOptions.StepEvents"/> 时发出。
/// <see cref="Event"/> 已按视角处理：两端都看不到的卡，<see cref="CardMovedEvent.InstanceId"/> 为 -1。
/// <see cref="Card"/> 是移动的那张卡（两端都看不到时为 <see cref="CardView.Hidden"/>），
/// <see cref="FromVisible"/> / <see cref="ToVisible"/> 表示移动前 / 后这名玩家能否看到正面（界面据此决定是否翻面）。
/// </summary>
public sealed record BoardChanged(GameView View, GameEvent Event, CardView? Card, bool FromVisible, bool ToVisible) : HostEvent(View);

public sealed record GameFinished(GameView View, GameResult Result) : HostEvent(View);

/// <summary>引擎抛出了异常（规则实现的 bug）。对局无法继续。</summary>
public sealed record HostFailed(GameView View, Exception Error) : HostEvent(View);

public sealed class GameHostOptions
{
    /// <summary>界面视角的玩家（只能看到自己的手牌）。</summary>
    public int Viewer { get; init; }
    /// <summary>双方手牌都可见（观战 / 调试）。</summary>
    public bool RevealAll { get; init; }
    /// <summary>AI 每次做选择前等待的时间，让玩家看得清对手的操作。</summary>
    public TimeSpan AiDelay { get; init; } = TimeSpan.Zero;
    public GameOptions? Rules { get; init; }
    /// <summary>每一步变化都发 <see cref="BoardChanged"/>（界面播动画用）。</summary>
    public bool StepEvents { get; init; }
}

/// <summary>
/// 在后台线程运行 <see cref="GameEngine"/>，把它的同步提问变成事件队列，供 Godot 等界面使用。
/// <para>
/// 座位的 agent 为 null 表示由界面操作：引擎问到这名玩家时发出 <see cref="DecisionRequired"/> 并阻塞，
/// 直到界面调用 <see cref="Submit"/>。其他座位（AI）照常调用 agent，之前先发 <see cref="StateUpdated"/>。
/// </para>
/// <para>
/// 所有快照都在引擎线程上、引擎停在提问处时生成，所以界面线程从不直接读 <see cref="GameState"/>。
/// </para>
/// </summary>
public sealed class GameHost : IDisposable
{
    private readonly GameEngine _engine;
    private readonly GameHostOptions _options;
    private readonly ConcurrentQueue<HostEvent> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private (Decision Decision, TaskCompletionSource<int[]> Answer)? _pending;
    private int _logShown;
    private Task? _task;

    public GameHost(Deck deck0, Deck deck1, IPlayerAgent? agent0, IPlayerAgent? agent1, ulong seed, GameHostOptions? options = null)
    {
        _options = options ?? new GameHostOptions();
        _engine = new GameEngine(deck0, deck1,
            new Seat(this, agent0), new Seat(this, agent1), seed, _options.Rules);
        if (_options.StepEvents) _engine.EventRaised += OnEngineEvent;
    }

    /// <summary>这名视角的玩家能否看到某区域里卡的正面。</summary>
    private bool CanSee(int owner, ZoneType zone) => zone switch
    {
        ZoneType.Deck => false,
        ZoneType.Hand => _options.RevealAll || owner == _options.Viewer,
        ZoneType.Stock => _options.RevealAll,
        _ => true,
    };

    private void OnEngineEvent(GameEvent e)
    {
        if (_cts.IsCancellationRequested) return;
        CardView? card = null;
        bool fromVisible = true, toVisible = true;
        if (e is CardMovedEvent m)
        {
            fromVisible = CanSee(m.Owner, m.From);
            toVisible = CanSee(m.Owner, m.To);
            var inst = _engine.State.Players[m.Owner].FindInstance(m.InstanceId);
            if (fromVisible || toVisible)
            {
                card = inst != null ? CardView.Of(inst) : null;
            }
            else
            {
                card = CardView.Hidden;
                e = m with { InstanceId = -1 };
            }
        }
        _events.Enqueue(new BoardChanged(Snapshot(), e, card, fromVisible, toVisible));
    }

    public ulong Seed => _engine.Seed;

    /// <summary>回答记录（对局结束后可保存为回放）。只在对局结束后读取。</summary>
    public IReadOnlyList<RecordedDecision> Decisions => _engine.Decisions;

    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (_task != null) throw new InvalidOperationException("已经开始");
        _task = Task.Factory.StartNew(RunEngine, TaskCreationOptions.LongRunning);
    }

    public bool TryDequeue(out HostEvent ev) => _events.TryDequeue(out ev!);

    /// <summary>当前在等界面回答的选择；没有则为 null。</summary>
    public Decision? PendingDecision
    {
        get { lock (_lock) return _pending?.Decision; }
    }

    /// <summary>回答当前选择。序号不对、答案不合法时返回 false（不改变任何状态）。</summary>
    public bool Submit(int sequence, IReadOnlyList<int> answer)
    {
        TaskCompletionSource<int[]> tcs;
        lock (_lock)
        {
            if (_pending is not { } p || p.Decision.Sequence != sequence || !p.Decision.IsValidAnswer(answer))
                return false;
            tcs = p.Answer;
            _pending = null;
        }
        tcs.TrySetResult(answer.ToArray());
        return true;
    }

    /// <summary>等后台线程结束（测试用）。</summary>
    public bool Wait(TimeSpan timeout) => _task?.Wait(timeout) ?? true;

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            _pending?.Answer.TrySetCanceled();
            _pending = null;
        }
        try { _task?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _cts.Dispose();
    }

    private void RunEngine()
    {
        try
        {
            var result = _engine.Run();
            _events.Enqueue(new GameFinished(Snapshot(), result));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (AggregateException e) when (_cts.IsCancellationRequested && e.InnerException is OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _events.Enqueue(new HostFailed(Snapshot(), e));
        }
    }

    private GameView Snapshot()
    {
        var log = _engine.Log;
        var newLog = log.Skip(_logShown).ToArray();
        _logShown = log.Count;
        return GameView.Create(_engine.State, _options.Viewer, _options.RevealAll, newLog);
    }

    /// <summary>引擎看到的玩家：AI 座位转给 agent，界面座位排队等回答。</summary>
    private sealed class Seat : IPlayerAgent
    {
        private readonly GameHost _host;
        private readonly IPlayerAgent? _ai;

        public Seat(GameHost host, IPlayerAgent? ai)
        {
            _host = host;
            _ai = ai;
        }

        public int[] Decide(GameState state, Decision d)
        {
            var token = _host._cts.Token;
            token.ThrowIfCancellationRequested();
            if (_ai != null)
            {
                _host._events.Enqueue(new StateUpdated(_host.Snapshot(), d));
                if (_host._options.AiDelay > TimeSpan.Zero)
                    token.WaitHandle.WaitOne(_host._options.AiDelay);
                token.ThrowIfCancellationRequested();
                return _ai.Decide(state, d);
            }

            var tcs = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_host._lock) _host._pending = (d, tcs);
            _host._events.Enqueue(new DecisionRequired(_host.Snapshot(), d));
            tcs.Task.Wait(token);
            return tcs.Task.Result;
        }
    }
}

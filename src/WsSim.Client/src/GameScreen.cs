using Godot;
using WsSim.Core.Agents;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>
/// 对局界面。规则全部在 <see cref="GameHost"/>（后台线程上的 WsSim.Core 引擎）里计算，
/// 这里只显示快照、把点击翻译成引擎给出的选项序号。
/// <para>
/// 场地按 WS 实卡布局，我方在下、对手在上（旋转 180°），见 <see cref="BoardLayout"/>。
/// 引擎每一步变化（卡移动、横置、攻击、阶段）都发事件，界面排队逐个播放动画（GameScreen.Anim.cs）。
/// </para>
/// <para>
/// 操作方式：可以操作的卡有黄框。点一张卡，如果只对应一个选项就直接执行；
/// 如果对应多个（出角色要选格子、移动要选目标、攻击要选方式），这张卡变蓝框，
/// 可去的格子变绿框，点格子或右侧按钮完成。多选（换牌、弃牌）点卡切换选中，再按“确定”。
/// </para>
/// </summary>
public partial class GameScreen : Control
{
    private static readonly Vector2 HandCard = BoardLayout.HandCard;

    private readonly Main _main;
    private readonly GameConfig _config;
    private GameHost _host = null!;
    private GameView? _view;
    private Decision? _decision;
    private GameResult? _result;
    private string? _error;

    // 选择中的状态
    private readonly HashSet<int> _multi = new();
    private CardControl? _armed;
    private List<int> _armedOptions = new();
    private bool _showAllActions;

    private readonly List<CardControl> _controls = new();
    private bool _openZoneForDecision;

    // 侧栏
    private Label _detailName = null!;
    private CardControl _detailCard = null!;
    private Label _detailInfo = null!;
    private RichTextLabel _detailText = null!;
    private Label _prompt = null!;
    private VBoxContainer _options = null!;
    private RichTextLabel _log = null!;
    private PopupPanel? _zonePopup;
    private Control? _overlay;

    public GameScreen(Main main, GameConfig config)
    {
        _main = main;
        _config = config;
    }

    private int Me => 0;
    private bool IsMyDecision => _decision != null && !_config.Spectate;

    public override void _Ready()
    {
        BuildLayout();
        _host = new GameHost(_config.PlayerDeck, _config.OpponentDeck,
            _config.Spectate ? new SimpleAgent() : null, new SimpleAgent(), _config.Seed,
            new GameHostOptions
            {
                Viewer = Me,
                RevealAll = _config.Spectate,
                AiDelay = TimeSpan.FromMilliseconds(_config.AiDelayMs),
                StepEvents = true,
            });
        AppendLog($"[color=#9aa3b2]种子 {_config.Seed}　{_config.PlayerDeck.Name}（我） 对 {_config.OpponentDeck.Name}[/color]");
        _host.Start();
    }

    public override void _ExitTree() => _host?.Dispose();

    // ================================================================ 事件

    public override void _Process(double delta)
    {
        while (_host.TryDequeue(out var ev)) _queue.Enqueue(ev);
        _busy -= delta;
        if (_queue.Count == 0 || _busy > 0) return;

        bool changed = false;
        int guard = 0;
        while (_queue.Count > 0 && _busy <= 0 && guard++ < 400)
        {
            changed = true;
            var ev = _queue.Dequeue();
            if (ev is BoardChanged bc)
            {
                if (Animate)
                {
                    PlayStep(bc);
                    continue;
                }
                // 不播动画：只收日志，最后统一刷新。
                AcceptView(bc.View);
                _dirty = true;
                continue;
            }
            AcceptView(ev.View);
            HandleEvent(ev);
            _dirty = true;
        }
        if (_dirty)
        {
            _dirty = false;
            Rebuild();
        }
        if (!changed) return;
        if (_openZoneForDecision && IsMyDecision) OpenZoneForDecision();
        _openZoneForDecision = false;
        if (IsMyDecision && _selfTestDone < _main.SelfTestSteps) SelfTestStep();
        else if (IsMyDecision && _main.SelfTestSteps > 0)
            ShowDetail(_controls.FirstOrDefault(c => c.Zone == ZoneType.Stage && c.Card != null)?.Card);
    }

    private bool _dirty;

    /// <summary>收下一个快照：记日志。</summary>
    private void AcceptView(GameView v)
    {
        _view = v;
        foreach (var line in v.NewLog) AppendLog(Escape(PlayerNames(line)));
    }

    private void HandleEvent(HostEvent ev)
    {
        switch (ev)
        {
            case DecisionRequired dr:
                _decision = dr.Decision;
                ResetSelection();
                _openZoneForDecision = true;
                break;
            case StateUpdated:
                _decision = null;
                ResetSelection();
                break;
            case GameFinished f:
                _decision = null;
                _result = f.Result;
                if (_main.SelfTestSteps > 0)
                    GD.Print($"SELFTEST done: {_selfTestDone} 个选择, {_selfTestFailures} 个失败；{f.Result.Reason}，{f.Result.Turns} 回合");
                break;
            case HostFailed hf:
                _decision = null;
                _error = hf.Error.ToString();
                GD.PrintErr(_error);
                break;
        }
    }

    private void ResetSelection()
    {
        _multi.Clear();
        _armed = null;
        _armedOptions = new();
        _showAllActions = false;
    }

    private void Submit(params int[] answer)
    {
        if (_decision == null) return;
        if (!_host.Submit(_decision.Sequence, answer))
        {
            AppendLog("[color=#e0736e]这个选择不合法[/color]");
            return;
        }
        _decision = null;
        ResetSelection();
        _zonePopup?.Hide();
        Rebuild();
    }
}

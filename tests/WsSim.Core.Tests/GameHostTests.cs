using WsSim.Core.Agents;
using WsSim.Core.Game;
using WsSim.Core.Hosting;
using Xunit;

namespace WsSim.Core.Tests;

/// <summary>GameHost：后台线程运行引擎、界面座位通过事件队列回答。</summary>
public class GameHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>只看得到快照的“界面玩家”：选第一个非“结束”的选项，多选时不选（最少张数）。</summary>
    private static int[] UiPolicy(Decision d)
    {
        if (d.MaxPicks != 1 || d.MinPicks != 1)
            return Enumerable.Range(0, d.MinPicks).ToArray();
        // 主要阶段只出角色、不移动，避免来回移动拖长对局。
        for (int i = 0; i < d.Options.Count; i++)
        {
            var o = d.Options[i];
            if (o.IsPass || o.MainAction == MainActionKind.Move) continue;
            return new[] { i };
        }
        return new[] { d.Options.ToList().FindIndex(o => o.IsPass) is var p && p >= 0 ? p : 0 };
    }

    /// <summary>界面线程的事件循环：处理事件直到对局结束。</summary>
    private static (GameFinished Finished, List<HostEvent> Events) Pump(GameHost host, Func<HostEvent, bool>? onEvent = null)
    {
        var events = new List<HostEvent>();
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!host.TryDequeue(out var ev)) { Thread.Sleep(1); continue; }
            events.Add(ev);
            if (onEvent != null && !onEvent(ev)) continue;
            switch (ev)
            {
                case DecisionRequired dr:
                    Assert.True(host.Submit(dr.Decision.Sequence, UiPolicy(dr.Decision)));
                    break;
                case GameFinished f:
                    return (f, events);
                case HostFailed failed:
                    throw new Exception("引擎出错", failed.Error);
            }
        }
        throw new TimeoutException("对局没有在时限内结束");
    }

    [Fact]
    public void HumanSeatPlaysFullGame_SameResultAsDirectEngine()
    {
        const ulong seed = 20261123;
        using var host = new GameHost(TestData.Sao, TestData.Nikke, null, new SimpleAgent(), seed);
        host.Start();
        var (finished, events) = Pump(host);

        Assert.True(finished.View.IsOver);
        Assert.Contains(events, e => e is DecisionRequired);
        Assert.Contains(events, e => e is StateUpdated);

        // 同样的种子、同样的选择，直接跑引擎结果相同（GameHost 不引入任何不确定性）。
        var direct = new GameEngine(TestData.Sao, TestData.Nikke,
            new ScriptedAgent((_, d) => UiPolicy(d)), new SimpleAgent(), seed);
        var r = direct.Run();
        Assert.Equal(r.Winner, finished.Result.Winner);
        Assert.Equal(r.Turns, finished.Result.Turns);
        Assert.Equal(direct.Decisions.Select(x => string.Join(",", x.Answer)),
            host.Decisions.Select(x => string.Join(",", x.Answer)));

        // 日志按顺序分段送出，拼起来就是完整日志。
        Assert.Equal(direct.Log, events.SelectMany(e => e.View.NewLog));
    }

    [Fact]
    public void BothSeatsAi_RunsToEnd()
    {
        using var host = new GameHost(TestData.Sao, TestData.Nikke, new SimpleAgent(), new SimpleAgent(), 7,
            new GameHostOptions { RevealAll = true });
        host.Start();
        var (finished, events) = Pump(host);
        Assert.DoesNotContain(events, e => e is DecisionRequired);
        Assert.True(finished.Result.Winner != null || finished.Result.IsDraw);
    }

    [Fact]
    public void InvalidAnswersAreRejected()
    {
        using var host = new GameHost(TestData.Sao, TestData.Nikke, null, null, 1);
        host.Start();
        DecisionRequired? first = null;
        var deadline = DateTime.UtcNow + Timeout;
        while (first == null && DateTime.UtcNow < deadline)
            if (host.TryDequeue(out var ev) && ev is DecisionRequired dr) first = dr;
        Assert.NotNull(first);
        var d = first!.Decision;
        Assert.Equal(DecisionType.ChooseFirstPlayer, d.Type);

        Assert.False(host.Submit(d.Sequence + 1, new[] { 0 }));   // 序号不对
        Assert.False(host.Submit(d.Sequence, new[] { 5 }));       // 越界
        Assert.False(host.Submit(d.Sequence, new[] { 0, 1 }));    // 个数不对
        Assert.Same(d, host.PendingDecision);
        Assert.True(host.Submit(d.Sequence, new[] { 0 }));
        Assert.False(host.Submit(d.Sequence, new[] { 0 }));       // 已经回答过
    }

    [Fact]
    public void DisposeWhileWaiting_StopsEngineThread()
    {
        var host = new GameHost(TestData.Sao, TestData.Nikke, null, new SimpleAgent(), 3,
            new GameHostOptions { AiDelay = TimeSpan.FromMinutes(10) });
        host.Start();
        var deadline = DateTime.UtcNow + Timeout;
        // 引擎要么停在等界面回答，要么停在 AI 的等待里（猜拳胜者不定）。
        HostEvent? ev0 = null;
        while (ev0 == null && DateTime.UtcNow < deadline)
            if (!host.TryDequeue(out ev0)) Thread.Sleep(1);
        Assert.True(ev0 is DecisionRequired or StateUpdated);
        host.Dispose();
        Assert.False(host.IsRunning);
        while (host.TryDequeue(out var ev)) Assert.IsNotType<HostFailed>(ev);
    }

    [Fact]
    public void ViewHidesOpponentHandAndStock()
    {
        using var host = new GameHost(TestData.Sao, TestData.Nikke, null, new SimpleAgent(), 11);
        host.Start();
        GameView? seen = null;
        Pump(host, ev =>
        {
            if (ev is DecisionRequired { Decision.Type: DecisionType.MainAction } && seen == null && ev.View.Opponent.Hand.Count > 0)
                seen = ev.View;
            return true;
        });
        Assert.NotNull(seen);
        var v = seen!;
        Assert.All(v.Me.Hand, c => Assert.False(c.IsHidden));
        Assert.All(v.Opponent.Hand, c => Assert.True(c.IsHidden && c.InstanceId == -1));
        Assert.All(v.Me.Stock.Concat(v.Opponent.Stock), c => Assert.True(c.IsHidden));
        Assert.Equal(50, v.Me.DeckCount + v.Me.Hand.Count + v.Me.Clock.Count + v.Me.Level.Count
            + v.Me.Stock.Count + v.Me.WaitingRoom.Count + v.Me.Memory.Count + v.Me.ClimaxArea.Count + v.Me.Resolution.Count
            + v.Me.Stage.Count(c => c != null));
    }

    [Fact]
    public void StepEvents_SameResult_EveryMoveMatchesSnapshotAndHidesUnseenCards()
    {
        const ulong seed = 777;
        using var plain = new GameHost(TestData.Sao, TestData.Nikke, null, new SimpleAgent(), seed);
        plain.Start();
        var (f1, _) = Pump(plain);

        using var host = new GameHost(TestData.Sao, TestData.Nikke, null, new SimpleAgent(), seed, new GameHostOptions { StepEvents = true });
        host.Start();
        var (f2, events) = Pump(host);
        Assert.Equal(f1.Result, f2.Result);

        var steps = events.OfType<BoardChanged>().ToList();
        Assert.True(steps.Count > 100);
        Assert.Contains(steps, s => s.Event is ShuffleEvent);
        Assert.Contains(steps, s => s.Event is AttackDeclaredEvent);
        Assert.Contains(steps, s => s.Event is CardOrientationEvent { To: CardOrientation.Rested });
        Assert.Contains(steps, s => s.Event is PhaseEvent { Phase: Phase.Attack });

        foreach (var s in steps)
        {
            if (s.Event is not CardMovedEvent m) continue;
            var p = s.View.Players[m.Owner];
            // 快照是移动之后的局面：卡在目标位置。
            if (m.To == ZoneType.Stage)
            {
                Assert.NotNull(p.Stage[(int)m.ToSlot!.Value]);
                Assert.Equal(m.InstanceId, p.Stage[(int)m.ToSlot!.Value]!.InstanceId);
            }
            else if (m.To != ZoneType.Deck)
            {
                var list = Zone(p, m.To);
                Assert.InRange(m.ToIndex, 0, list.Count - 1);
                if (s.ToVisible) Assert.Equal(m.InstanceId, list[m.ToIndex].InstanceId);
            }
            // 两端都看不到（对手抽牌、牌库进能量区）就不能带 InstanceId 和卡面。
            if (!s.FromVisible && !s.ToVisible)
            {
                Assert.Equal(-1, m.InstanceId);
                Assert.True(s.Card!.IsHidden);
            }
            if (m.Owner == 1 && m.From == ZoneType.Deck && m.To == ZoneType.Hand) Assert.Equal(-1, m.InstanceId);
        }
    }

    private static IReadOnlyList<CardView> Zone(PlayerView p, ZoneType z) => z switch
    {
        ZoneType.Hand => p.Hand,
        ZoneType.Clock => p.Clock,
        ZoneType.Level => p.Level,
        ZoneType.Stock => p.Stock,
        ZoneType.WaitingRoom => p.WaitingRoom,
        ZoneType.Memory => p.Memory,
        ZoneType.ClimaxArea => p.ClimaxArea,
        ZoneType.Resolution => p.Resolution,
        _ => throw new ArgumentException(z.ToString()),
    };
}

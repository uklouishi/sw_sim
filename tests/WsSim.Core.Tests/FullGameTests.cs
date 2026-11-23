using WsSim.Core.Agents;
using WsSim.Core.Game;
using Xunit;
using Xunit.Abstractions;

namespace WsSim.Core.Tests;

/// <summary>P1 完成标准：用测试卡组从开局一路打到分出胜负。</summary>
public class FullGameTests
{
    private readonly ITestOutputHelper _out;
    public FullGameTests(ITestOutputHelper output) => _out = output;

    /// <summary>每次提问时检查不变量：卡不会凭空多出或消失、伤害区不会停在 7 张以上等。</summary>
    private static void AttachInvariantChecks(GameEngine engine)
    {
        engine.DecisionRequested += d =>
        {
            foreach (var p in engine.State.Players)
            {
                Assert.Equal(50, p.TotalCards);
                if (d.Type != DecisionType.LevelUp)
                    Assert.True(p.Clock.Count < 7, $"P{p.Index} 伤害区 {p.Clock.Count} 张却没有升级");
                Assert.True(p.PlayerLevel < 4);
                for (int i = 0; i < p.Stage.Length; i++)
                    if (p.Stage[i] is { } c) Assert.Equal(ZoneType.Stage, c.Zone);
                foreach (var z in new[] { ZoneType.Deck, ZoneType.Hand, ZoneType.Clock, ZoneType.Level, ZoneType.Stock, ZoneType.WaitingRoom, ZoneType.ClimaxArea })
                    Assert.All(p.ZoneList(z), c => Assert.Equal(z, c.Zone));
                if (d.Type is DecisionType.MainAction or DecisionType.AttackDeclaration)
                {
                    Assert.Empty(p.Resolution);
                    Assert.True(p.Deck.Count > 0);
                }
            }
        };
    }

    private static void AssertFinished(GameResult r, GameEngine e)
    {
        Assert.True(e.State.IsOver);
        Assert.DoesNotContain("上限", r.Reason);
        if (!r.IsDraw)
        {
            Assert.NotNull(r.Winner);
            var loser = e.State.Players[1 - r.Winner!.Value];
            Assert.True(loser.HasLost);
            Assert.True(loser.PlayerLevel >= 4 || (loser.Deck.Count == 0 && loser.WaitingRoom.Count == 0));
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(20261123UL)]
    [InlineData(0xDEADBEEFUL)]
    public void SimpleAgents_NikkeVsSao_PlayToAWinner(ulong seed)
    {
        var e = new GameEngine(TestData.Nikke, TestData.Sao, new SimpleAgent(), new SimpleAgent(), seed);
        AttachInvariantChecks(e);
        var r = e.Run();
        _out.WriteLine($"种子 {seed}：{r.Reason}，{r.Turns} 回合，{e.Decisions.Count} 个选择");
        AssertFinished(r, e);
        Assert.NotNull(r.Winner);
        Assert.Contains(e.Log, l => l.Contains("正面攻击") || l.Contains("直接攻击"));
        Assert.Contains(e.Log, l => l.Contains("升级"));
    }

    [Fact]
    public void RandomAgents_ManySeeds_AllGamesFinish()
    {
        int wins0 = 0, wins1 = 0, draws = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var e = new GameEngine(TestData.Nikke, TestData.Sao, new RandomAgent(seed * 31), new RandomAgent(seed * 17 + 5), seed);
            AttachInvariantChecks(e);
            var r = e.Run();
            AssertFinished(r, e);
            if (r.IsDraw) draws++; else if (r.Winner == 0) wins0++; else wins1++;
        }
        _out.WriteLine($"P0 胜 {wins0}，P1 胜 {wins1}，平局 {draws}");
        Assert.True(wins0 > 0 && wins1 > 0);
    }

    [Fact]
    public void FirstPlayer_AttacksOnlyOnceOnFirstTurn()
    {
        var attackLine = new System.Text.RegularExpressions.Regex(@"^P\d 的 .*(正面|侧面|直接)攻击");
        int maxTurn1 = 0, maxOther = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var e = new GameEngine(TestData.Nikke, TestData.Sao, new SimpleAgent(), new SimpleAgent(), seed);
            e.Run();
            int turn = 0, count = 0;
            foreach (var line in e.Log)
            {
                if (line.StartsWith("===== 第 "))
                {
                    turn++;
                    count = 0;
                    continue;
                }
                if (!attackLine.IsMatch(line)) continue;
                count++;
                if (turn == 1) maxTurn1 = Math.Max(maxTurn1, count);
                else maxOther = Math.Max(maxOther, count);
            }
        }
        Assert.Equal(1, maxTurn1);
        Assert.True(maxOther >= 2);
    }

    [Fact]
    public void Mulligan_KeepsFiveCards()
    {
        var e = new GameEngine(TestData.Nikke, TestData.Sao, new RandomAgent(1), new RandomAgent(2), 5);
        bool checkedHands = false;
        e.DecisionRequested += d =>
        {
            if (!checkedHands && d.Type == DecisionType.Clock)
            {
                // 第 1 回合伤害阶段：先攻玩家 5 + 抽 1。
                Assert.Equal(6, e.State.Active.Hand.Count);
                Assert.Equal(5, e.State.NonActive.Hand.Count);
                checkedHands = true;
            }
        };
        e.Run();
        Assert.True(checkedHands);
    }

    [Fact]
    public void SameDeckMirrorMatch_Finishes()
    {
        var e = new GameEngine(TestData.Sao, TestData.Sao, new SimpleAgent(), new RandomAgent(3), 77);
        AttachInvariantChecks(e);
        AssertFinished(e.Run(), e);
    }
}

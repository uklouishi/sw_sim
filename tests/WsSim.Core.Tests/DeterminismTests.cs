using WsSim.Core.Agents;
using WsSim.Core.Game;
using Xunit;

namespace WsSim.Core.Tests;

/// <summary>同一种子、同样的操作必须得到完全一样的对局。</summary>
public class DeterminismTests
{
    private static (GameEngine engine, List<ulong> hashes) Play(ulong seed, IPlayerAgent a0, IPlayerAgent a1)
    {
        var e = new GameEngine(TestData.Nikke, TestData.Sao, a0, a1, seed);
        var hashes = new List<ulong>();
        e.DecisionRequested += _ => hashes.Add(e.StateHash());
        e.Run();
        return (e, hashes);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(123456789UL)]
    public void SameSeedSameAgents_IdenticalGame(ulong seed)
    {
        var (a, ha) = Play(seed, new RandomAgent(seed + 1), new RandomAgent(seed + 2));
        var (b, hb) = Play(seed, new RandomAgent(seed + 1), new RandomAgent(seed + 2));
        Assert.Equal(a.Log, b.Log);
        Assert.Equal(ha, hb);
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.Equal(a.Decisions.Select(d => string.Join(",", d.Answer)), b.Decisions.Select(d => string.Join(",", d.Answer)));
    }

    [Fact]
    public void DifferentSeed_DifferentGame()
    {
        var (a, _) = Play(1, new SimpleAgent(), new SimpleAgent());
        var (b, _) = Play(2, new SimpleAgent(), new SimpleAgent());
        Assert.NotEqual(a.Log, b.Log);
    }

    /// <summary>只记录双方的回答，用同一种子重放，必须逐步得到相同的状态哈希（联机同步的基础）。</summary>
    [Fact]
    public void ReplayingRecordedAnswers_ReproducesGameExactly()
    {
        const ulong seed = 4242;
        var (orig, origHashes) = Play(seed, new RandomAgent(9), new SimpleAgent());

        var answers0 = orig.Decisions.Where(d => d.Player == 0).Select(d => d.Answer);
        var answers1 = orig.Decisions.Where(d => d.Player == 1).Select(d => d.Answer);
        var (replay, replayHashes) = Play(seed, new ReplayAgent(answers0), new ReplayAgent(answers1));

        Assert.Equal(origHashes, replayHashes);
        Assert.Equal(orig.Log, replay.Log);
        Assert.Equal(orig.State.Winner, replay.State.Winner);
    }

    [Fact]
    public void InvalidAnswer_IsRejected()
    {
        var bad = new ScriptedAgent((_, d) => new[] { d.Options.Count });
        var e = new GameEngine(TestData.Nikke, TestData.Sao, bad, bad, 1);
        Assert.Throws<InvalidOperationException>(() => e.Run());
    }
}

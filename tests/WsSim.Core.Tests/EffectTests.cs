using WsSim.Core.Agents;
using WsSim.Core.Cards;
using WsSim.Core.Effects;
using WsSim.Core.Game;
using Xunit;
using Xunit.Abstractions;

namespace WsSim.Core.Tests;

/// <summary>效果脚本解释器：覆盖率、压力测试（随机卡组随机打）。</summary>
public class EffectStressTests
{
    private readonly ITestOutputHelper _out;
    public EffectStressTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<CardDefinition> SeriesCards(string prefix) =>
        TestData.Db.All.Where(c => c.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Coverage_Report()
    {
        foreach (var (name, filter) in new (string, Func<CardDefinition, bool>)[]
                 {
                     ("NIKKE", c => c.Id.StartsWith("NIK/")),
                     ("SAO", c => c.Id.StartsWith("SAO/")),
                     ("全部", c => true),
                 })
        {
            var cards = TestData.Db.All.Where(filter).ToList();
            var reports = cards.Select(ScriptCoverage.Analyze).ToList();
            int ok = reports.Count(r => r.FullySupported);
            _out.WriteLine($"{name}: {ok}/{cards.Count} 张卡的脚本全部认识");
            foreach (var g in reports.SelectMany(r => r.Missing).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(40))
                _out.WriteLine($"  {g.Count(),4} {g.Key}   " + string.Join(" ", reports.Where(r => r.Missing.Contains(g.Key)).Take(3).Select(r => r.Card.Id)));
        }
    }
}

public class EffectGameTests
{
    private readonly ITestOutputHelper _out;
    public EffectGameTests(ITestOutputHelper output) => _out = output;

    /// <summary>从一个系列随机组 50 张：8 张名场面，其余角色 / 事件，每种最多 4 张。</summary>
    public static Deck RandomDeck(string name, IReadOnlyList<CardDefinition> pool, ulong seed)
    {
        var rng = new Randomness.DeterministicRandom(seed);
        var cx = pool.Where(c => c.IsClimax).ToList();
        var others = pool.Where(c => !c.IsClimax).ToList();
        var cards = new List<CardDefinition>();
        while (cards.Count < 8 && cx.Count > 0) cards.Add(cx[rng.NextInt(cx.Count)]);
        var counts = new Dictionary<string, int>();
        while (cards.Count < 50)
        {
            var c = others[rng.NextInt(others.Count)];
            counts.TryGetValue(c.Id, out var n);
            if (n >= 4) continue;
            counts[c.Id] = n + 1;
            cards.Add(c);
        }
        return new Deck(name, cards);
    }

    public static List<CardDefinition> Pool(string series) => series == "NIKKE"
        ? TestData.Db.All.Where(c => c.Id.StartsWith("NIK/")).ToList()
        : TestData.Db.All.Where(c => c.Id.StartsWith("SAO/")).ToList();

    private static void Check(GameEngine e)
    {
        e.DecisionRequested += d =>
        {
            foreach (var p in e.State.Players)
            {
                Assert.Equal(50, e.State.TotalCardsOf(p.Index));
                for (int i = 0; i < p.Stage.Length; i++)
                    if (p.Stage[i] is { } c) Assert.Equal(ZoneType.Stage, c.Zone);
                foreach (var z in new[] { ZoneType.Deck, ZoneType.Hand, ZoneType.Clock, ZoneType.Level, ZoneType.Stock, ZoneType.WaitingRoom, ZoneType.ClimaxArea, ZoneType.Memory })
                    Assert.All(p.ZoneList(z), c => Assert.Equal(z, c.Zone));
            }
        };
    }

    [Theory]
    [InlineData("NIKKE", "SAO", 0)]
    [InlineData("SAO", "NIKKE", 1)]
    [InlineData("NIKKE", "NIKKE", 2)]
    [InlineData("SAO", "SAO", 3)]
    public void RandomDecks_RandomAgents_NoCrash(string a, string b, int salt)
    {
        var poolA = Pool(a);
        var poolB = Pool(b);
        int games = 60, finished = 0;
        var unknown = new Dictionary<string, int>();
        for (int g = 0; g < games; g++)
        {
            ulong seed = (ulong)(salt * 1000 + g + 1);
            var e = new GameEngine(RandomDeck(a, poolA, seed * 7), RandomDeck(b, poolB, seed * 13),
                g % 2 == 0 ? new RandomAgent(seed) : new SimpleAgent(), new RandomAgent(seed + 99), seed,
                new GameOptions { MaxTurns = 80 });
            Check(e);
            GameResult r;
            try
            {
                r = e.Run();
            }
            catch (Exception ex)
            {
                _out.WriteLine(string.Join("\n", e.Log.TakeLast(25)));
                throw new Exception($"种子 {seed} 第 {e.State.TurnNumber} 回合出错：{ex.Message}", ex);
            }
            if (!r.Reason.Contains("上限")) finished++;
            foreach (var l in e.Log.Where(l => l.StartsWith("[脚本]")))
                unknown[l] = unknown.TryGetValue(l, out var n) ? n + 1 : 1;
        }
        _out.WriteLine($"{a} vs {b}：{finished}/{games} 局正常结束");
        foreach (var kv in unknown.OrderByDescending(kv => kv.Value).Take(30)) _out.WriteLine($"  {kv.Value,4} {kv.Key}");
        Assert.True(finished >= games * 0.9, $"只有 {finished}/{games} 局正常结束");
    }
}

public class EffectLogDump
{
    private readonly ITestOutputHelper _out;
    public EffectLogDump(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_TestDecks_Log()
    {
        var e = new GameEngine(TestData.Nikke, TestData.Sao, new SimpleAgent(), new SimpleAgent(), 5);
        var r = e.Run();
        _out.WriteLine($"结果：{r.Reason}，{r.Turns} 回合");
        foreach (var l in e.Log) _out.WriteLine(l);
    }
}

/// <summary>具体卡片的效果。</summary>
public class CardEffectTests
{
    private static CardDefinition C(string id) => TestData.Db.Get(id);

    private static List<CardDefinition> DeckWith(params CardDefinition[] cards)
    {
        var list = cards.ToList();
        list.AddRange(Make.Many(50 - list.Count, () => Make.Char(power: 1000)));
        return list;
    }

    [Fact]
    public void Climax_GiveAll_PowerAndSoul()
    {
        var cx = C("NIK/S117-026"); // SEA, YOU, AGAIN：全体 +1000 / +1 魂
        var b = new Board(DeckWith(cx), DeckWith());
        var ch = b.PutOnStage(b.P0, d => d.Power == 1000, StageSlot.FrontLeft);
        int p0 = ch.Power, s0 = ch.Soul;
        b.Put(b.P0, d => d.Id == cx.Id, ZoneType.ClimaxArea);
        b.Engine.UpdateContinuous();
        Assert.Equal(p0 + 1000, ch.Power);
        Assert.Equal(s0 + 1, ch.Soul);
    }

    [Fact]
    public void Assist_GivesPowerByLevel_OnlyInMyTurn()
    {
        var liter = C("NIK/S117-015"); // 应援：我方回合，前方角色 +等级×1000
        var front = Make.Char(level: 2, power: 5000);
        var b = new Board(DeckWith(liter, front), DeckWith());
        b.PutOnStage(b.P0, d => d.Id == liter.Id, StageSlot.BackLeft);
        var f = b.PutOnStage(b.P0, d => d == front, StageSlot.FrontLeft);
        b.Engine.UpdateContinuous();
        Assert.Equal(7000, f.Power);
        b.State.ActivePlayer = 1;
        b.Engine.UpdateContinuous();
        Assert.Equal(5000, f.Power);
    }

    [Fact]
    public void EncoreKeyword_ClockCost_ReturnsRested()
    {
        var naga = C("NIK/S117-022"); // 【自】再演 [牌库顶 1 张放到伤害区]
        var b = new Board(DeckWith(naga), DeckWith());
        var c = b.PutOnStage(b.P0, d => d.Id == naga.Id, StageSlot.FrontCenter, CardOrientation.Reversed);
        b.Engine.EncoreStep();
        Assert.Equal(ZoneType.Stage, c.Zone);
        Assert.Equal(CardOrientation.Rested, c.Orientation);
        Assert.Single(b.P0.Clock);
    }

    [Fact]
    public void Act_Brainstorm_PerClimax()
    {
        var suguha = C("SAO/S20-E034"); // 【起】集中 [(1)] 每张名场面 +3000
        var cx = Make.Climax();
        var b = new Board(DeckWith(suguha, cx), DeckWith());
        var s = b.PutOnStage(b.P0, d => d.Id == suguha.Id, StageSlot.FrontCenter);
        b.Put(b.P0, d => d.Power == 1000, ZoneType.Stock);
        // 牌库顶 4 张里放 1 张名场面
        var cxCard = b.P0.Deck.First(x => x.Def.IsClimax);
        b.P0.Deck.Remove(cxCard);
        b.P0.Deck.Insert(2, cxCard);
        int before = s.Power;
        var acts = b.Engine.UsableActs(s);
        Assert.Single(acts);
        b.Engine.ActivateAbility(s, acts[0].Index);
        Assert.Equal(before + 3000, s.Power);
        Assert.Empty(b.P0.Stock);
        Assert.Equal(5, b.P0.WaitingRoom.Count); // 翻开的 4 张 + 付的 1 能量
    }

    [Fact]
    public void OnAttack_PayCost_RevealAndAddToHand()
    {
        var crow = C("NIK/S117-013"); // 攻击时：[其他 NIKKE 放到休息室] 公开牌库顶，是 NIKKE 角色就加入手牌
        var nikke = C("NIK/S117-022");
        var b = new Board(DeckWith(crow, nikke, nikke), DeckWith());
        var a = b.PutOnStage(b.P0, d => d.Id == crow.Id, StageSlot.FrontCenter);
        var other = b.PutOnStage(b.P0, d => d.Id == nikke.Id, StageSlot.BackLeft);
        var top = b.P0.Deck.First(x => x.Def.Id == nikke.Id);
        b.P0.Deck.Remove(top);
        b.P0.Deck.Insert(0, top);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Direct));
        Assert.Equal(ZoneType.WaitingRoom, other.Zone);
        Assert.Equal(ZoneType.Hand, top.Zone);
    }
}

public class CardEffectTests2
{
    private static CardDefinition C(string id) => TestData.Db.Get(id);
    private static List<CardDefinition> DeckWith(params CardDefinition[] cards)
    {
        var list = cards.ToList();
        list.AddRange(Make.Many(50 - list.Count, () => Make.Char(power: 1000)));
        return list;
    }

    [Fact]
    public void GiveSelf_CountsTargets_ButPowersSelf()
    {
        var moran = C("NIK/S117-078"); // 其他 NIKKE 每个 +1000
        var nikke = C("NIK/S117-022");
        var b = new Board(DeckWith(moran, nikke, nikke), DeckWith());
        var m = b.PutOnStage(b.P0, d => d.Id == moran.Id, StageSlot.FrontCenter);
        var n1 = b.PutOnStage(b.P0, d => d.Id == nikke.Id, StageSlot.FrontLeft);
        var n2 = b.PutOnStage(b.P0, d => d.Id == nikke.Id, StageSlot.FrontRight);
        b.Engine.UpdateContinuous();
        Assert.Equal(moran.Power + 2000, m.Power);
        // Naga 自己的【永】：全部是 NIKKE 时 +2000（Moran 和另一张 Naga 也是 NIKKE）
        Assert.Equal(nikke.Power + 2000, n1.Power);
        Assert.Equal(nikke.Power + 2000, n2.Power);
    }

    [Fact]
    public void GiveSelf_FacingCondition()
    {
        var anis = C("NIK/S117-002"); // 我方回合，正对的角色是蓝色时 +4000；有其他 NIKKE 时 +3000
        var blue = Make.Char(color: CardColor.Blue, power: 1000);
        var b = new Board(DeckWith(anis), DeckWith(blue));
        var a = b.PutOnStage(b.P0, d => d.Id == anis.Id, StageSlot.FrontLeft);
        b.Engine.UpdateContinuous();
        Assert.Equal(anis.Power, a.Power);
        var f = b.PutOnStage(b.P1, d => d == blue, StageSlot.FrontRight); // 我方左格正对对手右格
        b.Engine.UpdateContinuous();
        Assert.Equal(anis.Power + 4000, a.Power);
        Assert.Equal(1000, f.Power);
    }
}

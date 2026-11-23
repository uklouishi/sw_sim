using WsSim.Core.Cards;
using WsSim.Core.Randomness;
using Xunit;

namespace WsSim.Core.Tests;

public class DeterministicRandomTests
{
    [Fact]
    public void SameSeed_SameSequence()
    {
        var a = new DeterministicRandom(12345);
        var b = new DeterministicRandom(12345);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
    }

    [Fact]
    public void DifferentSeed_DifferentSequence()
    {
        var a = new DeterministicRandom(1);
        var b = new DeterministicRandom(2);
        Assert.NotEqual(Enumerable.Range(0, 10).Select(_ => a.NextUInt64()), Enumerable.Range(0, 10).Select(_ => b.NextUInt64()));
    }

    [Fact]
    public void NextInt_StaysInRange_AndCoversAllValues()
    {
        var r = new DeterministicRandom(7);
        var seen = new int[6];
        for (int i = 0; i < 6000; i++)
        {
            int v = r.NextInt(6);
            Assert.InRange(v, 0, 5);
            seen[v]++;
        }
        Assert.All(seen, c => Assert.InRange(c, 800, 1200));
    }

    [Fact]
    public void Shuffle_IsPermutation_AndReproducible()
    {
        var x = Enumerable.Range(0, 50).ToList();
        var y = Enumerable.Range(0, 50).ToList();
        new DeterministicRandom(99).Shuffle(x);
        new DeterministicRandom(99).Shuffle(y);
        Assert.Equal(x, y);
        Assert.Equal(Enumerable.Range(0, 50), x.OrderBy(v => v));
        Assert.NotEqual(Enumerable.Range(0, 50), x);
    }

    /// <summary>固定种子的输出值不能随代码改动而变化，否则旧回放和不同版本之间的联机都会失效。</summary>
    [Fact]
    public void Sequence_IsStableAcrossVersions()
    {
        var r = new DeterministicRandom(0);
        var first = Enumerable.Range(0, 3).Select(_ => r.NextUInt64()).ToArray();
        var again = new DeterministicRandom(0);
        Assert.Equal(first, Enumerable.Range(0, 3).Select(_ => again.NextUInt64()).ToArray());
        Assert.Equal(new DeterministicRandom(0).NextInt(1000), new DeterministicRandom(0).NextInt(1000));
        Assert.Equal(GoldenSeed0, first);
    }

    // xoshiro256**（SplitMix64 初始化）种子 0 的前 3 个输出，已用独立的 Python 参考实现核对。
    private static readonly ulong[] GoldenSeed0 = { 0x99EC5F36CB75F2B4UL, 0xBF6E1F784956452AUL, 0x1A5F849D4933E6E0UL };
}

public class CardDataTests
{
    [Fact]
    public void LoadsCards_AndParsesFields()
    {
        var c = TestData.Db.Get("NIK/S117-010");
        Assert.Equal(CardType.Character, c.Type);
        Assert.Equal("Proceeding With the Plan, Rapi", c.Name);
        Assert.Equal(CardColor.Yellow, c.Color);
        Assert.Equal(3, c.Level);
        Assert.Equal(2, c.Cost);
        Assert.Equal(10000, c.Power);
        Assert.Equal(2, c.Soul);
        Assert.Equal(new[] { TriggerIcon.Soul }, c.Triggers);
        Assert.Contains("NIKKE", c.Traits);
    }

    [Fact]
    public void MissingSoulField_DefaultsToOne()
    {
        var c = TestData.Db.Get("SAO/S100-E001"); // CardData 里没有 Soul 行
        Assert.Equal(1, c.Soul);
        Assert.Equal(0, TestData.Db.Get("SAO/S100-E022").Soul); // 名场面不适用
    }

    [Fact]
    public void CloneCard_InheritsFieldsOfTarget()
    {
        const string json = """
        [
          {"id":"X/C-002","type":"Character","name":"","traits":[],"series":["X"],"script":"Clone X/C-001"},
          {"id":"X/C-001","type":"Character","name":"Original","color":"R","level":2,"cost":1,"power":7000,"soul":1,
           "trigger":["Soul"],"traits":["A"],"series":["X"],"texts_en":["t"],"script":"Backup 2500","script_expanded":"Backup 2500"},
          {"id":"X/C-003","type":"Character","name":"Alt","power":8000,"series":["X"],"script":"Clone X/C-001"}
        ]
        """;
        var db = new CardDatabase();
        db.AddFromJson(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Equal(0, db.UnresolvedCloneCount);
        var c = db.Get("X/C-002");
        Assert.Equal("Original", c.Name);
        Assert.Equal((CardColor.Red, 2, 1, 7000), (c.Color, c.Level, c.Cost, c.Power));
        Assert.Equal(2500, c.BackupPower);
        Assert.Equal("X/C-001", c.CloneOf);
        var alt = db.Get("X/C-003");
        Assert.Equal(("Alt", 8000, 2), (alt.Name, alt.Power, alt.Level));
    }

    [Fact]
    public void ParsesBackupPower()
    {
        Assert.Equal(3500, TestData.Db.Get("NIK/S117-T20").BackupPower);
        Assert.Null(TestData.Db.Get("NIK/S117-010").BackupPower);
    }

    [Fact]
    public void LookupFallsBackToCaseInsensitive()
    {
        Assert.True(TestData.Db.TryGet("nik/s117-010", out var c));
        Assert.Equal("NIK/S117-010", c.Id);
    }

    [Theory]
    [InlineData("TEST_NIKKE.txt")]
    [InlineData("TEST_SAO.txt")]
    public void TestDecks_AreLegal(string file)
    {
        var deck = Deck.LoadFile(Path.Combine(TestData.Dir, file), TestData.Db);
        Assert.Empty(deck.Validate());
        Assert.Equal(50, deck.Cards.Count);
        Assert.Equal(8, deck.Cards.Count(c => c.IsClimax));
    }

    [Fact]
    public void Validate_ReportsProblems()
    {
        var cx = Make.Climax();
        var deck = new Deck("bad", Enumerable.Repeat(cx, 9).ToList());
        var errors = deck.Validate();
        Assert.Equal(3, errors.Count); // 张数、同名、名场面
    }
}

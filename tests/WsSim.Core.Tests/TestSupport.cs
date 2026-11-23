using WsSim.Core.Cards;
using WsSim.Core.Game;

namespace WsSim.Core.Tests;

/// <summary>
/// 测试数据：优先读仓库根目录 ws-devkit/data/cards.json（开发包不进仓库，自行放置），
/// 没有时读 TestData 里的 NIKKE / SAO 子集（格式相同）。
/// </summary>
public static class TestData
{
    private static readonly Lazy<CardDatabase> _db = new(LoadDb);

    public static CardDatabase Db => _db.Value;

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "TestData");

    public static string? FindDevkitCardsJson()
    {
        var env = Environment.GetEnvironmentVariable("WS_DEVKIT");
        if (!string.IsNullOrEmpty(env))
        {
            var p = Path.Combine(env, "data", "cards.json");
            if (File.Exists(p)) return p;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "ws-devkit", "data", "cards.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }

    private static CardDatabase LoadDb()
    {
        var full = FindDevkitCardsJson();
        return full != null
            ? CardDatabase.LoadFiles(full)
            : CardDatabase.LoadFiles(Path.Combine(Dir, "subset_nikke.json"), Path.Combine(Dir, "subset_sao.json"));
    }

    public static Deck Nikke => Deck.LoadFile(Path.Combine(Dir, "TEST_NIKKE.txt"), Db);
    public static Deck Sao => Deck.LoadFile(Path.Combine(Dir, "TEST_SAO.txt"), Db);
}

/// <summary>按固定规则回答的玩家：Func 返回 null 时交给后备玩家。</summary>
public sealed class ScriptedAgent : IPlayerAgent
{
    private readonly Func<GameState, Decision, int[]?> _f;
    private readonly IPlayerAgent _fallback;

    public ScriptedAgent(Func<GameState, Decision, int[]?> f, IPlayerAgent? fallback = null)
    {
        _f = f;
        _fallback = fallback ?? new Agents.SimpleAgent();
    }

    public List<Decision> Seen { get; } = new();

    public int[] Decide(GameState state, Decision d)
    {
        Seen.Add(d);
        return _f(state, d) ?? _fallback.Decide(state, d);
    }
}

/// <summary>
/// 规则单元测试用的局面搭建：用合成卡片组成卡组，不经过 Setup（不洗牌），
/// 再把牌库里的卡直接摆到需要的区域。
/// </summary>
public sealed class Board
{
    public GameEngine Engine { get; }
    public GameState State => Engine.State;
    public PlayerState P0 => State.Players[0];
    public PlayerState P1 => State.Players[1];

    public Board(IReadOnlyList<CardDefinition> deck0, IReadOnlyList<CardDefinition> deck1, IPlayerAgent? a0 = null, IPlayerAgent? a1 = null, GameOptions? options = null)
    {
        Engine = new GameEngine(new Deck("A", deck0), new Deck("B", deck1),
            a0 ?? new Agents.SimpleAgent(), a1 ?? new Agents.SimpleAgent(), 42, options);
        State.FirstPlayer = 0;
        State.ActivePlayer = 0;
        State.TurnNumber = 3;
        State.Phase = Phase.Attack;
    }

    /// <summary>把牌库里第一张满足条件的卡移到指定区域顶端。</summary>
    public CardInstance Put(PlayerState p, Func<CardDefinition, bool> pick, ZoneType zone)
    {
        var c = p.Deck.First(x => pick(x.Def));
        p.Deck.Remove(c);
        c.Zone = zone;
        p.ZoneList(zone).Add(c);
        return c;
    }

    public CardInstance PutOnStage(PlayerState p, Func<CardDefinition, bool> pick, StageSlot slot, CardOrientation o = CardOrientation.Standing)
    {
        var c = p.Deck.First(x => pick(x.Def));
        p.Deck.Remove(c);
        c.Zone = ZoneType.Stage;
        c.Orientation = o;
        p.Stage[(int)slot] = c;
        return c;
    }

    /// <summary>执行一步；对局在其中结束时返回 true。</summary>
    public bool Step(Action a)
    {
        try { a(); return false; }
        catch (GameEngine.GameEndedSignal) { return true; }
    }
}

public static class Make
{
    private static int _n;

    public static CardDefinition Char(int level = 0, int power = 3000, int soul = 1, CardColor color = CardColor.Yellow, int cost = 0,
        TriggerIcon[]? triggers = null, int? backup = null, string? name = null)
    {
        int n = Interlocked.Increment(ref _n);
        return new CardDefinition
        {
            Id = $"T/C-{n:000}",
            Type = CardType.Character,
            Name = name ?? $"角色{n}",
            Color = color,
            Level = level,
            Cost = cost,
            Power = power,
            Soul = soul,
            Triggers = triggers ?? Array.Empty<TriggerIcon>(),
            RawTriggers = (triggers ?? Array.Empty<TriggerIcon>()).Select(t => t.ToString()).ToArray(),
            BackupPower = backup,
        };
    }

    public static CardDefinition Climax(CardColor color = CardColor.Yellow, TriggerIcon[]? triggers = null)
    {
        int n = Interlocked.Increment(ref _n);
        var t = triggers ?? new[] { TriggerIcon.Soul };
        return new CardDefinition
        {
            Id = $"T/CX-{n:000}",
            Type = CardType.Climax,
            Name = $"名场面{n}",
            Color = color,
            Triggers = t,
            RawTriggers = t.Select(x => x.ToString()).ToArray(),
        };
    }

    public static List<CardDefinition> Many(int count, Func<CardDefinition> f) => Enumerable.Range(0, count).Select(_ => f()).ToList();
}

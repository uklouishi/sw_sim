using WsSim.Core.Cards;

namespace WsSim.Core.Game;

/// <summary>区域。Marker 是舞台角色下面的标记（卡在 <see cref="CardInstance.MarkerHost"/> 的 Markers 里）。</summary>
public enum ZoneType { Deck, Hand, Clock, Level, Stock, WaitingRoom, Memory, ClimaxArea, Resolution, Stage, Marker }

/// <summary>舞台格：前列 3 格（0–2），后列 2 格（3–4），左右以该玩家自己的视角为准。
/// 我方前列左格正对对手前列右格（综合规则 3.6.6），见 <see cref="StageSlots.Facing"/>。</summary>
public enum StageSlot { FrontLeft = 0, FrontCenter = 1, FrontRight = 2, BackLeft = 3, BackRight = 4 }

public enum CardOrientation { Standing, Rested, Reversed }

public enum Phase { Setup, Stand, Draw, Clock, Main, Climax, Attack, Encore, End, GameOver }

public enum AttackType { Front, Side, Direct }

public static class StageSlots
{
    public const int Count = 5;
    public static readonly StageSlot[] All = { StageSlot.FrontLeft, StageSlot.FrontCenter, StageSlot.FrontRight, StageSlot.BackLeft, StageSlot.BackRight };
    public static readonly StageSlot[] Front = { StageSlot.FrontLeft, StageSlot.FrontCenter, StageSlot.FrontRight };
    public static bool IsFront(this StageSlot s) => (int)s <= 2;
    /// <summary>正对的对手前列格（综合规则 3.6.6：①↔③、②↔②、③↔①）。后列没有正对的格子。</summary>
    public static StageSlot Facing(this StageSlot s) =>
        s.IsFront() ? (StageSlot)(2 - (int)s) : throw new ArgumentException("后列没有正对的格子", nameof(s));
    public static string ToChinese(this StageSlot s) => s switch
    {
        StageSlot.FrontLeft => "前列左",
        StageSlot.FrontCenter => "前列中",
        StageSlot.FrontRight => "前列右",
        StageSlot.BackLeft => "后列左",
        _ => "后列右",
    };
}

/// <summary>对局中的一张实体卡。InstanceId 在一局内唯一且由开局顺序决定（确定性）。</summary>
public sealed class CardInstance
{
    public int InstanceId { get; }
    public CardDefinition Def { get; }
    public int Owner { get; }

    public ZoneType Zone { get; internal set; }
    public CardOrientation Orientation { get; internal set; }

    /// <summary>有期限的修正（效果给的战力、魂、等级、能力等）。离开舞台或到期时移除。</summary>
    internal List<Effects.TimedModifier> Modifiers { get; } = new();

    /// <summary>【永】效果算出来的修正，每次重新计算（<see cref="GameEngine"/> 的 UpdateContinuous）。</summary>
    internal Effects.ContinuousLayer Cont { get; } = new();

    /// <summary>这张卡下面的标记。</summary>
    public List<CardInstance> Markers { get; } = new();
    /// <summary>这张卡是标记时，它在哪张卡下面。</summary>
    public CardInstance? MarkerHost { get; internal set; }

    /// <summary>最近一次在舞台上的格子（离开舞台后保留，用于“放回原来的位置”）。</summary>
    public StageSlot? LastSlot { get; internal set; }
    /// <summary>最近一次放到舞台上的回合；从手牌使用时 <see cref="PlayedFromHandTurn"/> 也会记下。</summary>
    public int EnteredStageTurn { get; internal set; } = -1;
    public int PlayedFromHandTurn { get; internal set; } = -1;

    /// <summary>每回合发动次数（按能力计）。</summary>
    internal Dictionary<Effects.Ability, int> UsesThisTurn { get; } = new(ReferenceEqualityComparer.Instance);
    internal HashSet<string> TempLabels { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal CardInstance(int instanceId, CardDefinition def, int owner)
    {
        InstanceId = instanceId;
        Def = def;
        Owner = owner;
        Zone = ZoneType.Deck;
    }

    private int Timed(Effects.ModKind k) { int s = 0; foreach (var m in Modifiers) if (m.Kind == k) s += m.Value; return s; }

    /// <summary>有期限的战力 / 魂修正合计（判定的魂图示、助太刀、效果）。</summary>
    public int PowerModifierUntilEndOfTurn => Timed(Effects.ModKind.Power);
    public int SoulModifierUntilEndOfTurn => Timed(Effects.ModKind.Soul);

    public int Power => Def.Power + Timed(Effects.ModKind.Power) + Cont.Power;
    public int Soul => Math.Max(0, Def.Soul + Timed(Effects.ModKind.Soul) + Cont.Soul);
    public int Level => Math.Max(0, Def.Level + Timed(Effects.ModKind.Level) + Cont.Level);
    public int Cost => Math.Max(0, Def.Cost + Timed(Effects.ModKind.Cost) + Cont.Cost);

    public IEnumerable<string> Traits => Def.Traits
        .Concat(Modifiers.Where(m => m.Kind == Effects.ModKind.Trait).Select(m => m.Text!))
        .Concat(Cont.Traits);

    public bool HasTrait(string t) => Traits.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase));

    /// <summary>特殊效果（CannotSideAttack 等）。</summary>
    public bool Has(string special) =>
        Cont.Specials.Contains(special) || Modifiers.Any(m => m.Kind == Effects.ModKind.Special && string.Equals(m.Text, special, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> Specials =>
        Cont.Specials.Concat(Modifiers.Where(m => m.Kind == Effects.ModKind.Special).Select(m => m.Text!)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>现在拥有的所有能力：卡面的 + 效果给的（有期限的和【永】给的）。</summary>
    public IEnumerable<(Effects.Ability Ability, CardInstance Granter)> Abilities
    {
        get
        {
            foreach (var a in Effects.ScriptCache.Get(Def).Abilities) yield return (a, this);
            foreach (var m in Modifiers) if (m.Kind == Effects.ModKind.Ability) yield return (m.Ability!, m.Granter ?? this);
            foreach (var g in Cont.Abilities) yield return g;
        }
    }

    /// <summary>效果给的东西的说明（界面显示用）：获得的能力、特殊效果、特征、等级修正。</summary>
    public IEnumerable<string> GainedDescriptions()
    {
        foreach (var m in Modifiers)
            if (m.Kind == Effects.ModKind.Ability) yield return "获得能力：" + (m.Ability!.Text ?? m.Ability.ToString());
        foreach (var (ab, _) in Cont.Abilities) yield return "获得能力：" + (ab.Text ?? ab.ToString());
        foreach (var s in Specials) yield return "效果：" + s;
        foreach (var t in Traits.Skip(Def.Traits.Count)) if (!t.StartsWith("__")) yield return "特征：" + t;
        if (Level != Def.Level) yield return $"等级 {Level}（原 {Def.Level}）";
    }

    internal void ResetTransient()
    {
        Orientation = CardOrientation.Standing;
        Modifiers.Clear();
        Cont.Clear();
        UsesThisTurn.Clear();
        TempLabels.Clear();
    }

    public override string ToString() => $"#{InstanceId} {Def.Name}";
}

/// <summary>一名玩家的全部区域。列表约定：Deck[0] 是牌库顶；Clock / Stock / Level / WaitingRoom 的末尾是最上面（最新放入）。</summary>
public sealed class PlayerState
{
    public int Index { get; }
    public string DeckName { get; }

    public List<CardInstance> Deck { get; } = new();
    public List<CardInstance> Hand { get; } = new();
    public List<CardInstance> Clock { get; } = new();
    public List<CardInstance> Level { get; } = new();
    public List<CardInstance> Stock { get; } = new();
    public List<CardInstance> WaitingRoom { get; } = new();
    public List<CardInstance> Memory { get; } = new();
    public List<CardInstance> ClimaxArea { get; } = new();
    public List<CardInstance> Resolution { get; } = new();
    public CardInstance?[] Stage { get; } = new CardInstance?[StageSlots.Count];

    /// <summary>已洗牌重整、尚未执行的重整伤害（牌库顶 1 张放到伤害区）次数。</summary>
    public int PendingRefreshPoints { get; internal set; }

    /// <summary>玩家级的有期限效果（BlockOpponentEncore 等，Kind = Special）。</summary>
    internal List<Effects.TimedModifier> Modifiers { get; } = new();
    public bool HasFlag(string name) => Modifiers.Any(m => string.Equals(m.Text, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>因规则败北（等级 4、需要重整时牌库和休息室都没有卡）。</summary>
    public bool HasLost { get; internal set; }
    public string? LossReason { get; internal set; }

    public PlayerState(int index, string deckName)
    {
        Index = index;
        DeckName = deckName;
    }

    public int PlayerLevel => Level.Count;

    public IEnumerable<CardInstance> StageCards => Stage.Where(c => c != null)!;

    public StageSlot? SlotOf(CardInstance card)
    {
        for (int i = 0; i < Stage.Length; i++)
            if (ReferenceEquals(Stage[i], card)) return (StageSlot)i;
        return null;
    }

    public List<CardInstance> ZoneList(ZoneType zone) => zone switch
    {
        ZoneType.Deck => Deck,
        ZoneType.Hand => Hand,
        ZoneType.Clock => Clock,
        ZoneType.Level => Level,
        ZoneType.Stock => Stock,
        ZoneType.WaitingRoom => WaitingRoom,
        ZoneType.Memory => Memory,
        ZoneType.ClimaxArea => ClimaxArea,
        ZoneType.Resolution => Resolution,
        _ => throw new ArgumentException($"{zone} 不是列表区域"),
    };

    /// <summary>等级区或伤害区是否有该颜色的卡（使用 1 级以上的卡和名场面的颜色条件）。</summary>
    /// <summary>按 InstanceId 找这名玩家的卡（任何区域）。</summary>
    public CardInstance? FindInstance(int instanceId)
    {
        foreach (var c in Stage)
        {
            if (c == null) continue;
            if (c.InstanceId == instanceId) return c;
            foreach (var m in c.Markers) if (m.InstanceId == instanceId) return m;
        }
        foreach (var z in new[] { Deck, Hand, Clock, Level, Stock, WaitingRoom, Memory, ClimaxArea, Resolution })
            foreach (var c in z) if (c.InstanceId == instanceId) return c;
        return null;
    }

    public bool HasColorInClockOrLevel(CardColor color) =>
        Clock.Any(c => c.Def.Color == color) || Level.Any(c => c.Def.Color == color);

    public int TotalCards =>
        Deck.Count + Hand.Count + Clock.Count + Level.Count + Stock.Count + WaitingRoom.Count +
        Memory.Count + ClimaxArea.Count + Resolution.Count + Stage.Sum(c => c == null ? 0 : 1 + c.Markers.Count(m => m.Owner == Index));
}

public sealed class GameState
{
    public PlayerState[] Players { get; }
    public int TurnNumber { get; internal set; }
    public int ActivePlayer { get; internal set; }
    public int FirstPlayer { get; internal set; } = -1;
    public Phase Phase { get; internal set; } = Phase.Setup;
    public int AttacksThisTurn { get; internal set; }

    public bool IsOver { get; internal set; }
    /// <summary>胜者（0 或 1）；平局或未结束为 null。</summary>
    public int? Winner { get; internal set; }
    public bool IsDraw { get; internal set; }
    public string? EndReason { get; internal set; }

    public GameState(PlayerState p0, PlayerState p1)
    {
        Players = new[] { p0, p1 };
    }

    /// <summary>这名玩家拥有的卡的总数（包括放在任何一方角色下面的标记）。</summary>
    public int TotalCardsOf(int player)
    {
        var p = Players[player];
        int n = p.Deck.Count + p.Hand.Count + p.Clock.Count + p.Level.Count + p.Stock.Count + p.WaitingRoom.Count +
                p.Memory.Count + p.ClimaxArea.Count + p.Resolution.Count + p.Stage.Count(c => c != null);
        foreach (var q in Players)
            foreach (var c in q.StageCards)
                n += c.Markers.Count(m => m.Owner == player);
        return n;
    }

    public PlayerState Active => Players[ActivePlayer];
    public PlayerState NonActive => Players[1 - ActivePlayer];
    public PlayerState Opponent(int player) => Players[1 - player];
}

using WsSim.Core.Cards;

namespace WsSim.Core.Game;

public enum ZoneType { Deck, Hand, Clock, Level, Stock, WaitingRoom, Memory, ClimaxArea, Resolution, Stage }

/// <summary>舞台格：前列 3 格（0–2），后列 2 格（3–4）。我方前列第 i 格正对对手前列第 i 格。</summary>
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

    /// <summary>“直到回合结束为止”的战力 / 魂修正（判定的魂图示、助太刀等）。离开舞台或回合结束时清零。</summary>
    public int PowerModifierUntilEndOfTurn { get; internal set; }
    public int SoulModifierUntilEndOfTurn { get; internal set; }

    internal CardInstance(int instanceId, CardDefinition def, int owner)
    {
        InstanceId = instanceId;
        Def = def;
        Owner = owner;
        Zone = ZoneType.Deck;
    }

    public int Power => Def.Power + PowerModifierUntilEndOfTurn;
    public int Soul => Math.Max(0, Def.Soul + SoulModifierUntilEndOfTurn);
    public int Level => Def.Level;

    internal void ResetTransient()
    {
        Orientation = CardOrientation.Standing;
        PowerModifierUntilEndOfTurn = 0;
        SoulModifierUntilEndOfTurn = 0;
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
    public bool HasColorInClockOrLevel(CardColor color) =>
        Clock.Any(c => c.Def.Color == color) || Level.Any(c => c.Def.Color == color);

    public int TotalCards =>
        Deck.Count + Hand.Count + Clock.Count + Level.Count + Stock.Count + WaitingRoom.Count +
        Memory.Count + ClimaxArea.Count + Resolution.Count + Stage.Count(c => c != null);
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

    public PlayerState Active => Players[ActivePlayer];
    public PlayerState NonActive => Players[1 - ActivePlayer];
    public PlayerState Opponent(int player) => Players[1 - player];
}

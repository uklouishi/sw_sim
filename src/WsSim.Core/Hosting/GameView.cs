using WsSim.Core.Cards;
using WsSim.Core.Game;

namespace WsSim.Core.Hosting;

/// <summary>
/// 一张卡在某一玩家视角下的样子。看不到正面的卡（对手手牌、能量区、牌库）<see cref="Def"/> 为 null，
/// <see cref="InstanceId"/> 为 -1（InstanceId 由开局卡组顺序决定，泄露它等于泄露是哪张卡）。
/// </summary>
public sealed record CardView(int InstanceId, CardDefinition? Def, CardOrientation Orientation, int Power, int Soul)
{
    public bool IsHidden => Def == null;

    /// <summary>下面的标记张数（舞台上的角色）。</summary>
    public int Markers { get; init; }
    /// <summary>效果给的能力、特殊效果、特征（详情里显示）。</summary>
    public IReadOnlyList<string> Gained { get; init; } = Array.Empty<string>();

    public static readonly CardView Hidden = new(-1, null, CardOrientation.Standing, 0, 0);

    public static CardView Of(CardInstance c) => new(c.InstanceId, c.Def, c.Orientation, c.Power, c.Soul)
    {
        Markers = c.Markers.Count,
        Gained = c.Zone == ZoneType.Stage ? c.GainedDescriptions().ToArray() : Array.Empty<string>(),
    };
}

public sealed record PlayerView(
    int Index,
    string DeckName,
    int PlayerLevel,
    int DeckCount,
    IReadOnlyList<CardView> Hand,
    IReadOnlyList<CardView> Clock,
    IReadOnlyList<CardView> Level,
    IReadOnlyList<CardView> Stock,
    IReadOnlyList<CardView> WaitingRoom,
    IReadOnlyList<CardView> Memory,
    IReadOnlyList<CardView> ClimaxArea,
    IReadOnlyList<CardView> Resolution,
    IReadOnlyList<CardView?> Stage,
    bool HasLost,
    string? LossReason);

/// <summary>
/// 某一玩家视角下的不可变局面快照。由引擎线程在等待选择时生成，界面线程可以随意读取。
/// </summary>
public sealed record GameView(
    int Viewer,
    int TurnNumber,
    int ActivePlayer,
    int FirstPlayer,
    Phase Phase,
    int AttacksThisTurn,
    IReadOnlyList<PlayerView> Players,
    IReadOnlyList<string> NewLog,
    bool IsOver,
    int? Winner,
    bool IsDraw,
    string? EndReason)
{
    public PlayerView Me => Players[Viewer];
    public PlayerView Opponent => Players[1 - Viewer];

    /// <summary>
    /// 生成快照。<paramref name="revealAll"/> 为 true 时双方手牌都可见（观战 / 调试）。
    /// 能量区在规则上背面朝上，双方都看不到；牌库只给张数。
    /// </summary>
    public static GameView Create(GameState s, int viewer, bool revealAll, IReadOnlyList<string> newLog)
    {
        PlayerView P(PlayerState p)
        {
            bool seeHand = revealAll || p.Index == viewer;
            static IReadOnlyList<CardView> Open(List<CardInstance> z) => z.Select(CardView.Of).ToArray();
            static IReadOnlyList<CardView> Closed(List<CardInstance> z) => z.Select(_ => CardView.Hidden).ToArray();
            return new PlayerView(
                p.Index, p.DeckName, p.PlayerLevel, p.Deck.Count,
                seeHand ? Open(p.Hand) : Closed(p.Hand),
                Open(p.Clock), Open(p.Level),
                revealAll ? Open(p.Stock) : Closed(p.Stock),
                Open(p.WaitingRoom), Open(p.Memory), Open(p.ClimaxArea), Open(p.Resolution),
                p.Stage.Select(c => c == null ? null : CardView.Of(c)).ToArray(),
                p.HasLost, p.LossReason);
        }

        return new GameView(viewer, s.TurnNumber, s.ActivePlayer, s.FirstPlayer, s.Phase, s.AttacksThisTurn,
            new[] { P(s.Players[0]), P(s.Players[1]) }, newLog,
            s.IsOver, s.Winner, s.IsDraw, s.EndReason);
    }

    /// <summary>按 InstanceId 在快照里找一张看得到的卡。</summary>
    public CardView? FindCard(int instanceId)
    {
        if (instanceId < 0) return null;
        foreach (var p in Players)
        {
            foreach (var z in new[] { p.Hand, p.Clock, p.Level, p.Stock, p.WaitingRoom, p.Memory, p.ClimaxArea, p.Resolution })
                foreach (var c in z)
                    if (c.InstanceId == instanceId) return c;
            foreach (var c in p.Stage)
                if (c != null && c.InstanceId == instanceId) return c;
        }
        return null;
    }
}

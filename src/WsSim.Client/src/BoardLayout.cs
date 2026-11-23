using Godot;
using WsSim.Core.Game;

namespace WsSim.Client;

/// <summary>
/// 场地版面：按 WS 实卡的场地布局（每名玩家从自己的座位看）：
/// <code>
///        能量        名场面  [前列 ① ② ③]       牌库   判定
///        (横放竖叠)          [后列  ④ ⑤ ]       休息室
///              等级   [ 伤害区 7 格 ]           思出
/// </code>
/// 前列靠近对手、后列在前列后面（④在①②之间，⑤在②③之间，综合规则 3.6.4/3.6.5）。
/// 我方在下半场；对手半场是我方半场绕场地中心旋转 180°（像真的面对面坐着），
/// 所以我方①（左）正对对手③（对手的右，在我看来也在左边），符合综合规则 3.6.6。
/// 所有坐标是场地设计尺寸 <see cref="Size"/> 里的像素。
/// </summary>
public static class BoardLayout
{
    public static readonly Vector2 Size = new(1200, 900);
    public static float CenterY => Size.Y / 2;

    public static readonly Vector2 StageCard = new(74, 104);
    public static readonly Vector2 StageCell = new(104, 104);
    public static readonly Vector2 PileCard = new(74, 104);
    public static readonly Vector2 SmallCard = new(50, 70);
    /// <summary>横放的小卡（能量、等级）占的格子。</summary>
    public static readonly Vector2 SideCell = new(70, 50);
    public static readonly Vector2 HandCard = new(86, 120);
    public static readonly Vector2 OppHandSize = new(56, 78);

    public const float StockStep = 9;
    public const float LevelStep = 14;
    public const float ClockStep = 54;

    // ---------------------------------------------------------------- 我方半场（下半）的格子

    private static float Y(float dy) => CenterY + dy;

    private static readonly float FrontY = Y(12);
    private static readonly float BackY = Y(122);
    private static readonly float BottomY = Y(234);
    private const float StageStep = 116;
    private static readonly float StageX0 = Size.X / 2 - StageCell.X / 2 - StageStep;

    private static Rect2 MyStage(StageSlot s) => s switch
    {
        StageSlot.FrontLeft => new Rect2(StageX0, FrontY, StageCell),
        StageSlot.FrontCenter => new Rect2(StageX0 + StageStep, FrontY, StageCell),
        StageSlot.FrontRight => new Rect2(StageX0 + StageStep * 2, FrontY, StageCell),
        StageSlot.BackLeft => new Rect2(StageX0 + StageStep / 2, BackY, StageCell),
        _ => new Rect2(StageX0 + StageStep * 1.5f, BackY, StageCell),
    };

    private static Rect2 MyClimax => new(StageX0 - 24 - StageCell.X, FrontY + (StageCell.Y - StageCard.X) / 2, StageCell.X, StageCard.X);

    /// <summary>能量区整个区域（竖着往下叠）。</summary>
    private static Rect2 MyStockArea => new(MyClimax.Position.X - 30 - SideCell.X - 8, FrontY - 2, SideCell.X + 16, 212);

    private static Rect2 MyClockArea => new(Size.X / 2 - ClockStep * 3.5f, BottomY, ClockStep * 7, SmallCard.Y);

    private static Rect2 MyLevelArea => new(MyClockArea.Position.X - 24 - SideCell.X - 8, BottomY - 8, SideCell.X + 16, SmallCard.Y + 16);

    private static float RightX => StageX0 + StageStep * 2 + StageCell.X + 70;

    private static Rect2 MyDeck => new(RightX, FrontY, PileCard);
    private static Rect2 MyWaitingRoom => new(RightX, BackY, PileCard);
    private static Rect2 MyMemory => new(RightX + (PileCard.X - SmallCard.X) / 2, BottomY, SmallCard);
    private static Rect2 MyResolution => new(RightX + PileCard.X + 24, FrontY, PileCard.X + 40, PileCard.Y);

    /// <summary>玩家信息牌（名字、等级、伤害）。</summary>
    private static Rect2 MyInfo => new(16, BottomY - 10, 210, 86);

    // ---------------------------------------------------------------- 对外

    /// <summary>bottom = 这名玩家坐在下方（界面视角的玩家）。</summary>
    public static Rect2 Rotate(Rect2 r, bool bottom) =>
        bottom ? r : new Rect2(Size - r.Position - r.Size, r.Size);

    public static Rect2 Stage(StageSlot s, bool bottom) => Rotate(MyStage(s), bottom);
    public static Rect2 Climax(bool bottom) => Rotate(MyClimax, bottom);
    public static Rect2 StockArea(bool bottom) => Rotate(MyStockArea, bottom);
    public static Rect2 ClockArea(bool bottom) => Rotate(MyClockArea, bottom);
    public static Rect2 LevelArea(bool bottom) => Rotate(MyLevelArea, bottom);
    public static Rect2 Deck(bool bottom) => Rotate(MyDeck, bottom);
    public static Rect2 WaitingRoom(bool bottom) => Rotate(MyWaitingRoom, bottom);
    public static Rect2 Memory(bool bottom) => Rotate(MyMemory, bottom);
    public static Rect2 Resolution(bool bottom) => Rotate(MyResolution, bottom);
    public static Rect2 Info(bool bottom) => Rotate(MyInfo, bottom);

    /// <summary>能量区第 i 张（0 = 最下面那张，最早放入）。我方从上往下叠，对手（旋转后）从下往上叠。</summary>
    public static Rect2 StockCard(int i, int count, bool bottom)
    {
        var area = MyStockArea;
        float step = count <= 1 ? 0 : Math.Min(StockStep, (area.Size.Y - SideCell.Y - 8) / (count - 1));
        var r = new Rect2(area.Position.X + 8, area.Position.Y + 4 + i * step, SideCell);
        return Rotate(r, bottom);
    }

    /// <summary>等级区第 i 张（横放，往下叠）。</summary>
    public static Rect2 LevelCard(int i, bool bottom)
    {
        var area = MyLevelArea;
        var r = new Rect2(area.Position.X + 8, area.Position.Y + 6 + i * LevelStep, SideCell);
        return Rotate(r, bottom);
    }

    /// <summary>伤害区第 i 格（0 = 最早的一张，从左往右）。</summary>
    public static Rect2 ClockCard(int i, bool bottom)
    {
        var area = MyClockArea;
        var r = new Rect2(area.Position.X + i * ClockStep + (ClockStep - SmallCard.X) / 2, area.Position.Y, SmallCard);
        return Rotate(r, bottom);
    }

    /// <summary>判定区（结算区）第 i 张。</summary>
    public static Rect2 ResolutionCard(int i, int count, bool bottom)
    {
        var area = MyResolution;
        float step = count <= 1 ? 0 : Math.Min(14, (area.Size.X - PileCard.X) / (count - 1));
        return Rotate(new Rect2(area.Position.X + i * step, area.Position.Y, PileCard), bottom);
    }

    /// <summary>我方手牌第 i 张（底部居中排开）。</summary>
    public static Rect2 MyHandCard(int i, int count)
    {
        float maxWidth = 980;
        float step = count <= 1 ? 0 : Math.Min(HandCard.X + 8, (maxWidth - HandCard.X) / (count - 1));
        float total = step * (count - 1) + HandCard.X;
        return new Rect2(Size.X / 2 - total / 2 + i * step, Size.Y - HandCard.Y - 6, HandCard);
    }

    /// <summary>对手手牌第 i 张（顶部，只露出下半截）。</summary>
    public static Rect2 OppHandCard(int i, int count)
    {
        float maxWidth = 520;
        float step = count <= 1 ? 0 : Math.Min(OppHandSize.X * 0.6f, (maxWidth - OppHandSize.X) / (count - 1));
        float total = step * (count - 1) + OppHandSize.X;
        // 从对手的角度看是从左往右，所以在我看来是从右往左。
        return new Rect2(Size.X / 2 + total / 2 - OppHandSize.X - i * step, -OppHandSize.Y * 0.35f, OppHandSize);
    }

    public static Rect2 HandArea(bool bottom) => bottom
        ? new Rect2(Size.X / 2 - 490, Size.Y - HandCard.Y - 6, 980, HandCard.Y)
        : new Rect2(Size.X / 2 - 260, 0, 520, OppHandSize.Y * 0.65f);
}

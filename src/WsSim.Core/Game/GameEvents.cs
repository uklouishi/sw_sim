namespace WsSim.Core.Game;

/// <summary>
/// 引擎在局面每一步变化时发出的事件（给界面播动画、给回放 / 观战用）。
/// 事件只描述发生了什么，不影响规则；不订阅时引擎行为完全一样。
/// 事件里带的是真实的 <see cref="InstanceId"/>，交给界面之前要按视角隐藏（见 Hosting/GameHost）。
/// </summary>
public abstract record GameEvent;

/// <summary>
/// 一张卡从一个区域移到另一个区域。<see cref="FromIndex"/> 是移动前在原区域列表里的位置，
/// <see cref="ToIndex"/> 是移动后在新区域列表里的位置（舞台用 Slot，Index 为格子编号）。
/// </summary>
public sealed record CardMovedEvent(
    int Owner, int InstanceId,
    ZoneType From, int FromIndex, StageSlot? FromSlot,
    ZoneType To, int ToIndex, StageSlot? ToSlot,
    CardOrientation Orientation) : GameEvent;

/// <summary>舞台上一张卡的方向改变（正置 / 横置 / 倒置）。</summary>
public sealed record CardOrientationEvent(int Owner, int InstanceId, StageSlot Slot, CardOrientation From, CardOrientation To) : GameEvent;

/// <summary>洗牌（开局、洗牌重整）。</summary>
public sealed record ShuffleEvent(int Owner) : GameEvent;

/// <summary>攻击宣告。</summary>
public sealed record AttackDeclaredEvent(int Attacker, StageSlot AttackerSlot, AttackType Type, StageSlot? DefenderSlot) : GameEvent;

/// <summary>阶段开始（含回合开始的正置阶段）。</summary>
public sealed record PhaseEvent(int ActivePlayer, int TurnNumber, Phase Phase) : GameEvent;

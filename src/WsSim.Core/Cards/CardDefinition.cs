namespace WsSim.Core.Cards;

public enum CardType { Character, Event, Climax }

public enum CardColor { None, Yellow, Green, Red, Blue }

/// <summary>判定图示。数据里的少数特殊图示（Focus、Telescope 等）归为 Other，P1 不处理。</summary>
public enum TriggerIcon { Soul, DoubleSoul, Wind, Bag, Door, Book, Gate, Shot, Standby, Choice, Bar, Other }

/// <summary>一张卡的静态数据（来自 cards.json），对局中不可变。</summary>
public sealed class CardDefinition
{
    public required string Id { get; init; }
    public required CardType Type { get; init; }
    public required string Name { get; init; }
    public string? JpName { get; init; }
    public CardColor Color { get; init; }
    public int Level { get; init; }
    public int Cost { get; init; }
    public int Power { get; init; }
    public int Soul { get; init; }
    public IReadOnlyList<TriggerIcon> Triggers { get; init; } = Array.Empty<TriggerIcon>();
    /// <summary>判定图示原文（包括无法识别的）。</summary>
    public IReadOnlyList<string> RawTriggers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Traits { get; init; } = Array.Empty<string>();
    public string? Combo { get; init; }
    public IReadOnlyList<string> Series { get; init; } = Array.Empty<string>();
    public string? Image { get; init; }
    public IReadOnlyList<string> TextsEn { get; init; } = Array.Empty<string>();
    /// <summary>已展开宏的效果脚本（P2 由解释器执行；P1 只从中读取 Backup / Counter 等固定字段）。</summary>
    public string ScriptExpanded { get; init; } = "";
    /// <summary>【助太刀】的战力数值（脚本里的 <c>Backup 2500</c>），没有则为 null。</summary>
    public int? BackupPower { get; init; }
    /// <summary>反击事件（脚本里的 <c>Counter</c>）。</summary>
    public bool IsCounter { get; init; }

    /// <summary>Clone 卡：视为这个卡号的卡（字段已沿用）。</summary>
    public string? CloneOf { get; init; }

    public bool IsCharacter => Type == CardType.Character;
    public bool IsEvent => Type == CardType.Event;
    public bool IsClimax => Type == CardType.Climax;

    /// <summary>判定图示里的魂图示数（Soul +1，DoubleSoul +2）。</summary>
    public int SoulTriggerCount => Triggers.Sum(t => t == TriggerIcon.Soul ? 1 : t == TriggerIcon.DoubleSoul ? 2 : 0);

    public override string ToString() => $"{Id} {Name}";
}

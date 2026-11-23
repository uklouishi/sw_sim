using WsSim.Core.Game;

namespace WsSim.Core.Effects;

public enum ModKind { Power, Soul, Level, Cost, Special, Trait, Ability, Trigger, Name }

/// <summary>效果给的有期限修正。<see cref="ExpiresAfterTurn"/> 回合的结束阶段移除（“直到回合结束” = 当前回合，“直到对手的回合结束” = 下一回合）。</summary>
public sealed class TimedModifier
{
    public required ModKind Kind { get; init; }
    public int Value { get; init; }
    public string? Text { get; init; }
    public Ability? Ability { get; init; }
    public CardInstance? Granter { get; init; }
    public int ExpiresAfterTurn { get; init; }
}

/// <summary>【永】效果算出来的修正。每次重新计算前清空。</summary>
public sealed class ContinuousLayer
{
    public int Power, Soul, Level, Cost;
    public readonly HashSet<string> Specials = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Traits = new();
    public readonly List<string> Triggers = new();
    public readonly List<(Ability Ability, CardInstance Granter)> Abilities = new();

    public void Clear()
    {
        Power = Soul = Level = Cost = 0;
        Specials.Clear();
        Traits.Clear();
        Triggers.Clear();
        Abilities.Clear();
    }
}

/// <summary>触发时记下的信息（“那张卡”、战斗对手等）。</summary>
public sealed class TriggerInfo
{
    public string Trigger { get; init; } = "";
    /// <summary>另一张相关的卡：OnOther* 里的那张卡、OnPlay 时被替换的卡等。</summary>
    public CardInstance? Other { get; init; }
    /// <summary>名场面相关时机里的名场面。</summary>
    public CardInstance? Climax { get; init; }
    /// <summary>OnPlay|PlacedBy 等的放置方式（Change 等）。</summary>
    public string? PlacedBy { get; init; }
    /// <summary>离开舞台前的格子。</summary>
    public StageSlot? FromSlot { get; init; }
}

/// <summary>执行一个能力（或其中一段）时的状态。</summary>
public sealed class EffectContext
{
    /// <summary>拥有这个能力的卡。</summary>
    public required CardInstance Source { get; init; }
    /// <summary>“这张卡”：通常就是 Source；GiveAll / Assist 时是受影响的那张卡。</summary>
    public required CardInstance Self { get; set; }
    public required int Controller { get; init; }
    public Ability? Ability { get; init; }
    public TriggerInfo Trigger { get; init; } = new();

    /// <summary>【永】模式：动作写进 Cont 层，不提问。</summary>
    public bool Continuous { get; init; }
    /// <summary>【永】里的 GainPower 等总是给“这张卡”（Self），目标只用来计数和判断（GiveSelf / GiveAll / Assist…）。
    /// Global / Memory 类型给目标。</summary>
    public bool ApplyToSelf { get; init; }

    public List<CardInstance> Targets { get; set; } = new();
    /// <summary>是否明确指定过目标（没指定时动作作用于 Self）。</summary>
    public bool HasTargets { get; set; }
    public List<CardInstance> Looked { get; } = new();
    public List<CardInstance> Saved { get; set; } = new();
    public Stack<List<CardInstance>> Pushed { get; } = new();
    public List<CardInstance> Brainstormed { get; } = new();
    public int BrainstormClimax { get; set; }
    public CardInstance? ThatCard { get; set; }
    public int X { get; set; }
    public Stack<int> XStack { get; } = new();
    public bool LastYes { get; set; }
    public int Answer { get; set; } = -1;
    public bool UntilOpponentsTurn { get; set; }
    public bool ShuffleDeckAtEnd { get; set; }
    public StageSlot? RememberedSlot { get; set; }
    /// <summary>最近一次给予的伤害是否被取消。</summary>
    public bool LastDamageCanceled { get; set; }

    public EffectContext Derive(CardInstance self) => new()
    {
        Source = Source, Self = self, Controller = Controller, Ability = Ability, Trigger = Trigger, Continuous = Continuous, ApplyToSelf = ApplyToSelf,
    };
}

/// <summary>已触发、等待处理的【自】能力。</summary>
public sealed class PendingAbility
{
    public required CardInstance Source { get; init; }
    public required Ability Ability { get; init; }
    public required int Controller { get; init; }
    public required TriggerInfo Info { get; init; }
    /// <summary>预约效果（ScheduleEffectForTargets）带着的目标。</summary>
    public List<CardInstance>? Targets { get; init; }
    public CardInstance? Self { get; init; }
    public override string ToString() => $"{Source.Def.Name}：{Ability.Text ?? Ability.ToString()}";
}

/// <summary>预约在某个时机执行的效果（ScheduleEffect / ScheduleEffectForTargets）。</summary>
public sealed class ScheduledEffect
{
    public required string Timing { get; init; }
    public required CardInstance Source { get; init; }
    public required int Controller { get; init; }
    public required List<Ability> Abilities { get; init; }
    public List<CardInstance>? Targets { get; init; }
    public int CreatedTurn { get; init; }
}

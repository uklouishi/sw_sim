namespace WsSim.Core.Game;

public enum DecisionType
{
    /// <summary>猜拳胜者选择先攻或后攻。</summary>
    ChooseFirstPlayer,
    /// <summary>开局换牌：选任意张放入休息室。</summary>
    Mulligan,
    /// <summary>伤害阶段：选 0–1 张手牌放到伤害区。</summary>
    Clock,
    /// <summary>主要阶段：选一个行动（使用角色、使用事件、移动、结束）。</summary>
    MainAction,
    /// <summary>名场面阶段：选 0–1 张名场面。</summary>
    Climax,
    /// <summary>攻击阶段：选择攻击角色与攻击方式，或结束攻击。</summary>
    AttackDeclaration,
    /// <summary>反击步骤：防守方选择助太刀或不使用。</summary>
    Counter,
    /// <summary>判定效果等“可以”选择的是/否。</summary>
    YesNo,
    /// <summary>从若干卡中选择（判定效果的目标等）。</summary>
    ChooseCard,
    /// <summary>选择舞台格。</summary>
    ChooseSlot,
    /// <summary>升级：从伤害区下方 7 张中选 1 张放到等级区。</summary>
    LevelUp,
    /// <summary>结束阶段：手牌超过上限时选择弃掉的卡。</summary>
    DiscardToHandLimit,
    /// <summary>再演步骤：是否支付规则再演的费用。</summary>
    Encore,
    /// <summary>箭头（Choice）判定：选择加入手牌还是放到能量区。</summary>
    ChoiceDestination,
    /// <summary>效果：从若干卡中选多张（个数见 MinPicks / MaxPicks）。</summary>
    ChooseCards,
    /// <summary>效果：多选一（MultipleChoiceQuestion 等）。</summary>
    ChooseOption,
    /// <summary>同时有多个【自】能力待处理：选先处理哪个。</summary>
    ChooseAbility,
}

public enum MainActionKind { End, PlayCharacter, PlayEvent, Move, Activate }

/// <summary>一个可选项。界面显示 Label；其余字段供 AI / 界面识别。</summary>
public sealed record DecisionOption(string Label)
{
    public int? CardInstanceId { get; init; }
    /// <summary>选项对应的卡的定义（牌库里查看的卡等界面上看不到的卡，用来显示详情）。</summary>
    public Cards.CardDefinition? Card { get; init; }
    public StageSlot? Slot { get; init; }
    public StageSlot? TargetSlot { get; init; }
    public MainActionKind? MainAction { get; init; }
    public AttackType? AttackType { get; init; }
    /// <summary>发动【起】时：能力在这张卡的能力列表里的序号。</summary>
    public int? AbilityIndex { get; init; }
    /// <summary>“不做 / 结束 / 跳过”类选项。</summary>
    public bool IsPass { get; init; }
}

/// <summary>
/// 引擎向玩家提出的选择。回答是选中的选项序号数组（互不相同，个数在 [MinPicks, MaxPicks] 内）。
/// 回答只含序号，可直接序列化后通过网络传给对方，或记录成回放。
/// </summary>
public sealed record Decision(int Player, DecisionType Type, string Prompt, IReadOnlyList<DecisionOption> Options, int MinPicks, int MaxPicks)
{
    public int Sequence { get; init; }

    public bool IsValidAnswer(IReadOnlyList<int> answer)
    {
        if (answer.Count < MinPicks || answer.Count > MaxPicks) return false;
        var seen = new HashSet<int>();
        foreach (int a in answer)
            if (a < 0 || a >= Options.Count || !seen.Add(a)) return false;
        return true;
    }
}

/// <summary>做出选择的一方：界面、AI、网络对手、回放都实现这个接口。</summary>
public interface IPlayerAgent
{
    int[] Decide(GameState state, Decision decision);
}

/// <summary>一次已做出的选择（回放日志的一条）。</summary>
public sealed record RecordedDecision(int Sequence, int Player, DecisionType Type, int[] Answer);

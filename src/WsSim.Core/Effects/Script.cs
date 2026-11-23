using WsSim.Core.Cards;

namespace WsSim.Core.Effects;

/// <summary>
/// 效果脚本的一行：指令名 + 参数（原样字符串），后面可以跟一个 <c>{ }</c> 块。
/// 指令名不区分大小写，<see cref="Key"/> 是小写形式，用来分派。
/// </summary>
public sealed class ScriptNode
{
    public required string Name { get; init; }
    public string Args { get; init; } = "";
    public List<ScriptNode>? Block { get; set; }
    public int Line { get; init; }
    public string Key => Name.ToLowerInvariant();
    public override string ToString() => Args.Length > 0 ? $"{Name} {Args}" : Name;
}

public enum AbilityKind
{
    /// <summary>【自】</summary>
    Auto,
    /// <summary>【永】</summary>
    Cont,
    /// <summary>【起】</summary>
    Act,
    /// <summary>事件卡的效果（<c>Effect:</c>）。</summary>
    Event,
}

/// <summary>
/// 一个能力：<c>Auto: 时机 参数</c> / <c>Cont: 类型 参数</c> / <c>Act: 费用</c> / <c>Effect:</c> + 块，
/// 以及跟在块后面的修饰（<c>TimesPerTurn</c>、<c>Text</c>、<c>ActivationRequirement</c> 等）。
/// </summary>
public sealed class Ability
{
    public required AbilityKind Kind { get; init; }
    /// <summary>Auto 的时机 / Cont 的类型（原样大小写），Act 和 Event 为空。</summary>
    public string Trigger { get; init; } = "";
    /// <summary>头里时机之后的参数（Auto: OnSpecificClimaxPlay 后面的名场面名、Act 的费用等）。</summary>
    public string HeaderArgs { get; init; } = "";
    public List<ScriptNode> Body { get; init; } = new();
    /// <summary>块后面属于这个能力的修饰行。</summary>
    public List<ScriptNode> Modifiers { get; } = new();
    public string? Text { get; set; }
    /// <summary>在卡上的序号（同一张卡的第几个能力，用于“每回合 N 次”计数和界面显示）。</summary>
    public int Index { get; set; }

    public string TriggerKey => Trigger.ToLowerInvariant();

    /// <summary>Auto 的时机可能用 | 连接多个（OnPlay|OnAttack）。</summary>
    public IEnumerable<string> TriggerKeys => TriggerKey.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int? TimesPerTurn
    {
        get
        {
            var m = Modifiers.FirstOrDefault(n => n.Key == "timesperturn");
            return m != null && int.TryParse(m.Args.Split(' ')[0], out var n) ? n : null;
        }
    }

    public bool HasModifier(string key) => Modifiers.Any(n => n.Key == key);

    public override string ToString() => Kind switch
    {
        AbilityKind.Auto => $"Auto: {Trigger} {HeaderArgs}".TrimEnd(),
        AbilityKind.Cont => $"Cont: {Trigger} {HeaderArgs}".TrimEnd(),
        AbilityKind.Act => $"Act: {HeaderArgs}".TrimEnd(),
        _ => "Effect:",
    };
}

/// <summary>一张卡解析后的脚本。</summary>
public sealed class CardScript
{
    public static readonly CardScript Empty = new();

    public List<Ability> Abilities { get; } = new();
    /// <summary>卡片级的标记行（Tag、Backup、Counter、Quick、Requirement、AINote…）。</summary>
    public List<ScriptNode> Flags { get; } = new();
    /// <summary>解析时发现的问题（括号不配对等），不影响能执行的部分。</summary>
    public List<string> Problems { get; } = new();

    public IEnumerable<Ability> Of(AbilityKind kind) => Abilities.Where(a => a.Kind == kind);

    public ScriptNode? Flag(string key) => Flags.FirstOrDefault(f => f.Key == key);

    /// <summary>事件卡的使用条件（<c>Requirement 条件</c>）。</summary>
    public string? Requirement => Flag("requirement")?.Args;
}

/// <summary>按卡号缓存解析结果（脚本不会在对局中变化）。线程安全。</summary>
public static class ScriptCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CardDefinition, CardScript> Cache =
        new(ReferenceEqualityComparer.Instance);

    public static CardScript Get(CardDefinition def) =>
        Cache.GetOrAdd(def, d => string.IsNullOrWhiteSpace(d.ScriptExpanded) ? CardScript.Empty : ScriptParser.Parse(d.ScriptExpanded));
}

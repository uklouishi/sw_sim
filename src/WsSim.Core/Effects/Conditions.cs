using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WsSim.Core.Effects;

/// <summary>
/// 条件表达式（If / NarrowTargets / Requirement 等的参数）。语法见 spec/dsl-grammar.md §3：
/// 先按 <c>&amp;&amp;&amp;</c> 切成几段（全部成立），每段里 <c>&amp;&amp;</c> 比 <c>||</c> 优先；
/// <c>StageContains n 过滤</c> 这类带过滤条件的原子把这一段剩下的部分都当作自己的过滤条件。
/// </summary>
public abstract class Cond
{
    public static readonly Cond True = new AtomCond("Any", "", null);
}

public sealed class AndCond(List<Cond> parts) : Cond
{
    public List<Cond> Parts { get; } = parts;
    public override string ToString() => string.Join(" && ", Parts);
}

public sealed class OrCond(List<Cond> parts) : Cond
{
    public List<Cond> Parts { get; } = parts;
    public override string ToString() => string.Join(" || ", Parts);
}

public sealed class NotCond(Cond inner) : Cond
{
    public Cond Inner { get; } = inner;
    public override string ToString() => "Not " + Inner;
}

/// <summary>一个判定。<see cref="Args"/> 是名字后面的原文（不含过滤条件），<see cref="Filter"/> 是带过滤条件的原子的过滤部分。</summary>
public sealed class AtomCond(string name, string args, Cond? filter) : Cond
{
    public string Name { get; } = name;
    public string Key { get; } = name.ToLowerInvariant();
    public string Args { get; } = args;
    public Cond? Filter { get; } = filter;
    public override string ToString() => Filter != null ? $"{Name} {Args} [{Filter}]" : $"{Name} {Args}".TrimEnd();
}

public static partial class CondParser
{
    private static readonly ConcurrentDictionary<string, Cond> Cache = new();

    /// <summary>后面跟“数量 + 过滤条件”的原子（过滤条件吃掉这一段剩下的部分）。</summary>
    private static readonly HashSet<string> FilterAtoms = new(StringComparer.OrdinalIgnoreCase)
    {
        "StageContains", "StageContans", "StageContainsOrLess", "StageContains_Other", "Have", "Have_Other",
        "LocationContains", "LocationContainsOrLess", "AllYourCharacters", "Facing", "OpponentStageContains",
        "BattleOpponent",
    };

    /// <summary>已知的原子名（用来判断 " | " 是“或”还是名字里的分隔）。</summary>
    public static readonly HashSet<string> KnownAtoms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Any", "Yes", "No", "Answer", "HasTarget", "Other", "MyTurn", "OpponentsTurn", "HasTrait", "NameEquals",
        "PrintedNameEquals", "NameContains", "NameIncludes", "IsCharacter", "IsClimax", "IsEvent", "IsColor", "ColorIs", "HasTrigger",
        "CardPosition", "OnStage", "InLocation", "Climax", "Compare", "HasMarker", "InBattle", "CanBeChosen", "CanMove",
        "PlayedThisTurn", "Phase", "MyCard", "HasTempLabel", "With", "LastDamageCanceled", "HaveColor",
        "SameNameAsSavedTarget", "Not", "Level", "IsSelf", "Stand", "Rest", "Reverse", "FaceDown", "FaceUp", "If", "InSavedTargets", "TargetsContain",
    };

    static CondParser()
    {
        foreach (var a in FilterAtoms) KnownAtoms.Add(a);
    }

    public static Cond Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Cond.True;
        return Cache.GetOrAdd(text, ParseUncached);
    }

    [GeneratedRegex(@"(?<!\|)\s+\|\s+(?!\|)")]
    private static partial Regex SpacedBar();

    private static Cond ParseUncached(string text)
    {
        // " | " 后面是一个原子名时是“或”，否则是“A | B”这种名字列表。
        var s = SpacedBar().Replace(text.Trim().Replace("|||", "||"), m =>
        {
            int after = m.Index + m.Length;
            var rest = m.Result("$'");
            var word = rest.Split(' ', 2)[0];
            return KnownAtoms.Contains(word) ? " || " : "|";
        });
        var segs = s.Split("&&&", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 1) return ParseSegment(segs[0]);
        return new AndCond(segs.Select(ParseSegment).ToList());
    }

    /// <summary>解析一段（没有 &amp;&amp;&amp;）。</summary>
    public static Cond ParseSegment(string seg)
    {
        // 切成 (运算符, 原子文本)，第一项的运算符为空。
        var parts = new List<(string Op, string Text)>();
        int i = 0, start = 0;
        string op = "";
        while (i < seg.Length)
        {
            if (i + 1 < seg.Length && ((seg[i] == '&' && seg[i + 1] == '&') || (seg[i] == '|' && seg[i + 1] == '|')))
            {
                parts.Add((op, seg[start..i].Trim()));
                op = seg.Substring(i, 2);
                i += 2;
                start = i;
                continue;
            }
            i++;
        }
        parts.Add((op, seg[start..].Trim()));

        // 带过滤条件的原子把后面的全部吃掉。
        var atoms = new List<(string Op, Cond C)>();
        for (int k = 0; k < parts.Count; k++)
        {
            var (o, t) = parts[k];
            if (t.Length == 0) continue;
            bool neg = false;
            while (true)
            {
                if (t.StartsWith("Not ", StringComparison.OrdinalIgnoreCase)) { neg = !neg; t = t[4..].TrimStart(); continue; }
                if (t.StartsWith("If ", StringComparison.OrdinalIgnoreCase)) { t = t[3..].TrimStart(); continue; }
                break;
            }
            var name = t.Split(' ', 2)[0];
            if (FilterAtoms.Contains(name))
            {
                var restParts = parts.Skip(k + 1).Select(p => $"{p.Op} {p.Text}");
                var full = string.Join(" ", new[] { t }.Concat(restParts));
                var c = MakeFilterAtom(full);
                atoms.Add((o, neg ? new NotCond(c) : c));
                break;
            }
            var atom = MakeAtom(t);
            atoms.Add((o, neg ? new NotCond(atom) : atom));
        }
        if (atoms.Count == 0) return Cond.True;
        if (atoms.Count == 1) return atoms[0].C;

        // && 优先于 ||
        var ors = new List<Cond>();
        var cur = new List<Cond>();
        foreach (var (o, c) in atoms)
        {
            if (o == "||" && cur.Count > 0)
            {
                ors.Add(cur.Count == 1 ? cur[0] : new AndCond(cur));
                cur = new List<Cond>();
            }
            cur.Add(c);
        }
        ors.Add(cur.Count == 1 ? cur[0] : new AndCond(cur));
        return ors.Count == 1 ? ors[0] : new OrCond(ors);
    }

    public static bool IsCompareOp(string w) => w.ToLowerInvariant() is "ormore" or "orhigher" or "orgreater" or "orless" or "orlower"
        or "orfewer" or "exactly" or "equals" or "greaterthan" or "morethan" or "lessthan" or "fewerthan";

    private static AtomCond MakeAtom(string t)
    {
        int sp = t.IndexOf(' ');
        return sp < 0 ? new AtomCond(t, "", null) : new AtomCond(t[..sp], t[(sp + 1)..].Trim(), null);
    }

    /// <summary>
    /// StageContains n 过滤 / LocationContains 区域 n 过滤 / AllYourCharacters 过滤 / Facing 过滤：
    /// Args 是数量（和区域），Filter 是剩下的部分。
    /// </summary>
    private static AtomCond MakeFilterAtom(string full)
    {
        var w = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = w[0];
        int argCount = name.ToLowerInvariant() switch
        {
            "locationcontains" or "locationcontainsorless" => 2,
            "allyourcharacters" or "facing" or "battleopponent" => 0,
            _ => 1,
        };
        argCount = Math.Min(argCount, w.Length - 1);
        // "LocationContains OpponentStage 1 OrMore Any"：数量后面跟比较方式。
        if (argCount > 0 && 1 + argCount < w.Length && IsCompareOp(w[1 + argCount])) argCount++;
        var args = string.Join(" ", w.Skip(1).Take(argCount));
        var filterText = string.Join(" ", w.Skip(1 + argCount));
        // 剩下的部分可能以运算符开头（"Facing && X" 写法），去掉。
        filterText = filterText.TrimStart('&', '|', ' ');
        return new AtomCond(name, args, filterText.Length == 0 ? Cond.True : ParseSegment(filterText));
    }
}

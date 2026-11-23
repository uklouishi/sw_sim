namespace WsSim.Core.Effects;

/// <summary>
/// 把效果脚本文本解析成 <see cref="CardScript"/>（语法见开发包 spec/dsl-grammar.md）：
/// 一行一条指令，<c>{</c> <c>}</c> 各占一行包住块；第一层的 <c>Auto:</c> / <c>Cont:</c> / <c>Act:</c> / <c>Effect:</c>
/// 开始一个能力，块后面的 <c>TimesPerTurn</c>、<c>Text</c> 等属于这个能力，其余第一层的行是卡片级标记。
/// 解析从不抛异常：括号不配对等问题记到 <see cref="CardScript.Problems"/>。
/// </summary>
public static class ScriptParser
{
    /// <summary>跟在能力块后面、属于该能力的修饰。</summary>
    private static readonly HashSet<string> AbilityModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "TimesPerTurn", "Text", "ActivationRequirement", "ActivationRequirement_Other", "UntilEndOfOpponentsTurn",
        "RefundTimesPerTurn",
    };

    public static CardScript Parse(string text)
    {
        var script = new CardScript();
        var lines = text.Replace("\r", "").Split('\n');
        int pos = 0;
        var top = ParseBlock(lines, ref pos, script, topLevel: true);
        BuildAbilities(top, script.Abilities, script.Flags);
        for (int i = 0; i < script.Abilities.Count; i++) script.Abilities[i].Index = i;
        return script;
    }

    /// <summary>解析一个 GainEffect 块里的能力（块的内容和卡片第一层的写法一样）。</summary>
    public static List<Ability> ParseAbilities(List<ScriptNode> nodes)
    {
        var list = new List<Ability>();
        BuildAbilities(nodes, list, new List<ScriptNode>());
        return list;
    }

    private static string StripComment(string line)
    {
        // 行尾 // 注释（引号里的不算；脚本里的卡名不会含 //）。
        int i = line.IndexOf("//", StringComparison.Ordinal);
        return (i >= 0 ? line[..i] : line).Trim();
    }

    private static List<ScriptNode> ParseBlock(string[] lines, ref int pos, CardScript script, bool topLevel)
    {
        var nodes = new List<ScriptNode>();
        while (pos < lines.Length)
        {
            var t = StripComment(lines[pos]);
            int lineNo = pos + 1;
            pos++;
            if (t.Length == 0) continue;
            if (t == "}")
            {
                if (topLevel) script.Problems.Add($"第 {lineNo} 行：多余的 }}");
                else return nodes;
                continue;
            }
            if (t.StartsWith('{'))
            {
                // 块跟在上一条后面。
                var rest = t[1..].Trim();
                var block = ParseBlock(lines, ref pos, script, topLevel: false);
                if (rest.Length > 0) block.Insert(0, MakeNode(rest, lineNo));
                if (nodes.Count > 0 && nodes[^1].Block == null) nodes[^1].Block = block;
                else
                {
                    // 没有指令的块（极少见）：当作无名指令。
                    nodes.Add(new ScriptNode { Name = "Block", Line = lineNo, Block = block });
                }
                continue;
            }
            // 同一行里的 "X {" 写法
            if (t.EndsWith('{') && t.Length > 1)
            {
                nodes.Add(MakeNode(t[..^1].Trim(), lineNo));
                nodes[^1].Block = ParseBlock(lines, ref pos, script, topLevel: false);
                continue;
            }
            nodes.Add(MakeNode(t, lineNo));
        }
        if (!topLevel) script.Problems.Add("缺少 }");
        return nodes;
    }

    private static ScriptNode MakeNode(string t, int line)
    {
        // 能力头 "Auto: OnPlay" / "Act: 1 RestSelf" / "Effect:"：名字包含冒号。
        int colon = t.IndexOf(':');
        int space = t.IndexOf(' ');
        if (colon > 0 && (space < 0 || colon < space))
            return new ScriptNode { Name = t[..(colon + 1)], Args = t[(colon + 1)..].Trim(), Line = line };
        if (space < 0) return new ScriptNode { Name = t, Line = line };
        return new ScriptNode { Name = t[..space], Args = t[(space + 1)..].Trim(), Line = line };
    }

    public static bool IsHeader(ScriptNode n) => n.Name.EndsWith(':');

    private static void BuildAbilities(List<ScriptNode> nodes, List<Ability> abilities, List<ScriptNode> flags)
    {
        Ability? current = null;
        foreach (var n in nodes)
        {
            if (IsHeader(n))
            {
                current = MakeAbility(n);
                if (current != null) abilities.Add(current);
                else flags.Add(n);
                continue;
            }
            // 事件卡偶尔写成没有冒号的 "Effect" + 块。
            if (n.Key == "effect" && n.Block != null && n.Args.Length == 0)
            {
                current = new Ability { Kind = AbilityKind.Event, Body = n.Block };
                abilities.Add(current);
                continue;
            }
            if (current != null && AbilityModifiers.Contains(n.Name))
            {
                current.Modifiers.Add(n);
                if (n.Key == "text") current.Text = n.Args;
                continue;
            }
            flags.Add(n);
        }
    }

    private static Ability? MakeAbility(ScriptNode n)
    {
        var head = n.Name[..^1].ToLowerInvariant();
        var args = n.Args;
        var body = n.Block ?? new List<ScriptNode>();
        switch (head)
        {
            case "auto":
            case "cont":
            {
                int sp = args.IndexOf(' ');
                var trig = sp < 0 ? args : args[..sp];
                var rest = sp < 0 ? "" : args[(sp + 1)..].Trim();
                return new Ability
                {
                    Kind = head == "auto" ? AbilityKind.Auto : AbilityKind.Cont,
                    Trigger = trig,
                    HeaderArgs = rest,
                    Body = body,
                };
            }
            case "act":
            case "act_backup":
                return new Ability { Kind = AbilityKind.Act, Trigger = head == "act_backup" ? "Backup" : "", HeaderArgs = args, Body = body };
            case "effect":
                return new Ability { Kind = AbilityKind.Event, Body = body };
            default:
                return null;
        }
    }
}

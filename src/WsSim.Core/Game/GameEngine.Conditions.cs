using WsSim.Core.Cards;
using WsSim.Core.Effects;

namespace WsSim.Core.Game;

/// <summary>效果脚本：条件、数量、区域名。</summary>
public sealed partial class GameEngine
{
    // ------------------------------------------------------------------ 区域名

    /// <summary>
    /// 脚本里的区域名对应的卡（以 <paramref name="me"/> 的视角）：Stage、CenterStage、Backstage、MiddleCenterStage、
    /// Hand、Deck、WaitingRoom、Stock、Clock、Level、Memory、ClimaxArea / Climax、Marker；前缀 Opponent 表示对手的；
    /// AnyStage 是双方舞台。牌库按从顶到底的顺序。
    /// </summary>
    internal List<CardInstance> CardsIn(string loc, int me, CardInstance? self = null)
    {
        loc = loc.Trim();
        var key = loc.ToLowerInvariant();
        if (key == "anystage")
            return State.Players[me].StageCards.Concat(State.Players[1 - me].StageCards).ToList();
        int player = me;
        if (key.StartsWith("opponent") && key != "opponent")
        {
            player = 1 - me;
            key = key["opponent".Length..];
        }
        if (key.StartsWith("your")) key = key[4..];
        if (key.StartsWith("s") && key is "sstage" or "shand" or "swaitingroom") key = key[1..];
        var p = State.Players[player];
        switch (key)
        {
            case "stage": case "characters": return p.StageCards.ToList();
            case "centerstage": case "frontstage": case "center": case "frontrow":
                return StageSlots.Front.Select(s => p.Stage[(int)s]).Where(c => c != null).ToList()!;
            case "backstage": case "back": case "backrow":
                return new[] { StageSlot.BackLeft, StageSlot.BackRight }.Select(s => p.Stage[(int)s]).Where(c => c != null).ToList()!;
            case "middlecenterstage": case "middlestage":
                return p.Stage[(int)StageSlot.FrontCenter] is { } m ? new List<CardInstance> { m } : new();
            case "leftcenterstage": return p.Stage[0] is { } l ? new List<CardInstance> { l } : new();
            case "rightcenterstage": return p.Stage[2] is { } r ? new List<CardInstance> { r } : new();
            case "hand": return p.Hand.ToList();
            case "deck": return p.Deck.ToList();
            case "waitingroom": case "waitingrooom": case "wr": case "watingroom": return p.WaitingRoom.ToList();
            case "stock": return p.Stock.ToList();
            case "clock": return p.Clock.ToList();
            case "level": return p.Level.ToList();
            case "memory": case "memroy": return p.Memory.ToList();
            case "climax": case "climaxarea": return p.ClimaxArea.ToList();
            case "resolution": return p.Resolution.ToList();
            case "marker": case "markers":
                return self != null ? self.Markers.ToList() : p.StageCards.SelectMany(c => c.Markers).ToList();
            case "facing":
                if (self != null && self.Zone == ZoneType.Stage && State.Players[self.Owner].SlotOf(self) is { } fs && fs.IsFront())
                    return State.Players[1 - self.Owner].Stage[(int)fs.Facing()] is { } f ? new List<CardInstance> { f } : new();
                return new();
            default:
                NoteScript($"未知区域 {loc}");
                return new();
        }
    }

    /// <summary>区域名对应的 ZoneType（只看类型，不分玩家）。</summary>
    internal static (ZoneType Zone, bool Bottom, bool Opponent)? ZoneOf(string loc)
    {
        var key = loc.Trim().ToLowerInvariant();
        bool opp = false;
        if (key.StartsWith("opponent")) { opp = true; key = key["opponent".Length..]; }
        bool bottom = false;
        if (key.StartsWith("bottomof")) { bottom = true; key = key["bottomof".Length..]; }
        if (key.StartsWith("topof")) key = key["topof".Length..];
        ZoneType? z = key switch
        {
            "hand" => ZoneType.Hand,
            "deck" => ZoneType.Deck,
            "waitingroom" or "waitingrooom" => ZoneType.WaitingRoom,
            "stock" => ZoneType.Stock,
            "clock" => ZoneType.Clock,
            "level" => ZoneType.Level,
            "memory" => ZoneType.Memory,
            "climax" or "climaxarea" => ZoneType.ClimaxArea,
            "stage" or "centerstage" or "backstage" or "middlecenterstage" => ZoneType.Stage,
            "marker" => ZoneType.Marker,
            _ => null,
        };
        return z == null ? null : (z.Value, bottom, opp);
    }

    // ------------------------------------------------------------------ 条件

    internal bool Eval(string? cond, EffectContext ctx, CardInstance? subject) => Eval(CondParser.Parse(cond), ctx, subject);

    internal bool Eval(Cond c, EffectContext ctx, CardInstance? subject)
    {
        switch (c)
        {
            case AndCond a:
                foreach (var p in a.Parts) if (!Eval(p, ctx, subject)) return false;
                return true;
            case OrCond o:
                foreach (var p in o.Parts) if (Eval(p, ctx, subject)) return true;
                return false;
            case NotCond n:
                return !Eval(n.Inner, ctx, subject);
            case AtomCond atom:
                return EvalAtom(atom, ctx, subject);
            default:
                return false;
        }
    }

    private static string[] Alternatives(string s) =>
        s.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal static bool NameMatches(CardInstance c, string names, bool contains = false, bool printedOnly = false)
    {
        var own = new List<string> { c.Def.Name };
        if (c.Def.JpName != null) own.Add(c.Def.JpName);
        if (!printedOnly) own.AddRange(c.Modifiers.Where(m => m.Kind == ModKind.Name).Select(m => m.Text!));
        foreach (var alt in Alternatives(names))
        {
            var a = alt.Trim();
            foreach (var n in own)
            {
                if (contains ? n.Contains(a, StringComparison.OrdinalIgnoreCase) : string.Equals(n.Trim(), a, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        // 名字本身含 "|" 的少数卡：整体比较一次。
        return !contains && string.Equals(c.Def.Name, names.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool EvalAtom(AtomCond a, EffectContext ctx, CardInstance? s)
    {
        var me = ctx.Controller;
        var args = a.Args;
        switch (a.Key)
        {
            case "any": case "with": return true;
            case "yes": return ctx.LastYes;
            case "no": return !ctx.LastYes;
            case "answer": return int.TryParse(args.Split(' ')[0], out var ans) && ctx.Answer == ans;
            case "hastarget": return ctx.HasTargets ? ctx.Targets.Count > 0 : false;
            case "other": return s != null && !ReferenceEquals(s, ctx.Source);
            case "isself": return s != null && ReferenceEquals(s, ctx.Source);
            case "myturn": return State.ActivePlayer == me;
            case "opponentsturn": return State.ActivePlayer != me;
            case "mycard": return s != null && s.Owner == me;
            case "hastrait":
                return s != null && Alternatives(args).Any(s.HasTrait);
            case "nameequals": return s != null && NameMatches(s, args);
            case "printednameequals": return s != null && NameMatches(s, args, printedOnly: true);
            case "namecontains": case "nameincludes": return s != null && NameMatches(s, args, contains: true);
            case "insavedtargets": return s != null && ctx.Saved.Contains(s);
            case "targetscontain": return s != null && ctx.HasTargets && ctx.Targets.Contains(s);
            case "samenameassavedtarget": return s != null && ctx.Saved.Any(x => x.Def.Name == s.Def.Name);
            case "ischaracter": return s != null && s.Def.IsCharacter;
            case "isclimax": return s != null && s.Def.IsClimax;
            case "isevent": return s != null && s.Def.IsEvent;
            case "iscolor": case "coloris":
                return s != null && Alternatives(args).Any(x => Enum.TryParse<CardColor>(x, true, out var col) && s.Def.Color == col);
            case "hastrigger":
                return s != null && Alternatives(args).Any(x => HasTriggerIcon(s, x));
            case "cardposition":
            {
                if (s == null || s.Zone != ZoneType.Stage) return false;
                return args.ToLowerInvariant() switch
                {
                    "reverse" or "reversed" => s.Orientation == CardOrientation.Reversed,
                    "rest" or "rested" => s.Orientation == CardOrientation.Rested,
                    "stand" or "standing" => s.Orientation == CardOrientation.Standing,
                    _ => false,
                };
            }
            case "stand": return s != null && s.Zone == ZoneType.Stage && s.Orientation == CardOrientation.Standing;
            case "rest": return s != null && s.Zone == ZoneType.Stage && s.Orientation == CardOrientation.Rested;
            case "reverse": return s != null && s.Zone == ZoneType.Stage && s.Orientation == CardOrientation.Reversed;
            case "onstage": return s != null && s.Zone == ZoneType.Stage;
            case "inlocation":
                return s != null && Alternatives(args).Any(l => CardsIn(l, me, ctx.Self).Contains(s));
            case "hasmarker": return s != null && s.Markers.Count > 0;
            case "inbattle": return s != null && _attack != null && (ReferenceEquals(_attack.Attacker, s) || ReferenceEquals(_attack.Defender, s));
            case "canbechosen": return s != null && (s.Owner == me || !s.Has("CannotBeChosen"));
            case "canmove": return s != null && !s.Has("CannotMove");
            case "playedthisturn": return s != null && s.PlayedFromHandTurn == State.TurnNumber;
            case "phase": return Enum.TryParse<Phase>(args, true, out var ph) && State.Phase == ph;
            case "hastemplabel": return s != null && s.TempLabels.Contains(args);
            case "lastdamagecanceled": return ctx.LastDamageCanceled;
            case "havecolor":
                return Alternatives(args).Any(x => Enum.TryParse<CardColor>(x, true, out var col) && State.Players[me].StageCards.Any(c => c.Def.Color == col));
            case "climax":
            {
                var cx = State.Players[me].ClimaxArea;
                return args.Length == 0 ? cx.Count > 0 : cx.Any(c => NameMatches(c, args));
            }
            case "level":
                return s != null && int.TryParse(args.Split(' ')[0], out var lv) && s.Level == lv;
            case "compare":
                return EvalCompare(args, ctx, s);

            case "stagecontains": case "stagecontans": case "have":
                return CompareCount(CountStage(me, a.Filter!, ctx, null), args, ctx, "ormore");
            case "stagecontains_other": case "have_other":
                return CompareCount(CountStage(me, a.Filter!, ctx, ctx.Source), args, ctx, "ormore");
            case "stagecontainsorless":
                return CompareCount(CountStage(me, a.Filter!, ctx, null), args, ctx, "orless");
            case "opponentstagecontains":
                return CompareCount(CountStage(1 - me, a.Filter!, ctx, null), args, ctx, "ormore");
            case "locationcontains":
            case "locationcontainsorless":
            {
                var w = args.Split(' ', 2);
                int count = CardsIn(w[0], me, ctx.Self).Count(c => Eval(a.Filter!, ctx, c));
                return CompareCount(count, w.Length > 1 ? w[1] : "1", ctx, a.Key == "locationcontains" ? "ormore" : "orless");
            }
            case "facedown": return false;
            case "faceup": return true;
            case "allyourcharacters":
                return State.Players[me].StageCards.All(c => Eval(a.Filter!, ctx, c));
            case "facing":
            case "battleopponent":
            {
                var who = s ?? ctx.Self;
                var f = a.Key == "facing" ? FacingOf(who) : BattleOpponentOf(who);
                return f != null && Eval(a.Filter!, ctx, f);
            }
            default:
                NoteScript($"未知条件 {a.Name}");
                return false;
        }
    }

    /// <summary>"n" 或 "n OrLess" 形式的数量比较。</summary>
    private bool CompareCount(int count, string args, EffectContext ctx, string defaultOp)
    {
        var w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = w.Length > 0 ? ParseQuantity(w[0], ctx, null) : 1;
        var op = w.Length > 1 && CondParser.IsCompareOp(w[1]) ? w[1].ToLowerInvariant() : defaultOp;
        return op switch
        {
            "orless" or "orlower" or "orfewer" => count <= n,
            "exactly" or "equals" => count == n,
            "greaterthan" or "morethan" => count > n,
            "lessthan" or "fewerthan" => count < n,
            _ => count >= n,
        };
    }

    private int CountStage(int player, Cond filter, EffectContext ctx, CardInstance? except) =>
        State.Players[player].StageCards.Count(c => !ReferenceEquals(c, except) && Eval(filter, ctx, c));

    private int FirstInt(string args, EffectContext ctx) => ParseQuantity(args.Split(' ')[0], ctx, null);

    internal static bool HasTriggerIcon(CardInstance c, string icon)
    {
        foreach (var raw in c.Def.RawTriggers)
            if (string.Equals(raw, icon, StringComparison.OrdinalIgnoreCase)) return true;
        if (Enum.TryParse<TriggerIcon>(icon, true, out var t) && c.Def.Triggers.Contains(t)) return true;
        return c.Cont.Triggers.Contains(icon, StringComparer.OrdinalIgnoreCase)
               || c.Modifiers.Any(m => m.Kind == ModKind.Trigger && string.Equals(m.Text, icon, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Compare 量 数 OrMore|OrLess|Exactly…</summary>
    private bool EvalCompare(string args, EffectContext ctx, CardInstance? s)
    {
        var w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 2) return false;
        int lhs = ParseQuantity(w[0], ctx, s);
        int rhs = ParseQuantity(w[1], ctx, s);
        var op = w.Length > 2 ? w[2].ToLowerInvariant() : "ormore";
        return op switch
        {
            "ormore" or "orhigher" or "orgreater" or "ormore." => lhs >= rhs,
            "orless" or "orlower" or "orfewer" => lhs <= rhs,
            "exactly" or "equals" or "equal" => lhs == rhs,
            "greaterthan" or "morethan" or "higherthan" => lhs > rhs,
            "lessthan" or "lowerthan" or "fewerthan" => lhs < rhs,
            _ => lhs >= rhs,
        };
    }

    /// <summary>数量：数字、X、ThatCardsLevel、CardsInClock、MyLevel……</summary>
    internal int ParseQuantity(string q, EffectContext ctx, CardInstance? subject)
    {
        q = q.Trim();
        if (int.TryParse(q, out var n)) return n;
        if (q.StartsWith('-') && q.Length > 1) return -ParseQuantity(q[1..], ctx, subject);
        var me = State.Players[ctx.Controller];
        var opp = State.Players[1 - ctx.Controller];
        var key = q.ToLowerInvariant();
        // GiveAll / Assist 里的“那个角色”是受影响的角色。
        var that = subject ?? ctx.ThatCard ?? (!ReferenceEquals(ctx.Self, ctx.Source) ? ctx.Self : null);
        var target = ctx.HasTargets && ctx.Targets.Count > 0 ? ctx.Targets[0] : null;
        switch (key)
        {
            case "x": return ctx.X;
            case "answer": return ctx.Answer;
            case "thatcardslevel": return that?.Level ?? 0;
            case "thatcardscost": return that?.Cost ?? 0;
            case "thatcardssoul": return that?.Soul ?? 0;
            case "thatcardspower": return that?.Power ?? 0;
            case "thiscardslevel": return ctx.Self.Level;
            case "thiscardspower": return ctx.Self.Power;
            case "thiscardssoul": return ctx.Self.Soul;
            case "targetcardslevel": return target?.Level ?? 0;
            case "targetcardscost": return target?.Cost ?? 0;
            case "targetcardssoul": return target?.Soul ?? 0;
            case "targetcardspower": return target?.Power ?? 0;
            case "targetcount": return ctx.HasTargets ? ctx.Targets.Count : 0;
            case "mylevel": return me.PlayerLevel;
            case "opponentslevel": case "opponentlevel": return opp.PlayerLevel;
            case "experience": case "expeirience": return me.Level.Sum(c => c.Def.Level);
            case "totallevel": return me.Level.Sum(c => c.Def.Level);
            case "brainstormedclimax": return ctx.BrainstormClimax;
            case "markersunderthis": case "thiscardsmarkers": return ctx.Self.Markers.Count;
            case "climaxinwaitingroom": return me.WaitingRoom.Count(c => c.Def.IsClimax);
            case "characters": case "numberofcharacters": return me.StageCards.Count();
            case "opponentscharacters": return opp.StageCards.Count();
        }
        if (key.StartsWith("cardsinopponents"))
            return CardsIn("Opponent" + q["CardsInOpponents".Length..], ctx.Controller, ctx.Self).Count;
        if (key.StartsWith("cardsinopponent"))
            return CardsIn("Opponent" + q["CardsInOpponent".Length..], ctx.Controller, ctx.Self).Count;
        if (key.StartsWith("cardsin"))
            return CardsIn(q["CardsIn".Length..], ctx.Controller, ctx.Self).Count;
        NoteScript($"未知数量 {q}");
        return 0;
    }

    // ------------------------------------------------------------------ 位置关系

    internal CardInstance? FacingOf(CardInstance? c)
    {
        if (c == null || c.Zone != ZoneType.Stage) return null;
        var slot = State.Players[c.Owner].SlotOf(c);
        if (slot is not { } s || !s.IsFront()) return null;
        return State.Players[1 - c.Owner].Stage[(int)s.Facing()];
    }

    /// <summary>战斗对手：正在进行的正面攻击里的另一方；没有正在进行的攻击时用最近一次战斗的记录。</summary>
    internal CardInstance? BattleOpponentOf(CardInstance? c)
    {
        if (c == null) return null;
        var at = _attack ?? _lastBattle;
        if (at == null || at.Type != AttackType.Front) return null;
        if (ReferenceEquals(at.Attacker, c)) return at.Defender;
        if (ReferenceEquals(at.Defender, c)) return at.Attacker;
        return null;
    }

    /// <summary>后列角色“前面”的前列角色（应援）。</summary>
    internal IEnumerable<CardInstance> InFrontOf(CardInstance c)
    {
        var p = State.Players[c.Owner];
        var slot = p.SlotOf(c);
        var fronts = slot switch
        {
            StageSlot.BackLeft => new[] { StageSlot.FrontLeft, StageSlot.FrontCenter },
            StageSlot.BackRight => new[] { StageSlot.FrontCenter, StageSlot.FrontRight },
            _ => Array.Empty<StageSlot>(),
        };
        foreach (var s in fronts)
            if (p.Stage[(int)s] is { } f) yield return f;
    }
}

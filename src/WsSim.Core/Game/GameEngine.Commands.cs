using WsSim.Core.Cards;
using WsSim.Core.Effects;

namespace WsSim.Core.Game;

/// <summary>效果脚本：指令执行。未知指令记日志后跳过，不会让对局崩溃。</summary>
public sealed partial class GameEngine
{
    /// <summary>解释器认识的指令（覆盖率报告用）。能力头和卡片级标记另算。</summary>
    public static readonly HashSet<string> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // 控制流
        "If", "Else", "IfTarget", "IfThatCard", "IfOther", "IfOwner", "PayCost", "ForcePayCost", "Question", "Question_Obvious",
        "OpponentQuestion", "MultipleChoiceQuestion", "OpponentMultipleChoiceQuestion", "Repeat", "PerClimax", "ForEachTarget",
        "GainEffect", "Effect", "Block",
        // 目标
        "TargetSelf", "TargetOther", "TargetFacing", "TargetBattleOpponent", "TargetMyCharacterInBattle", "TargetLocation",
        "TargetTop", "TargetBottom", "TargetAll", "TargetBrainstormed", "TargetMarkersUnderTargets", "TargetOwner",
        "AddSelfToTargets", "FirstTarget", "SaveTargets", "LoadTargets", "AddSavedToCurrentTargets", "PushTargets", "PopTargets",
        "NarrowTargets", "ReverseTargets",
        "ChooseCharacterFromTargets", "ChooseCharacterFromTargets_Optional", "ChooseCharactersFromTargets", "ChooseCardsFromTargets",
        "ChooseCardFromTargets", "ChooseCard", "ChooseCharacter", "ChooseMultipleCharacters",
        "SearchLocationForTargets", "OpponentSearchLocationForTargets", "SearchDeck", "SearchWaitingRoom", "SearchWaitingRoomForTarget",
        "RevealUpTo", "Peek", "PeekTop", "Reveal", "RevealTargets", "View", "EndPeek", "Reorder", "PutTheRestIntoTheWaitingRoom",
        "PutTheRestInToTheWaitingRoom", "PushSavedTargets", "PopSavedTargets", "AddCurrentToSavedTargets", "RemoveCurrentFromSavedTargets",
        "PushX", "PopX", "MarkerTargetsUnderSavedTarget_FaceDown", "OpponentRefresh",
        // 动作
        "GainPower", "LosePower", "GainSoul", "LoseSoul", "GainLevel", "LoseLevel", "GainTrait", "GainName", "GainSpecialEffect",
        "GainTriggerIcon", "MoveCard", "MoveCards", "MoveCardToBottom", "Bounce", "Rest", "Stand", "Reverse", "DontStand",
        "PlaceOnStage", "MoveToOpenPosition", "MoveToFacing", "Exchange", "Change", "Revive", "RememberSlot", "PlaceOnRememberedSlot",
        "Marker_FaceDown", "Marker_FaceUp", "MarkerTargetsUnderSavedTarget_FaceUp", "FlipFaceUp", "FlipFaceDown",
        "DrawCard", "Discard", "Mill", "MillOpponent", "MillOpponentBottom", "MillUpTo", "Heal", "Burn", "BurnSelf", "Brainstorm",
        "ShuffleLocation", "BlindStock", "Refresh", "Bond", "TempLabel",
        "SetXValue", "AddToXValue", "MultiplyXValue", "SetXToNumberOfCharacters",
        "UntilEndOfOpponentsTurn", "RefundTimesPerTurn", "ScheduleEffect", "ScheduleEffectForTargets", "ScheduleEffectUntil",
        "BlockOpponentEncore", "BlockEncore", "BlockOpponentEvents", "BlockOpponentBackup", "BlockOpponentAct",
        // 只影响 AI / 界面
        "Tag", "AIChoice", "Message", "OpponentMessage", "AINote", "EffectNote", "Quick", "Text", "Image", "Wait",
    };

    /// <summary>卡片级标记和能力修饰（不是指令，但解释器认识）。</summary>
    public static readonly HashSet<string> SupportedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tag", "Backup", "Counter", "Requirement", "AINote", "Quick", "InMemory", "Text", "TimesPerTurn", "ActivationRequirement",
        "ActivationRequirement_Other", "UntilEndOfOpponentsTurn", "RefundTimesPerTurn", "Alarm", "Army", "Clone", "EffectNote", "Image",
        "Image:",
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ScriptNode, List<Ability>> GainedCache = new(ReferenceEqualityComparer.Instance);

    internal static List<Ability> AbilitiesInBlock(ScriptNode n) =>
        GainedCache.GetOrAdd(n, x => ScriptParser.ParseAbilities(x.Block ?? new List<ScriptNode>()));

    private int _execDepth;

    internal void Exec(List<ScriptNode>? body, EffectContext ctx)
    {
        if (body == null) return;
        if (++_execDepth > 60)
        {
            _execDepth--;
            Info("[脚本] 效果嵌套过深，停止");
            return;
        }
        try
        {
            for (int i = 0; i < body.Count; i++)
            {
                var n = body[i];
                bool? cond = ExecNode(n, ctx);
                if (cond is { } c && i + 1 < body.Count && body[i + 1].Key == "else")
                {
                    if (!c) Exec(body[i + 1].Block, ctx);
                    i++;
                }
            }
        }
        finally
        {
            _execDepth--;
        }
    }

    private List<CardInstance> Cur(EffectContext ctx) => ctx.HasTargets ? ctx.Targets : new List<CardInstance> { ctx.Self };

    /// <summary>获得类动作（GainPower、GainEffect…）作用的卡。</summary>
    private List<CardInstance> Recipients(EffectContext ctx) =>
        ctx.Continuous && ctx.ApplyToSelf ? new List<CardInstance> { ctx.Self } : Cur(ctx);

    private void SetTargets(EffectContext ctx, IEnumerable<CardInstance?> cards)
    {
        ctx.Targets = cards.Where(c => c != null).Distinct().ToList()!;
        ctx.HasTargets = true;
    }

    private void ExecTail(ScriptNode n, EffectContext ctx)
    {
        // "TargetSelf GainPower 1000"：目标指令后面直接跟一条指令。
        if (n.Args.Length == 0) return;
        var w = n.Args.Split(' ', 2);
        var sub = new ScriptNode { Name = w[0], Args = w.Length > 1 ? w[1] : "", Line = n.Line, Block = n.Block };
        ExecNode(sub, ctx);
    }

    private int Q(string s, EffectContext ctx) => ParseQuantity(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0", ctx, null);

    /// <summary>执行一条指令。If 类指令返回条件结果（给后面的 Else 用），其余返回 null。</summary>
    private bool? ExecNode(ScriptNode n, EffectContext ctx)
    {
        var a = n.Args;
        var me = State.Players[ctx.Controller];
        var opp = State.Players[1 - ctx.Controller];
        bool cont = ctx.Continuous;
        switch (n.Key)
        {
            // -------------------------------------------------- 控制流
            case "if":
            {
                bool r = Eval(a, ctx, ctx.Self);
                if (r) Exec(n.Block, ctx);
                return r;
            }
            case "iftarget":
            {
                bool r = ctx.HasTargets && ctx.Targets.Count > 0 && ctx.Targets.All(t => Eval(a, ctx, t));
                if (r) Exec(n.Block, ctx);
                return r;
            }
            case "ifthatcard":
            {
                bool r = ctx.ThatCard != null && Eval(a, ctx, ctx.ThatCard);
                if (r) Exec(n.Block, ctx);
                return r;
            }
            case "ifother":
            {
                bool r = ctx.Trigger.Other != null && Eval(a, ctx, ctx.Trigger.Other);
                if (r) Exec(n.Block, ctx);
                return r;
            }
            case "ifowner":
            {
                bool r = Eval(a, ctx, ctx.Source);
                if (r) Exec(n.Block, ctx);
                return r;
            }
            case "else":
                return null; // 由前面的 If 处理
            case "paycost":
            case "forcepaycost":
            {
                if (cont) return false;
                var items = ParseCost(a);
                bool r = false;
                if (CanPayCost(items, ctx))
                {
                    r = n.Key == "forcepaycost" || items.Count == 0 && a.Trim() == "0"
                        ? n.Key == "forcepaycost" || AskYesNo(ctx.Controller, $"{ctx.Source.Def.Name}：是否发动？")
                        : AskYesNo(ctx.Controller, $"{ctx.Source.Def.Name}：是否支付费用（{DescribeCost(a)}）？");
                    if (r)
                    {
                        PayCost(items, ctx);
                        Info($"P{ctx.Controller} 支付 {ctx.Source.Def.Name} 的费用：{DescribeCost(a)}");
                    }
                }
                if (r) Exec(n.Block, ctx);
                return r;
            }
            case "question":
            case "question_obvious":
                if (cont) { ctx.LastYes = false; return null; }
                ctx.LastYes = AskYesNo(ctx.Controller, $"{ctx.Source.Def.Name}：{a}");
                return null;
            case "opponentquestion":
                if (cont) { ctx.LastYes = false; return null; }
                ctx.LastYes = AskYesNo(1 - ctx.Controller, $"{ctx.Source.Def.Name}：{a}");
                return null;
            case "multiplechoicequestion":
            case "opponentmultiplechoicequestion":
            {
                if (cont) { ctx.Answer = 0; return null; }
                var parts = a.Split('|');
                var prompt = parts[0].Replace("\\n", "\n").Trim();
                var options = parts.Skip(1).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (options.Count == 0) options = ExtractNumberedOptions(prompt);
                if (options.Count == 0) options = new List<string> { "1", "2" };
                int who = n.Key.StartsWith("opponent") ? 1 - ctx.Controller : ctx.Controller;
                var opts = options.Select(o => new DecisionOption(o)).ToList();
                ctx.Answer = Ask(who, DecisionType.ChooseOption, $"{ctx.Source.Def.Name}：{prompt}", opts, 1, 1)[0];
                return null;
            }
            case "repeat":
            {
                int times = Math.Min(Q(a, ctx), 60);
                for (int k = 0; k < times; k++) Exec(n.Block, ctx);
                return null;
            }
            case "perclimax":
            {
                int times = a.Length == 0 ? ctx.BrainstormClimax : ctx.Brainstormed.Count(c => c.Def.IsClimax && Eval(a, ctx, c));
                for (int k = 0; k < times; k++) Exec(n.Block, ctx);
                return null;
            }
            case "foreachtarget":
            {
                foreach (var t in Cur(ctx).ToList())
                {
                    var saved = (ctx.Targets, ctx.HasTargets);
                    SetTargets(ctx, new[] { t });
                    ctx.ThatCard = t;
                    Exec(n.Block, ctx);
                    (ctx.Targets, ctx.HasTargets) = saved;
                }
                return null;
            }
            case "effect":
            case "block":
                Exec(n.Block, ctx);
                return null;
            case "gaineffect":
            {
                var abilities = AbilitiesInBlock(n);
                foreach (var t in Recipients(ctx).ToList())
                    foreach (var ab in abilities)
                        AddModifier(t, ModKind.Ability, 0, null, ab, ctx);
                ctx.UntilOpponentsTurn = false;
                return null;
            }

            // -------------------------------------------------- 目标
            case "targetself":
                SetTargets(ctx, new[] { ctx.Self });
                ExecTail(n, ctx);
                return null;
            case "targetother":
                SetTargets(ctx, new[] { ctx.Trigger.Other });
                ExecTail(n, ctx);
                return null;
            case "targetfacing":
                SetTargets(ctx, new[] { FacingOf(ctx.Self) });
                ExecTail(n, ctx);
                return null;
            case "targetbattleopponent":
                SetTargets(ctx, new[] { BattleOpponentFor(ctx) });
                ExecTail(n, ctx);
                return null;
            case "targetmycharacterinbattle":
            {
                var at = _attack ?? _lastBattle;
                CardInstance? mine = null;
                if (at != null)
                    mine = at.Attacker.Owner == ctx.Controller ? at.Attacker : at.Defender?.Owner == ctx.Controller ? at.Defender : null;
                SetTargets(ctx, new[] { mine?.Zone == ZoneType.Stage ? mine : null });
                ExecTail(n, ctx);
                return null;
            }
            case "targetlocation":
            {
                var w = a.Split(' ', 2);
                SetTargets(ctx, CardsIn(w[0], ctx.Controller, ctx.Self));
                return null;
            }
            case "targetall":
                SetTargets(ctx, me.StageCards.Concat(opp.StageCards));
                return null;
            case "targettop":
            case "targetbottom":
            {
                var w = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int k = w.Length > 1 ? Q(w[1], ctx) : 1;
                var loc = w.Length > 0 ? w[0] : "Deck";
                SetTargets(ctx, TopOrBottom(loc, k, n.Key == "targettop", ctx));
                ctx.ThatCard = ctx.Targets.FirstOrDefault();
                ExecTail2(n, ctx, 2);
                return null;
            }
            case "targetbrainstormed":
                SetTargets(ctx, ctx.Brainstormed);
                return null;
            case "targetmarkersundertargets":
                SetTargets(ctx, Cur(ctx).SelectMany(c => c.Markers).ToList());
                return null;
            case "targetowner":
                SetTargets(ctx, new[] { ctx.Source });
                return null;
            case "addselftotargets":
                SetTargets(ctx, (ctx.HasTargets ? ctx.Targets : new List<CardInstance>()).Append(ctx.Self).ToList());
                return null;
            case "firsttarget":
                SetTargets(ctx, ctx.Targets.Take(1).ToList());
                return null;
            case "savetargets":
                ctx.Saved = Cur(ctx).ToList();
                return null;
            case "loadtargets":
                SetTargets(ctx, ctx.Saved);
                return null;
            case "addsavedtocurrenttargets":
                SetTargets(ctx, (ctx.HasTargets ? ctx.Targets : new List<CardInstance>()).Concat(ctx.Saved).ToList());
                return null;
            case "pushsavedtargets":
                ctx.Pushed.Push(ctx.Saved.ToList());
                return null;
            case "popsavedtargets":
                if (ctx.Pushed.Count > 0) ctx.Saved = ctx.Pushed.Pop();
                return null;
            case "addcurrenttosavedtargets":
                ctx.Saved = ctx.Saved.Concat(Cur(ctx)).Distinct().ToList();
                return null;
            case "removecurrentfromsavedtargets":
                ctx.Saved = ctx.Saved.Except(Cur(ctx)).ToList();
                return null;
            case "pushx":
                ctx.XStack.Push(ctx.X);
                return null;
            case "popx":
                if (ctx.XStack.Count > 0) ctx.X = ctx.XStack.Pop();
                return null;
            case "pushtargets":
                ctx.Pushed.Push(ctx.Targets.ToList());
                return null;
            case "poptargets":
                if (ctx.Pushed.Count > 0) SetTargets(ctx, ctx.Pushed.Pop());
                return null;
            case "reversetargets":
                if (ctx.HasTargets) ctx.Targets.Reverse();
                return null;
            case "narrowtargets":
                SetTargets(ctx, Cur(ctx).Where(t => Eval(a, ctx, t)).ToList());
                return null;

            case "choosecharacterfromtargets":
            case "choosecharacterfromtargets_optional":
            case "choosecardfromtargets":
            case "choosecard":
            case "choosecharacter":
            {
                if (cont) return null;
                bool optional = n.Key.EndsWith("_optional");
                var cands = Choosable(ctx.HasTargets ? ctx.Targets : new List<CardInstance>(), ctx);
                var picked = AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：{(a.Length > 0 ? a : "选择 1 张卡")}", cands, optional ? 0 : 1, 1);
                SetTargets(ctx, picked);
                ctx.ThatCard = picked.FirstOrDefault() ?? ctx.ThatCard;
                return null;
            }
            case "choosecharactersfromtargets":
            {
                if (cont) return null;
                var w = a.Split(' ', 3);
                int min = w.Length > 0 ? Q(w[0], ctx) : 0, max = w.Length > 1 ? Q(w[1], ctx) : 1;
                var cands = Choosable(ctx.HasTargets ? ctx.Targets : new List<CardInstance>(), ctx);
                var picked = AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：{(w.Length > 2 ? w[2] : "选择角色")}", cands, min, max);
                SetTargets(ctx, picked);
                return null;
            }
            case "choosecardsfromtargets":
            {
                if (cont) return null;
                var w = a.Split(' ', 3);
                int min = Q(w[0], ctx), max = w.Length > 1 ? Q(w[1], ctx) : 1;
                var cond = w.Length > 2 ? w[2] : "Any";
                var cands = (ctx.HasTargets ? ctx.Targets : new List<CardInstance>()).Where(t => Eval(cond, ctx, t)).ToList();
                var picked = AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：选择 {(min == max ? max.ToString() : $"{min}–{max}")} 张", cands, min, max);
                SetTargets(ctx, picked);
                ctx.ThatCard = picked.FirstOrDefault() ?? ctx.ThatCard;
                return null;
            }
            case "choosemultiplecharacters":
            {
                if (cont) return null;
                var w = a.Split(' ', 3);
                int min = Q(w[0], ctx), max = w.Length > 1 ? Q(w[1], ctx) : 1;
                var cond = w.Length > 2 ? w[2] : "Any";
                var cands = Choosable(me.StageCards.Concat(opp.StageCards).Where(t => Eval(cond, ctx, t)).ToList(), ctx);
                SetTargets(ctx, AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：选择角色", cands, min, max));
                return null;
            }
            case "searchlocationfortargets":
            case "opponentsearchlocationfortargets":
            {
                if (cont) return null;
                var w = a.Split(' ', 4);
                bool oppChooses = n.Key.StartsWith("opponent");
                int chooser = oppChooses ? 1 - ctx.Controller : ctx.Controller;
                var loc = w[0];
                int min = w.Length > 1 ? Q(w[1], ctx) : 0, max = w.Length > 2 ? Q(w[2], ctx) : 1;
                var cond = w.Length > 3 ? w[3] : "Any";
                // 对手选：区域以对手的视角为准。
                var evalCtx = oppChooses ? ctx.Derive(ctx.Self) : ctx;
                var cands = CardsIn(loc, chooser, ctx.Self).Where(t => Eval(cond, ctx, t)).ToList();
                if (!oppChooses) cands = Choosable(cands, ctx);
                if (loc.Contains("Deck", StringComparison.OrdinalIgnoreCase)) ctx.ShuffleDeckAtEnd = true;
                var picked = AskChooseCards(chooser, $"{ctx.Source.Def.Name}：从{ZoneName(loc)}选择 {(min == max ? max.ToString() : $"{min}–{max}")} 张", cands, Math.Min(min, cands.Count), max);
                SetTargets(ctx, picked);
                ctx.ThatCard = picked.FirstOrDefault() ?? ctx.ThatCard;
                _ = evalCtx;
                return null;
            }
            case "searchdeck":
            {
                if (cont) return null;
                var cands = me.Deck.Where(t => Eval(a.Length == 0 ? "Any" : a, ctx, t)).ToList();
                var picked = AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：从牌库选择最多 1 张加入手牌", cands, 0, 1);
                foreach (var c in picked)
                {
                    Info($"P{ctx.Controller} 从牌库展示并加入手牌：{c}");
                    MoveTo(c, ZoneType.Hand);
                }
                Shuffle(me);
                SetTargets(ctx, picked);
                ctx.ThatCard = picked.FirstOrDefault() ?? ctx.ThatCard;
                return null;
            }
            case "searchwaitingroom":
            {
                if (cont) return null;
                var cands = me.WaitingRoom.Where(t => Eval(a.Length == 0 ? "Any" : a, ctx, t)).ToList();
                var picked = AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：从休息室选择 1 张加入手牌", cands, Math.Min(1, cands.Count), 1);
                foreach (var c in picked)
                {
                    Info($"P{ctx.Controller} 从休息室加入手牌：{c}");
                    MoveTo(c, ZoneType.Hand);
                }
                SetTargets(ctx, picked);
                ctx.ThatCard = picked.FirstOrDefault() ?? ctx.ThatCard;
                return null;
            }
            case "searchwaitingroomfortarget":
            {
                if (cont) return null;
                var cands = me.WaitingRoom.Where(t => Eval(a.Length == 0 ? "Any" : a, ctx, t)).ToList();
                SetTargets(ctx, AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：从休息室选择 1 张", cands, Math.Min(1, cands.Count), 1));
                return null;
            }
            case "revealupto":
            case "peek":
            case "peektop":
            case "reveal" when a.Length > 0:
            {
                var w = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var loc = w.Length > 0 ? w[0] : "Deck";
                int k = w.Length > 1 ? Q(w[1], ctx) : 1;
                var cards = TopOrBottom(loc, k, true, ctx);
                ctx.Looked.Clear();
                ctx.Looked.AddRange(cards);
                SetTargets(ctx, cards);
                ctx.ThatCard = cards.FirstOrDefault();
                if (cards.Count > 0)
                    Info($"P{ctx.Controller} {(n.Key.StartsWith("reveal") ? "公开" : "查看")}{ZoneName(loc)} {cards.Count} 张：{string.Join("、", cards.Select(c => c.Def.Name))}");
                return null;
            }
            case "reveal":
            case "revealtargets":
                if (ctx.HasTargets && ctx.Targets.Count > 0)
                {
                    ctx.ThatCard = ctx.Targets[0];
                    Info($"P{ctx.Controller} 公开：{string.Join("、", ctx.Targets.Select(c => c.Def.Name))}");
                }
                return null;
            case "view":
                return null;
            case "endpeek":
                ctx.Looked.Clear();
                return null;
            case "reorder":
            {
                if (cont) return null;
                var cards = Cur(ctx).Where(c => c.Zone == ZoneType.Deck).ToList();
                if (cards.Count <= 1) return null;
                var order = new List<CardInstance>();
                var left = cards.ToList();
                while (left.Count > 1)
                {
                    var pick = AskChooseCards(ctx.Controller, $"按顺序放回牌库顶：选择第 {order.Count + 1} 张（先选的在上面）", left, 1, 1)[0];
                    order.Add(pick);
                    left.Remove(pick);
                }
                order.AddRange(left);
                var owner = State.Players[cards[0].Owner];
                foreach (var c in cards) owner.Deck.Remove(c);
                owner.Deck.InsertRange(0, order);
                return null;
            }
            case "puttherestintothewaitingroom":
            {
                foreach (var c in ctx.Looked.Where(c => c.Zone == ZoneType.Deck).ToList())
                    MoveTo(c, ZoneType.WaitingRoom);
                ctx.Looked.Clear();
                return null;
            }

            // -------------------------------------------------- 数值 / 能力
            case "gainpower": ApplyStat(ModKind.Power, Q(a, ctx), ctx); return null;
            case "losepower": ApplyStat(ModKind.Power, -Q(a, ctx), ctx); return null;
            case "gainsoul": ApplyStat(ModKind.Soul, Q(a, ctx), ctx); return null;
            case "losesoul": ApplyStat(ModKind.Soul, -Q(a, ctx), ctx); return null;
            case "gainlevel": ApplyStat(ModKind.Level, Q(a, ctx), ctx); return null;
            case "loselevel": ApplyStat(ModKind.Level, -Q(a, ctx), ctx); return null;
            case "gaintrait":
                foreach (var t in Recipients(ctx).ToList()) AddModifier(t, ModKind.Trait, 0, a.Trim(), null, ctx);
                ctx.UntilOpponentsTurn = false;
                return null;
            case "gainname":
                foreach (var t in Recipients(ctx).ToList()) AddModifier(t, ModKind.Name, 0, a.Trim(), null, ctx);
                ctx.UntilOpponentsTurn = false;
                return null;
            case "gainspecialeffect":
                foreach (var t in Recipients(ctx).ToList()) AddModifier(t, ModKind.Special, 0, a.Split(' ')[0], null, ctx);
                ctx.UntilOpponentsTurn = false;
                return null;
            case "gaintriggericon":
                foreach (var t in Recipients(ctx).ToList()) AddModifier(t, ModKind.Trigger, 0, a.Split(' ')[0], null, ctx);
                ctx.UntilOpponentsTurn = false;
                return null;
            case "templabel":
                if (!cont) foreach (var t in Cur(ctx)) t.TempLabels.Add(a.Trim());
                return null;
            case "dontstand":
                if (!cont)
                    foreach (var t in Cur(ctx).ToList())
                        t.Modifiers.Add(new TimedModifier { Kind = ModKind.Special, Text = "DontStandNext", ExpiresAfterTurn = State.TurnNumber + 2 });
                return null;
            case "untilendofopponentsturn":
                ctx.UntilOpponentsTurn = true;
                return null;
            case "refundtimesperturn":
                if (!cont && ctx.Ability != null && ctx.Source.UsesThisTurn.TryGetValue(ctx.Ability, out var usedTimes) && usedTimes > 0)
                    ctx.Source.UsesThisTurn[ctx.Ability] = usedTimes - Math.Max(1, a.Length > 0 ? Q(a, ctx) : 1);
                return null;
            case "setxvalue": ctx.X = Q(a, ctx); return null;
            case "addtoxvalue": ctx.X += Q(a, ctx); return null;
            case "multiplyxvalue": ctx.X *= Q(a, ctx); return null;
            case "setxtonumberofcharacters":
                ctx.X = me.StageCards.Count(c => Eval(a.Length == 0 ? "Any" : a, ctx, c));
                return null;
        }

        // 下面都是会改变局面的动作，【永】模式里不执行。
        if (cont)
        {
            if (!SupportedCommands.Contains(n.Name)) NoteUnknown(n);
            return null;
        }

        switch (n.Key)
        {
            case "movecard":
            case "movecards":
            case "movecardtobottom":
            {
                var moved = new List<CardInstance>();
                foreach (var t in Cur(ctx).ToList())
                {
                    if (n.Key == "movecardtobottom")
                    {
                        if (t.Zone == ZoneType.Stage && t.Has("CannotMove")) continue;
                        MoveTo(t, ZoneType.Deck, toBottom: true);
                    }
                    else if (!MoveToNamedZone(t, a.Split(' ')[0], ctx)) continue;
                    moved.Add(t);
                }
                if (moved.Count > 0)
                {
                    ctx.ThatCard = moved[^1];
                    Info($"{ctx.Source.Def.Name}：{string.Join("、", moved.Select(c => c.Def.Name))} 放到{(n.Key == "movecardtobottom" ? "牌库底" : ZoneName(a.Split(' ')[0]))}");
                }
                return null;
            }
            case "bounce":
                foreach (var t in Cur(ctx).ToList())
                    if (t.Zone == ZoneType.Stage && !t.Has("CannotReturnToHand")) { MoveTo(t, ZoneType.Hand); ctx.ThatCard = t; }
                return null;
            case "rest":
                foreach (var t in Cur(ctx).Where(t => t.Zone == ZoneType.Stage).ToList())
                    SetOrientation(t, CardOrientation.Rested);
                return null;
            case "stand":
                foreach (var t in Cur(ctx).Where(t => t.Zone == ZoneType.Stage).ToList())
                    if (!t.Has("CannotStand")) SetOrientation(t, CardOrientation.Standing);
                return null;
            case "reverse":
                foreach (var t in Cur(ctx).Where(t => t.Zone == ZoneType.Stage).ToList())
                    ReverseCard(t, inBattle: false, byEffect: true);
                return null;
            case "placeonstage":
            {
                var w = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Equals("true", StringComparison.OrdinalIgnoreCase)).ToArray();
                bool openOnly = w.Length > 0 && w[0], front = w.Length <= 1 || w[1], back = w.Length <= 2 || w[2], rested = w.Length > 3 && w[3];
                var card = Cur(ctx).FirstOrDefault(c => c.Zone != ZoneType.Stage && c.Def.IsCharacter);
                if (card == null) return null;
                var p = State.Players[card.Owner];
                var slots = StageSlots.All.Where(s => (s.IsFront() ? front : back) && (!openOnly || p.Stage[(int)s] == null)).ToList();
                if (slots.Count == 0) return null;
                var slot = AskSlot(card.Owner, $"{ctx.Source.Def.Name}：选择放置 {card.Def.Name} 的格子", slots);
                PutOnStageByEffect(card, slot, rested ? CardOrientation.Rested : CardOrientation.Standing, ctx);
                return null;
            }
            case "placeonrememberedslot":
            {
                var card = Cur(ctx).FirstOrDefault(c => c.Zone != ZoneType.Stage && c.Def.IsCharacter);
                if (card == null || ctx.RememberedSlot is not { } slot) return null;
                PutOnStageByEffect(card, slot, CardOrientation.Standing, ctx);
                return null;
            }
            case "rememberslot":
                ctx.RememberedSlot = ctx.Self.Zone == ZoneType.Stage ? me.SlotOf(ctx.Self) : ctx.Self.LastSlot;
                return null;
            case "movetoopenposition":
            case "movetofacing":
            {
                var w = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Equals("true", StringComparison.OrdinalIgnoreCase)).ToArray();
                bool front = w.Length == 0 || w[0], back = w.Length > 1 && w[1];
                if (w.Length == 2 && w[0] && w[1]) { front = true; back = true; }
                foreach (var t in Cur(ctx).Where(t => t.Zone == ZoneType.Stage && !t.Has("CannotMove")).Take(1).ToList())
                {
                    var p = State.Players[t.Owner];
                    var from = p.SlotOf(t)!.Value;
                    var slots = StageSlots.All.Where(s => s != from && p.Stage[(int)s] == null && (s.IsFront() ? front : back)).ToList();
                    if (slots.Count == 0) continue;
                    var to = AskSlot(ctx.Controller, $"{ctx.Source.Def.Name}：选择 {t.Def.Name} 移动到的空格", slots);
                    MoveOnStage(p, from, to);
                }
                return null;
            }
            case "exchange":
            {
                var t = Cur(ctx);
                if (t.Count >= 2) ExchangeCards(t[0], t[1], ctx);
                return null;
            }
            case "change":
            {
                var card = Cur(ctx).FirstOrDefault(c => c.Zone != ZoneType.Stage && c.Def.IsCharacter);
                var slot = ctx.RememberedSlot ?? ctx.Source.LastSlot;
                if (card == null || slot is not { } s) return null;
                var p = State.Players[card.Owner];
                if (p.Stage[(int)s] != null && !ReferenceEquals(p.Stage[(int)s], ctx.Source)) return null;
                if (ReferenceEquals(p.Stage[(int)s], ctx.Source)) MoveTo(ctx.Source, ZoneType.WaitingRoom);
                card.TempLabels.Add("PlacedByChangeThisTurn");
                PutOnStageByEffect(card, s, CardOrientation.Standing, ctx, placedBy: "Change");
                return null;
            }
            case "revive":
            {
                var c = ctx.Source;
                if (c.Zone == ZoneType.Stage || c.LastSlot is not { } s) return null;
                if (State.Players[c.Owner].Stage[(int)s] != null) return null;
                PutOnStageByEffect(c, s, CardOrientation.Rested, ctx);
                return null;
            }
            case "marker_facedown":
            case "marker_faceup":
            case "markertargetsundersavedtarget_faceup":
            case "markertargetsundersavedtarget_facedown":
            {
                var host = n.Key.StartsWith("markertargets") ? ctx.Saved.FirstOrDefault() : ctx.Source;
                if (host == null || host.Zone != ZoneType.Stage) return null;
                foreach (var t in Cur(ctx).Where(t => !ReferenceEquals(t, host)).ToList())
                    PutUnder(t, host);
                return null;
            }
            case "flipfaceup":
            case "flipfacedown":
                return null;
            case "drawcard":
            {
                int k = a.Length == 0 ? 1 : Q(a, ctx);
                for (int i = 0; i < k; i++) DrawCard(me);
                Info($"P{ctx.Controller} 抽 {k} 张（{ctx.Source.Def.Name}）");
                return null;
            }
            case "discard":
            {
                var w = a.Split(' ', 2);
                int k = w.Length > 0 && w[0].Length > 0 ? Q(w[0], ctx) : 1;
                var cond = w.Length > 1 ? w[1] : "Any";
                var cands = me.Hand.Where(c => Eval(cond, ctx, c)).ToList();
                var picked = AskChooseCards(ctx.Controller, $"{ctx.Source.Def.Name}：选择 {Math.Min(k, cands.Count)} 张手牌放到休息室", cands, Math.Min(k, cands.Count), Math.Min(k, cands.Count));
                foreach (var c in picked) { MoveTo(c, ZoneType.WaitingRoom); FireDiscarded(c); }
                SetTargets(ctx, picked);
                return null;
            }
            case "mill":
            case "millopponent":
            case "millopponentbottom":
            case "millupto":
            {
                var p = n.Key.StartsWith("millopponent") ? opp : me;
                int k = Q(a, ctx);
                if (n.Key == "millupto")
                {
                    var opts = Enumerable.Range(0, k + 1).Select(i => new DecisionOption($"{i} 张")).ToList();
                    k = Ask(ctx.Controller, DecisionType.ChooseOption, $"{ctx.Source.Def.Name}：把牌库顶几张放到休息室？", opts, 1, 1)[0];
                }
                var moved = new List<CardInstance>();
                for (int i = 0; i < k && p.Deck.Count > 0; i++)
                {
                    var c = n.Key == "millopponentbottom" ? p.Deck[^1] : p.Deck[0];
                    MoveTo(c, ZoneType.WaitingRoom);
                    moved.Add(c);
                }
                if (moved.Count > 0)
                {
                    ctx.ThatCard = moved[^1];
                    ctx.Brainstormed.Clear();
                    ctx.Brainstormed.AddRange(moved);
                    SetTargets(ctx, moved);
                    Info($"P{p.Index} 牌库{(n.Key == "millopponentbottom" ? "底" : "顶")} {moved.Count} 张放到休息室：{string.Join("、", moved.Select(c => c.Def.Name))}");
                }
                return null;
            }
            case "heal":
            {
                int k = a.Length == 0 ? 1 : Q(a, ctx);
                for (int i = 0; i < k && me.Clock.Count > 0; i++)
                {
                    var c = me.Clock[^1];
                    MoveTo(c, ZoneType.WaitingRoom);
                    ctx.ThatCard = c;
                    Info($"P{ctx.Controller} 回复：{c} 从伤害区放到休息室");
                    FireWatchers(ctx.Controller, "onhealtowaitingroom", new TriggerInfo { Other = c });
                    FireWatchers(1 - ctx.Controller, "onopponenthealtowaitingroom", new TriggerInfo { Other = c });
                }
                return null;
            }
            case "burn":
            case "burnself":
            {
                int k = Q(a, ctx);
                var target = n.Key == "burnself" ? me : opp;
                ctx.LastDamageCanceled = DealDamage(target, k, $"{ctx.Source.Def.Name} 的效果");
                return null;
            }
            case "brainstorm":
            {
                int k = Q(a, ctx);
                ctx.Brainstormed.Clear();
                for (int i = 0; i < k; i++)
                {
                    var c = TakeTopOfDeck(me);
                    if (c == null) break;
                    MoveTo(c, ZoneType.WaitingRoom);
                    ctx.Brainstormed.Add(c);
                }
                ctx.BrainstormClimax = ctx.Brainstormed.Count(c => c.Def.IsClimax);
                Info($"P{ctx.Controller} 集中：翻开 {ctx.Brainstormed.Count} 张，名场面 {ctx.BrainstormClimax} 张");
                return null;
            }
            case "shufflelocation":
            {
                var loc = a.Trim();
                var p = loc.StartsWith("Opponent", StringComparison.OrdinalIgnoreCase) ? opp : me;
                var z = ZoneOf(loc);
                if (z?.Zone == ZoneType.Deck) { Shuffle(p); if (p == me) ctx.ShuffleDeckAtEnd = false; }
                else if (z is { } zz && zz.Zone != ZoneType.Stage) _rng.Shuffle(p.ZoneList(zz.Zone));
                return null;
            }
            case "blindstock":
            {
                var c = TakeTopOfDeck(me);
                if (c != null) MoveTo(c, ZoneType.Stock);
                return null;
            }
            case "refresh":
            case "opponentrefresh":
            {
                var p = n.Key == "opponentrefresh" ? opp : me;
                foreach (var c in p.WaitingRoom.ToList()) MoveTo(c, ZoneType.Deck, toBottom: true);
                Shuffle(p);
                return null;
            }
            case "bond":
            {
                var cands = me.WaitingRoom.Where(c => NameMatches(c, a)).ToList();
                var picked = AskChooseCards(ctx.Controller, $"羁绊：从休息室选择 {a} 加入手牌", cands, Math.Min(1, cands.Count), 1);
                foreach (var c in picked) { MoveTo(c, ZoneType.Hand); Info($"羁绊：{c} 加入手牌"); }
                return null;
            }
            case "scheduleeffect":
            case "scheduleeffectfortargets":
            {
                var w = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _scheduled.Add(new ScheduledEffect
                {
                    Timing = w.Length > 0 ? w[0] : "EndOfTurn",
                    Source = ctx.Source,
                    Controller = ctx.Controller,
                    Abilities = AbilitiesInBlock(n),
                    Targets = n.Key == "scheduleeffectfortargets" ? Cur(ctx).ToList() : null,
                    CreatedTurn = State.TurnNumber,
                });
                return null;
            }
            case "scheduleeffectuntil":
            {
                // ScheduleEffectUntil 时机 期限 { Effect: {…} }：在期限前，时机发生时执行。作为临时【自】能力给目标。
                var w = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var trig = w.Length > 0 ? w[0] : "";
                foreach (var ab in AbilitiesInBlock(n))
                {
                    var auto = TempAuto(ab, trig);
                    foreach (var t in Cur(ctx).ToList()) AddModifier(t, ModKind.Ability, 0, null, auto, ctx);
                }
                return null;
            }
            case "blockopponentencore": AddPlayerFlag(opp, "CannotEncore", ctx); return null;
            case "blockencore": AddPlayerFlag(me, "CannotEncore", ctx); return null;
            case "blockopponentevents": AddPlayerFlag(opp, "CannotPlayEvents", ctx); return null;
            case "blockopponentbackup": AddPlayerFlag(opp, "CannotUseBackup", ctx); return null;
            case "blockopponentact": AddPlayerFlag(opp, "CannotUseAct", ctx); return null;

            case "tag": case "aichoice": case "message": case "opponentmessage": case "ainote": case "effectnote":
            case "quick": case "text": case "image": case "wait":
                return null;
            default:
                NoteUnknown(n);
                return null;
        }
    }

    private void ExecTail2(ScriptNode n, EffectContext ctx, int skip)
    {
        var w = n.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length <= skip || int.TryParse(w[skip], out _)) return;
        var rest = string.Join(" ", w.Skip(skip));
        var parts = rest.Split(' ', 2);
        ExecNode(new ScriptNode { Name = parts[0], Args = parts.Length > 1 ? parts[1] : "", Line = n.Line, Block = n.Block }, ctx);
    }

    private readonly HashSet<string> _unknownReported = new(StringComparer.OrdinalIgnoreCase);

    private void NoteUnknown(ScriptNode n) => NoteScript($"暂不支持的指令 {n.Name}，已跳过");

    /// <summary>脚本问题只在一局里第一次遇到时记日志。</summary>
    internal void NoteScript(string msg)
    {
        if (_unknownReported.Add(msg)) Info("[脚本] " + msg);
    }

    private static List<string> ExtractNumberedOptions(string prompt)
    {
        var list = new List<string>();
        foreach (var line in prompt.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 2 && char.IsDigit(t[0]) && t[1] == '.') list.Add(t);
        }
        return list;
    }

    /// <summary>对手的卡要能被选择（CannotBeChosen）。</summary>
    private List<CardInstance> Choosable(IEnumerable<CardInstance> cards, EffectContext ctx) =>
        cards.Where(c => c.Owner == ctx.Controller || c.Zone != ZoneType.Stage || !c.Has("CannotBeChosen")).ToList();

    private CardInstance? BattleOpponentFor(EffectContext ctx)
    {
        var at = _attack ?? _lastBattle;
        if (at == null || at.Type != AttackType.Front) return null;
        CardInstance? r;
        if (ReferenceEquals(at.Attacker, ctx.Self)) r = at.Defender;
        else if (ReferenceEquals(at.Defender, ctx.Self)) r = at.Attacker;
        else r = at.Attacker.Owner != ctx.Controller ? at.Attacker : at.Defender;
        return r != null && r.Zone == ZoneType.Stage ? r : null;
    }

    private List<CardInstance> TopOrBottom(string loc, int k, bool top, EffectContext ctx)
    {
        var cards = CardsIn(loc, ctx.Controller, ctx.Self);
        k = Math.Max(0, Math.Min(k, cards.Count));
        var z = ZoneOf(loc)?.Zone;
        // 牌库 [0] 是顶，其余区域末尾是顶。
        bool topIsFirst = z == ZoneType.Deck;
        if (top == topIsFirst) return cards.Take(k).ToList();
        var r = cards.Skip(cards.Count - k).ToList();
        r.Reverse();
        return r;
    }

    private void ApplyStat(ModKind kind, int v, EffectContext ctx)
    {
        foreach (var t in Recipients(ctx).ToList())
            AddModifier(t, kind, v, null, null, ctx);
        ctx.UntilOpponentsTurn = false;
    }

    /// <summary>给卡加修正：【永】模式写进 Cont 层，否则加有期限的修正。</summary>
    private void AddModifier(CardInstance t, ModKind kind, int v, string? text, Ability? ab, EffectContext ctx)
    {
        if (ctx.Continuous)
        {
            var c = t.Cont;
            switch (kind)
            {
                case ModKind.Power: c.Power += v; break;
                case ModKind.Soul: c.Soul += v; break;
                case ModKind.Level: c.Level += v; break;
                case ModKind.Cost: c.Cost += v; break;
                case ModKind.Special: c.Specials.Add(text!); break;
                case ModKind.Trait: c.Traits.Add(text!); break;
                case ModKind.Trigger: c.Triggers.Add(text!); break;
                case ModKind.Ability: c.Abilities.Add((ab!, ctx.Source)); break;
                case ModKind.Name: c.Traits.Add("__name:" + text); break;
            }
            return;
        }
        // 有期限的效果只对舞台上的卡（和手牌、记忆区里会用到的卡）有意义；离开区域时清除。
        int expires = State.TurnNumber + (ctx.UntilOpponentsTurn ? 1 : 0);
        t.Modifiers.Add(new TimedModifier { Kind = kind, Value = v, Text = text, Ability = ab, Granter = ctx.Source, ExpiresAfterTurn = expires });
        if (kind == ModKind.Power && v != 0) Info($"{t.Def.Name} 战力 {(v > 0 ? "+" : "")}{v}");
        else if (kind == ModKind.Soul && v != 0) Info($"{t.Def.Name} 魂 {(v > 0 ? "+" : "")}{v}");
        else if (kind == ModKind.Ability) Info($"{t.Def.Name} 获得能力：{ab!.Text ?? ab.ToString()}");
        else if (kind == ModKind.Special) Info($"{t.Def.Name} 获得：{text}");
    }

    private void AddPlayerFlag(PlayerState p, string flag, EffectContext ctx)
    {
        p.Modifiers.Add(new TimedModifier { Kind = ModKind.Special, Text = flag, ExpiresAfterTurn = State.TurnNumber + (ctx.UntilOpponentsTurn ? 1 : 0) });
        ctx.UntilOpponentsTurn = false;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Ability, string), Ability> TempAutoCache = new();

    private static Ability TempAuto(Ability effect, string trigger) =>
        TempAutoCache.GetOrAdd((effect, trigger), k => new Ability
        {
            Kind = AbilityKind.Auto,
            Trigger = k.Item2,
            Body = k.Item1.Body,
            Text = k.Item1.Text,
        });

    /// <summary>MoveCard 的目的地：卡总是去它主人的区域（OpponentX 只是写法）。返回是否移动了。</summary>
    internal bool MoveToNamedZone(CardInstance t, string loc, EffectContext ctx)
    {
        var key = loc.Trim().ToLowerInvariant();
        if (key is "revealed" or "") return false;
        if (key.Contains("stage"))
        {
            if (t.Zone == ZoneType.Stage || !t.Def.IsCharacter) return false;
            var slot = key.Contains("middle") ? StageSlot.FrontCenter : (StageSlot?)null;
            var p = State.Players[t.Owner];
            if (slot == null)
            {
                var slots = StageSlots.All.Where(s => !key.Contains("center") || s.IsFront()).Where(s => !key.Contains("back") || !s.IsFront()).ToList();
                slot = AskSlot(t.Owner, $"选择放置 {t.Def.Name} 的格子", slots);
            }
            PutOnStageByEffect(t, slot.Value, CardOrientation.Standing, ctx);
            return true;
        }
        var z = ZoneOf(loc);
        if (z == null) { NoteScript($"未知区域 {loc}"); return false; }
        if (t.Zone == ZoneType.Stage && z.Value.Zone == ZoneType.Hand && t.Has("CannotReturnToHand")) return false;
        if (t.Zone == ZoneType.Stage && z.Value.Zone == ZoneType.Memory && t.Has("CannotGoToMemory")) return false;
        if (z.Value.Zone == ZoneType.Marker) { PutUnder(t, ctx.Source); return true; }
        if (t.Zone == z.Value.Zone && z.Value.Zone != ZoneType.Deck) return false;
        MoveTo(t, z.Value.Zone, toBottom: z.Value.Bottom);
        return true;
    }

    private void ExchangeCards(CardInstance a, CardInstance b, EffectContext ctx)
    {
        if (a.Zone == ZoneType.Stage || b.Zone == ZoneType.Stage)
        {
            var onStage = a.Zone == ZoneType.Stage ? a : b;
            var other = ReferenceEquals(onStage, a) ? b : a;
            if (other.Zone == ZoneType.Stage || !other.Def.IsCharacter) return;
            var p = State.Players[onStage.Owner];
            var slot = p.SlotOf(onStage)!.Value;
            var otherZone = other.Zone;
            MoveTo(onStage, otherZone == ZoneType.Marker ? ZoneType.WaitingRoom : otherZone);
            PutOnStageByEffect(other, slot, CardOrientation.Standing, ctx);
            return;
        }
        var za = a.Zone;
        var zb = b.Zone;
        if (za == zb) return;
        MoveTo(a, zb);
        MoveTo(b, za);
        Info($"交换：{a.Def.Name} ↔ {b.Def.Name}");
    }

    /// <summary>把卡放到某个角色下面作为标记。</summary>
    private void PutUnder(CardInstance card, CardInstance host)
    {
        var from = Where(card);
        RemoveFromCurrentZone(card);
        card.ResetTransient();
        card.Zone = ZoneType.Marker;
        card.MarkerHost = host;
        host.Markers.Add(card);
        var hostSlot = State.Players[host.Owner].SlotOf(host);
        Raise(new CardMovedEvent(card.Owner, card.InstanceId, from.Zone, from.Index, from.Slot, ZoneType.Marker, host.Markers.Count - 1, hostSlot, card.Orientation));
        Info($"{card.Def.Name} 放到 {host.Def.Name} 下面作为标记");
        FireFor(host, "ongainmarker", new TriggerInfo { Other = card });
    }
}

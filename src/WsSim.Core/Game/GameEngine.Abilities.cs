using WsSim.Core.Effects;

namespace WsSim.Core.Game;

/// <summary>效果脚本：【自】的触发和处理、【永】的计算、【起】、事件卡、预约效果。</summary>
public sealed partial class GameEngine
{
    private readonly List<PendingAbility> _pending = new();
    private readonly List<ScheduledEffect> _scheduled = new();
    /// <summary>正在进行的攻击（攻击宣告到战斗步骤结束）。</summary>
    private AttackContext? _attack;
    /// <summary>最近一次战斗（倒置后触发的能力在战斗结束后才处理，还要知道战斗对手）。</summary>
    private AttackContext? _lastBattle;
    private bool _updatingContinuous;

    /// <summary>是否执行卡片效果脚本（关掉就是 P1 的纯规则引擎）。</summary>
    public bool EffectsEnabled => Options.EnableEffects;

    /// <summary>等待处理的【自】能力（只读，给界面和测试看）。</summary>
    public IReadOnlyList<PendingAbility> Pending => _pending;

    // ------------------------------------------------------------------ 能力来源

    private static bool HasFlag(CardInstance c, string flag) => ScriptCache.Get(c.Def).Flag(flag.ToLowerInvariant()) != null;

    /// <summary>会对“我方的……时”这类时机作出反应的卡：舞台、名场面区，以及标了 InMemory 的记忆区卡。</summary>
    private IEnumerable<CardInstance> Watchers(int player)
    {
        var p = State.Players[player];
        foreach (var c in p.StageCards.ToList()) yield return c;
        foreach (var c in p.ClimaxArea.ToList()) yield return c;
        foreach (var c in p.Memory.ToList()) if (HasFlag(c, "InMemory")) yield return c;
    }

    private static bool HeaderMatches(Ability ab, string key, TriggerInfo info)
    {
        var h = ab.HeaderArgs.Trim();
        if (h.Length == 0) return true;
        switch (key)
        {
            case "onspecificclimaxplay":
                return info.Climax != null && NameMatches(info.Climax, h);
            case "placedby":
            case "placedbyif":
                return info.PlacedBy != null && (string.Equals(h, info.PlacedBy, StringComparison.OrdinalIgnoreCase)
                                                 || info.PlacedBy.Split('|').Any(x => string.Equals(x, h, StringComparison.OrdinalIgnoreCase)));
            case "onotherplacedonstagefromhand":
            case "onotherplacedonstagefromwaitingroom":
                return info.Other != null && NameMatches(info.Other, h);
            default:
                return true;
        }
    }

    /// <summary>检查卡 <paramref name="c"/> 有没有对时机 <paramref name="key"/> 反应的【自】，有就加入待处理。</summary>
    private void FireFor(CardInstance c, string key, TriggerInfo info, IReadOnlyList<(Ability Ability, CardInstance Granter)>? abilities = null)
    {
        if (!EffectsEnabled) return;
        foreach (var (ab, _) in abilities ?? c.Abilities.ToList())
        {
            if (ab.Kind != AbilityKind.Auto || ab.TriggerKey.StartsWith("encore")) continue;
            if (!ab.TriggerKeys.Contains(key)) continue;
            if (!HeaderMatches(ab, key, info)) continue;
            _pending.Add(new PendingAbility { Source = c, Ability = ab, Controller = c.Owner, Info = new TriggerInfo
            {
                Trigger = key, Other = info.Other, Climax = info.Climax, PlacedBy = info.PlacedBy, FromSlot = info.FromSlot,
            } });
        }
    }

    private void FireWatchers(int player, string key, TriggerInfo info, CardInstance? except = null)
    {
        foreach (var c in Watchers(player))
            if (!ReferenceEquals(c, except)) FireFor(c, key, info);
    }

    // ------------------------------------------------------------------ 各种事件

    private void OnCardPlayed(CardInstance c)
    {
        c.PlayedFromHandTurn = State.TurnNumber;
        var info = new TriggerInfo { Other = c };
        FireFor(c, "onplay", info);
        FireWatchers(c.Owner, "onotherplacedonstagefromhand", info, c);
    }

    private void OnPlacedByEffect(CardInstance c, ZoneType from, string? placedBy)
    {
        var info = new TriggerInfo { Other = c, PlacedBy = placedBy };
        FireFor(c, "onplacedonstagefrom" + from.ToString().ToLowerInvariant(), info);
        if (placedBy != null)
        {
            FireFor(c, "placedby", info);
            FireFor(c, "placedbyif", info);
        }
        if (from == ZoneType.WaitingRoom) FireWatchers(c.Owner, "onotherplacedonstagefromwaitingroom", info, c);
    }

    private void OnClimaxPlayed(CardInstance cx)
    {
        var info = new TriggerInfo { Climax = cx, Other = cx };
        FireFor(cx, "onplay", info);
        foreach (var c in Watchers(cx.Owner).Where(c => !ReferenceEquals(c, cx)).ToList())
        {
            FireFor(c, "onclimaxplay", info);
            FireFor(c, "onspecificclimaxplay", info);
        }
        FireWatchers(1 - cx.Owner, "onopponentclimaxplay", info);
    }

    private void OnAttackDeclared(AttackContext at)
    {
        var a = at.Attacker;
        var info = new TriggerInfo { Other = a };
        FireFor(a, "onattack", info);
        if (at.Type == AttackType.Direct) FireFor(a, "ondirectattack", info);
        if (at.Type == AttackType.Front) FireFor(a, "onfrontalattack", info);
        if (at.Type == AttackType.Side) FireFor(a, "onsideattack", info);
        foreach (var c in Watchers(a.Owner).Where(c => !ReferenceEquals(c, a)).ToList())
        {
            FireFor(c, "onotherattacks", info);
            FireFor(c, "onothercharacterattacks", info);
        }
        if (at.Defender is { } d)
        {
            var dinfo = new TriggerInfo { Other = a };
            if (at.Type == AttackType.Front)
            {
                FireFor(d, "onfrontalattacked", dinfo);
                foreach (var c in Watchers(d.Owner).Where(c => !ReferenceEquals(c, d)).ToList())
                    FireFor(c, "onotherfrontalattacked", new TriggerInfo { Other = d });
            }
            FireFor(d, "onfacingattacks", dinfo);
            FireFor(d, "onattacked", dinfo);
        }
    }

    /// <summary>把舞台上的角色倒置（战斗或效果），并触发相关能力。CannotBeReversed 的角色不会倒置。</summary>
    internal bool ReverseCard(CardInstance c, bool inBattle, bool byEffect = false)
    {
        if (c.Zone != ZoneType.Stage || c.Orientation == CardOrientation.Reversed) return false;
        if (c.Has("CannotBeReversed")) { Info($"{c} 不会倒置"); return false; }
        if (byEffect && c.Has("CannotBeReversedByAutoEffects")) return false;
        SetOrientation(c, CardOrientation.Reversed);
        Info($"{c} 倒置");
        var info = new TriggerInfo { Other = c };
        FireFor(c, "onreversed", info);
        if (inBattle) FireFor(c, "onreversedinbattle", info);
        foreach (var o in Watchers(c.Owner).Where(o => !ReferenceEquals(o, c)).ToList())
            FireFor(o, "onotherreversed", info);
        FireWatchers(1 - c.Owner, "onotheropponentreversed", info);
        if (inBattle && BattleOpponentOf(c) is { } bo && bo.Zone == ZoneType.Stage)
        {
            FireFor(bo, "onreverseopponent", info);
            FireFor(bo, "onopponentreverse", info);
            FireFor(bo, "onreverseopponent_conditional", info);
        }
        return true;
    }

    /// <summary>卡离开舞台后调用（<paramref name="abilities"/> 是离开前的能力）。</summary>
    private void OnLeftStage(CardInstance c, ZoneType to, StageSlot slot, List<(Ability, CardInstance)> abilities)
    {
        var info = new TriggerInfo { Other = c, FromSlot = slot };
        if (to == ZoneType.WaitingRoom)
        {
            FireFor(c, "onsentfromstagetowaitingroom", info, abilities);
            FireWatchers(c.Owner, "onothersentfromstagetowaitingroom", info, c);
        }
        FireFor(c, "onsentfromstagetoelsewhere", info, abilities);
        FireFor(c, "onleavestage", info, abilities);
    }

    private void FireDiscarded(CardInstance c)
    {
        FireFor(c, "ondiscard", new TriggerInfo { Other = c });
    }

    private void FirePhaseStart(Phase phase)
    {
        if (!EffectsEnabled) return;
        var name = phase switch
        {
            Phase.Encore => "Encore",
            _ => phase.ToString(),
        };
        int act = State.ActivePlayer;
        var info = new TriggerInfo();
        foreach (var c in Watchers(act).ToList())
        {
            FireFor(c, $"start{name}phase".ToLowerInvariant(), info);
            FireFor(c, $"startmy{name}phase".ToLowerInvariant(), info);
            if (phase == Phase.Encore)
            {
                FireFor(c, "startencorestep", info);
                FireFor(c, "startmyencorestep", info);
                FireFor(c, "startmyencorephase", info);
            }
        }
        foreach (var c in Watchers(1 - act).ToList())
        {
            FireFor(c, $"startopponent{name}phase".ToLowerInvariant(), info);
            FireFor(c, $"startopponents{name}phase".ToLowerInvariant(), info);
            if (phase == Phase.Encore)
            {
                FireFor(c, "startencorestep", info);
                FireFor(c, "startencorephase", info);
                FireFor(c, "startopponentencorestep", info);
            }
        }
        RunScheduled(phase switch
        {
            Phase.Encore => new[] { "StartEncoreStep", "StartMyEncoreStep", "StartEncorePhase" },
            Phase.End => new[] { "EndOfTurn", "EndOfMyTurn", "StartEndPhase" },
            _ => new[] { $"Start{name}Phase", $"StartMy{name}Phase" },
        });
    }

    private void RunScheduled(string[] timings)
    {
        foreach (var s in _scheduled.ToList())
        {
            bool mine = s.Controller == State.ActivePlayer;
            bool hit = timings.Any(t => string.Equals(t, s.Timing, StringComparison.OrdinalIgnoreCase)
                                        && (!t.Contains("My", StringComparison.Ordinal) || mine));
            if (!hit) continue;
            _scheduled.Remove(s);
            foreach (var ab in s.Abilities)
                _pending.Add(new PendingAbility { Source = s.Source, Ability = ab, Controller = s.Controller, Info = new TriggerInfo { Trigger = s.Timing }, Targets = s.Targets });
        }
    }

    // ------------------------------------------------------------------ 处理待处理的【自】

    /// <summary>处理一个待处理的【自】能力（回合玩家的先）。返回是否处理了。</summary>
    private bool ResolveOnePending()
    {
        if (_pending.Count == 0) return false;
        int who = _pending.Any(p => p.Controller == State.ActivePlayer) ? State.ActivePlayer : 1 - State.ActivePlayer;
        var mine = _pending.Where(p => p.Controller == who).ToList();
        PendingAbility pa;
        if (mine.Count == 1) pa = mine[0];
        else
        {
            var opts = mine.Select(p => new DecisionOption($"{p.Source.Def.Name}：{Short(p.Ability.Text ?? p.Ability.ToString())}")
            { CardInstanceId = p.Source.InstanceId }).ToList();
            pa = mine[Ask(who, DecisionType.ChooseAbility, "选择先处理的【自】能力", opts, 1, 1)[0]];
        }
        _pending.Remove(pa);
        ResolvePending(pa);
        return true;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    private void ResolvePending(PendingAbility pa)
    {
        var ab = pa.Ability;
        var src = pa.Source;
        if (ab.TimesPerTurn is int limit)
        {
            src.UsesThisTurn.TryGetValue(ab, out var used);
            if (used >= limit) return;
            src.UsesThisTurn[ab] = used + 1;
        }
        var ctx = new EffectContext
        {
            Source = src,
            Self = pa.Self ?? src,
            Controller = pa.Controller,
            Ability = ab,
            Trigger = pa.Info,
        };
        if (pa.Targets != null)
        {
            ctx.Targets = pa.Targets.ToList();
            ctx.HasTargets = true;
        }
        var req = ab.Modifiers.FirstOrDefault(m => m.Key == "activationrequirement");
        if (req != null && !Eval(req.Args, ctx, src)) return;
        Info($"【自】{src.Def.Name}：{Short(ab.Text ?? ab.ToString())}");
        RunAbilityBody(ab.Body, ctx);
    }

    private void RunAbilityBody(List<ScriptNode> body, EffectContext ctx)
    {
        Exec(body, ctx);
        if (ctx.ShuffleDeckAtEnd) Shuffle(State.Players[ctx.Controller]);
    }

    // ------------------------------------------------------------------ 【永】

    /// <summary>重新计算所有【永】效果（两遍，让【永】给的【永】也生效）。</summary>
    internal void UpdateContinuous()
    {
        if (!EffectsEnabled || _updatingContinuous) return;
        _updatingContinuous = true;
        var savedLog = _log.Count;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var granted = new Dictionary<CardInstance, List<(Ability, CardInstance)>>(ReferenceEqualityComparer.Instance);
                foreach (var c in AllCards())
                {
                    if (c.Cont.Abilities.Count > 0) granted[c] = c.Cont.Abilities.ToList();
                    c.Cont.Clear();
                }
                foreach (var p in State.Players)
                {
                    foreach (var c in ContSources(p).ToList())
                    {
                        var abilities = ScriptCache.Get(c.Def).Abilities.Select(a => (a, c)).ToList();
                        if (c.Zone == ZoneType.Stage)
                        {
                            abilities.AddRange(c.Modifiers.Where(m => m.Kind == ModKind.Ability).Select(m => (m.Ability!, c)));
                            if (granted.TryGetValue(c, out var g)) abilities.AddRange(g);
                        }
                        foreach (var (ab, _) in abilities)
                            if (ab.Kind == AbilityKind.Cont) ApplyCont(c, ab);
                    }
                }
            }
        }
        finally
        {
            // 【永】计算不留日志（除了第一次遇到的未知指令）。
            if (_log.Count > savedLog)
            {
                var keep = _log.Skip(savedLog).Where(l => l.StartsWith("[脚本]")).ToList();
                _log.RemoveRange(savedLog, _log.Count - savedLog);
                _log.AddRange(keep);
            }
            _updatingContinuous = false;
        }
    }

    private IEnumerable<CardInstance> AllCards()
    {
        foreach (var p in State.Players)
        {
            foreach (var c in p.Stage) if (c != null) yield return c;
            foreach (var z in new[] { p.Hand, p.ClimaxArea, p.Memory, p.Level, p.Clock, p.WaitingRoom, p.Stock, p.Resolution, p.Deck })
                foreach (var c in z) yield return c;
        }
    }

    private static IEnumerable<CardInstance> ContSources(PlayerState p)
    {
        foreach (var c in p.Stage) if (c != null) yield return c;
        foreach (var c in p.ClimaxArea) yield return c;
        foreach (var c in p.Hand) yield return c;
        foreach (var c in p.Memory) yield return c;
    }

    private void ApplyCont(CardInstance src, Ability ab)
    {
        var p = State.Players[src.Owner];
        var kind = ab.TriggerKey;
        bool toSelf = kind is not ("global" or "memory" or "giveselfinmemory" or "during");
        EffectContext Ctx(CardInstance self) => new() { Source = src, Self = self, Controller = src.Owner, Ability = ab, Continuous = true, ApplyToSelf = toSelf };
        switch (kind)
        {
            case "giveself":
            case "global":
            case "during":
                if (src.Zone == ZoneType.Stage) Exec(ab.Body, Ctx(src));
                break;
            case "giveall":
            case "giveallyourcards":
                if (src.Zone is ZoneType.Stage or ZoneType.ClimaxArea)
                    foreach (var x in p.StageCards.ToList()) Exec(ab.Body, Ctx(x));
                break;
            case "assist":
                if (src.Zone == ZoneType.Stage)
                    foreach (var x in InFrontOf(src).ToList()) Exec(ab.Body, Ctx(x));
                break;
            case "givefacing":
                if (FacingOf(src) is { } f) Exec(ab.Body, Ctx(f));
                break;
            case "inhand":
                if (src.Zone == ZoneType.Hand) Exec(ab.Body, Ctx(src));
                break;
            case "givehand":
                if (src.Zone == ZoneType.Stage)
                    foreach (var x in p.Hand.ToList()) Exec(ab.Body, Ctx(x));
                break;
            case "giveselfeverywhere":
                Exec(ab.Body, Ctx(src));
                break;
            case "memory":
            case "giveselfinmemory":
                if (src.Zone == ZoneType.Memory) Exec(ab.Body, Ctx(src));
                break;
            default:
                if (src.Zone == ZoneType.Stage) Exec(ab.Body, Ctx(src));
                break;
        }
    }

    // ------------------------------------------------------------------ 【起】

    /// <summary>这张卡现在能发动的【起】能力（序号是 <see cref="CardInstance.Abilities"/> 里的位置）。</summary>
    internal List<(int Index, Ability Ability)> UsableActs(CardInstance c)
    {
        var list = new List<(int, Ability)>();
        if (!EffectsEnabled) return list;
        var p = State.Players[c.Owner];
        if (p.HasFlag("CannotUseAct")) return list;
        int i = 0;
        foreach (var (ab, _) in c.Abilities.ToList())
        {
            int idx = i++;
            if (ab.Kind != AbilityKind.Act || ab.TriggerKey == "backup") continue;
            if (ab.TimesPerTurn is int limit && c.UsesThisTurn.TryGetValue(ab, out var used) && used >= limit) continue;
            var ctx = new EffectContext { Source = c, Self = c, Controller = c.Owner, Ability = ab };
            var req = ab.Modifiers.FirstOrDefault(m => m.Key == "activationrequirement");
            if (req != null && !Eval(req.Args, ctx, c)) continue;
            if (!CanPayCost(ab.HeaderArgs, ctx)) continue;
            list.Add((idx, ab));
        }
        return list;
    }

    internal void ActivateAbility(CardInstance c, int index)
    {
        var all = c.Abilities.ToList();
        if (index < 0 || index >= all.Count) return;
        var ab = all[index].Ability;
        var ctx = new EffectContext { Source = c, Self = c, Controller = c.Owner, Ability = ab };
        if (!CanPayCost(ab.HeaderArgs, ctx)) return;
        if (ab.TimesPerTurn is int)
        {
            c.UsesThisTurn.TryGetValue(ab, out var used);
            c.UsesThisTurn[ab] = used + 1;
        }
        Info($"P{c.Owner} 发动 {c.Def.Name} 的【起】：{Short(ab.Text ?? ab.ToString())}");
        PayCost(ParseCost(ab.HeaderArgs), ctx);
        RunAbilityBody(ab.Body, ctx);
        FireWatchers(c.Owner, "onuseact", new TriggerInfo { Other = c });
    }

    // ------------------------------------------------------------------ 事件卡

    /// <summary>事件卡的使用条件（Requirement）和“不能使用事件”。</summary>
    internal bool EventRequirementMet(PlayerState me, CardInstance c)
    {
        if (!EffectsEnabled) return true;
        if (me.HasFlag("CannotPlayEvents") || me.StageCards.Any(x => x.Has("CannotUseEvents"))) return false;
        var req = ScriptCache.Get(c.Def).Requirement;
        if (req == null) return true;
        return Eval(req, new EffectContext { Source = c, Self = c, Controller = me.Index }, c);
    }

    private void ResolveEvent(PlayerState me, CardInstance card)
    {
        var script = ScriptCache.Get(card.Def);
        foreach (var ab in script.Of(AbilityKind.Event).ToList())
        {
            var ctx = new EffectContext { Source = card, Self = card, Controller = me.Index, Ability = ab };
            RunAbilityBody(ab.Body, ctx);
        }
        FireWatchers(me.Index, "onuseevent", new TriggerInfo { Other = card });
    }

    // ------------------------------------------------------------------ 放上舞台

    /// <summary>效果把卡放到舞台（不是“使用”，不触发 OnPlay）。格子上已有角色时那张放到休息室。</summary>
    internal void PutOnStageByEffect(CardInstance card, StageSlot slot, CardOrientation o, EffectContext ctx, string? placedBy = null)
    {
        var p = State.Players[card.Owner];
        var from = card.Zone;
        if (p.Stage[(int)slot] is { } old && !ReferenceEquals(old, card)) MoveTo(old, ZoneType.WaitingRoom);
        if (card.Zone == ZoneType.Stage)
        {
            var cur = p.SlotOf(card)!.Value;
            if (cur != slot) MoveOnStage(p, cur, slot);
            return;
        }
        PlaceOnStage(card, slot, o);
        ctx.ThatCard = card;
        Info($"{ctx.Source.Def.Name}：{card.Def.Name} 放到{slot.ToChinese()}" + (o == CardOrientation.Rested ? "（横置）" : ""));
        OnPlacedByEffect(card, from, placedBy ?? ctx.Source.Def.Name);
    }

    // ------------------------------------------------------------------ 有期限效果的结束

    private void ExpireModifiers()
    {
        int t = State.TurnNumber;
        foreach (var c in AllCards())
        {
            c.Modifiers.RemoveAll(m => m.ExpiresAfterTurn <= t);
            c.UsesThisTurn.Clear();
            c.TempLabels.Clear();
        }
        foreach (var p in State.Players)
            p.Modifiers.RemoveAll(m => m.ExpiresAfterTurn <= t);
        _scheduled.RemoveAll(s => s.Timing.StartsWith("End", StringComparison.OrdinalIgnoreCase) && s.CreatedTurn <= t);
    }

    // ------------------------------------------------------------------ 选择

    /// <summary>从候选中选 min–max 张。只能选 1 张且可以不选时加一个“不选”选项。</summary>
    internal List<CardInstance> AskChooseCards(int player, string prompt, IReadOnlyList<CardInstance> cands, int min, int max)
    {
        max = Math.Min(max, cands.Count);
        min = Math.Min(min, max);
        if (max <= 0) return new List<CardInstance>();
        if (min == max && max == cands.Count && min > 0 && cands.Count == 1)
        {
            // 只有一个必须选的候选：仍然问一下，让界面能显示（AI 直接选）。
        }
        var opts = cands.Select(CardOption).ToList();
        if (max == 1)
        {
            if (min == 0) opts.Add(new DecisionOption("不选") { IsPass = true });
            var ans = Ask(player, DecisionType.ChooseCard, prompt, opts, 1, 1);
            return opts[ans[0]].IsPass ? new List<CardInstance>() : new List<CardInstance> { cands[ans[0]] };
        }
        var picks = Ask(player, DecisionType.ChooseCards, prompt, opts, min, max);
        return picks.OrderBy(i => i).Select(i => cands[i]).ToList();
    }
}

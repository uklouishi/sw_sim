using WsSim.Core.Cards;
using WsSim.Core.Effects;

namespace WsSim.Core.Game;

public sealed partial class GameEngine
{
    /// <summary>一次攻击的临时信息。</summary>
    private sealed class AttackContext
    {
        public required CardInstance Attacker { get; init; }
        public required StageSlot AttackerSlot { get; init; }
        public required AttackType Type { get; init; }
        public CardInstance? Defender { get; init; }
        /// <summary>宣告时决定的魂修正：侧面攻击 −对面角色等级，直接攻击 +1。</summary>
        public int DeclarationSoulModifier { get; init; }
        public int ShotCount { get; set; }
    }

    /// <summary>加一个“直到回合结束为止”的修正（判定的魂图示、助太刀等）。</summary>
    internal void AddUntilEndOfTurn(CardInstance c, ModKind kind, int value) =>
        c.Modifiers.Add(new TimedModifier { Kind = kind, Value = value, ExpiresAfterTurn = State.TurnNumber });

    internal void AttackPhase(PlayerState me)
    {
        if (State.TurnNumber == 1 && me.Index == State.FirstPlayer && Options.FirstTurnAttackLimit <= 0) return;
        var opp = State.Opponent(me.Index);
        bool firstTurnOfFirstPlayer = State.TurnNumber == 1 && me.Index == State.FirstPlayer;
        while (true)
        {
            if (firstTurnOfFirstPlayer && State.AttacksThisTurn >= Options.FirstTurnAttackLimit) return;
            var opts = BuildAttackOptions(me, opp);
            if (opts.Count == 1) return; // 只剩“结束攻击”
            var ans = Ask(me.Index, DecisionType.AttackDeclaration, "攻击阶段：选择攻击的角色和方式", opts, 1, 1);
            var o = opts[ans[0]];
            if (o.IsPass) return;
            PerformAttack(me, opp, o.Slot!.Value, o.AttackType!.Value);
        }
    }

    internal List<DecisionOption> BuildAttackOptions(PlayerState me, PlayerState opp)
    {
        UpdateContinuous();
        var opts = new List<DecisionOption>();
        foreach (var s in StageSlots.Front)
        {
            var a = me.Stage[(int)s];
            if (a == null || a.Orientation != CardOrientation.Standing || a.Has("CannotAttack")) continue;
            var facing = opp.Stage[(int)s.Facing()];
            if (facing != null)
            {
                if (!a.Has("CannotFrontalAttack"))
                    opts.Add(new DecisionOption($"{a.Def.Name} 正面攻击 {facing.Def.Name}") { Slot = s, AttackType = AttackType.Front, CardInstanceId = a.InstanceId });
                if (!a.Has("CannotSideAttack"))
                    opts.Add(new DecisionOption($"{a.Def.Name} 侧面攻击 {facing.Def.Name}") { Slot = s, AttackType = AttackType.Side, CardInstanceId = a.InstanceId });
            }
            else
            {
                opts.Add(new DecisionOption($"{a.Def.Name} 直接攻击") { Slot = s, AttackType = AttackType.Direct, CardInstanceId = a.InstanceId });
            }
        }
        opts.Add(new DecisionOption("结束攻击") { IsPass = true });
        return opts;
    }

    internal void PerformAttack(PlayerState me, PlayerState opp, StageSlot slot, AttackType type)
    {
        var attacker = me.Stage[(int)slot] ?? throw new InvalidOperationException("攻击格没有角色");
        if (attacker.Orientation != CardOrientation.Standing) throw new InvalidOperationException("攻击角色不是正置");
        var defender = opp.Stage[(int)slot.Facing()];
        if ((type == AttackType.Direct) != (defender == null)) throw new InvalidOperationException("攻击方式与对面是否有角色不符");

        // 宣告：横置攻击角色。
        SetOrientation(attacker, CardOrientation.Rested);
        State.AttacksThisTurn++;
        Raise(new AttackDeclaredEvent(me.Index, slot, type, defender != null ? slot.Facing() : null));
        var ctx = new AttackContext
        {
            Attacker = attacker,
            AttackerSlot = slot,
            Type = type,
            Defender = defender,
            DeclarationSoulModifier = type switch
            {
                AttackType.Side => attacker.Has("FreeSideAttack") ? 0 : -defender!.Level,
                AttackType.Direct => 1,
                _ => 0,
            },
        };
        string typeName = type switch { AttackType.Front => "正面攻击", AttackType.Side => "侧面攻击", _ => "直接攻击" };
        Info($"P{me.Index} 的 {attacker} {typeName}" + (defender != null ? $" {defender}" : ""));
        _attack = ctx;
        try
        {
            PerformAttackSteps(me, opp, ctx);
        }
        finally
        {
            _lastBattle = ctx;
            _attack = null;
        }
        FireFor(attacker, "onendofattack", new TriggerInfo { Other = attacker });
        CheckTiming();
    }

    private void PerformAttackSteps(PlayerState me, PlayerState opp, AttackContext ctx)
    {
        var attacker = ctx.Attacker;
        var type = ctx.Type;
        OnAttackDeclared(ctx);
        CheckTiming();

        // 判定步骤
        TriggerStep(me, opp, ctx);
        CheckTiming();

        // 反击步骤（只在正面攻击）
        if (type == AttackType.Front && IsStillAttacking(me, ctx) && IsDefenderInPlace(opp, ctx))
        {
            CounterStep(opp, ctx);
            CheckTiming();
        }

        // 伤害步骤
        if (IsStillAttacking(me, ctx) && !attacker.Has("CannotDealDamage"))
        {
            UpdateContinuous();
            int damage = Math.Max(0, attacker.Soul + ctx.DeclarationSoulModifier);
            bool canceled = DealDamage(opp, damage, $"{attacker} 的攻击");
            if (damage > 0)
            {
                var info = new TriggerInfo { Other = attacker };
                if (canceled)
                {
                    FireFor(attacker, "ondamagecancel", info);
                    FireFor(attacker, "ondamagecanceled", info);
                    if (ctx.Defender is { } dd && dd.Zone == ZoneType.Stage) FireFor(dd, "oncancel", info);
                }
                else
                {
                    FireFor(attacker, "ondamagenotcanceled", info);
                    FireWatchers(opp.Index, "onreceivedamage", info);
                }
            }
            CheckTiming();
            if (canceled)
            {
                for (int i = 0; i < ctx.ShotCount; i++)
                {
                    Info("火烧：攻击伤害被取消，再给予 1 点伤害");
                    DealDamage(opp, 1, "火烧");
                    CheckTiming();
                }
            }
        }

        // 战斗步骤（只在正面攻击）
        if (type == AttackType.Front && IsStillAttacking(me, ctx) && IsDefenderInPlace(opp, ctx))
        {
            BattleStep(attacker, ctx.Defender!);
            CheckTiming();
        }
    }

    private static bool IsStillAttacking(PlayerState me, AttackContext ctx) =>
        ReferenceEquals(me.Stage[(int)ctx.AttackerSlot], ctx.Attacker);

    private static bool IsDefenderInPlace(PlayerState opp, AttackContext ctx) =>
        ctx.Defender != null && ReferenceEquals(opp.Stage[(int)ctx.AttackerSlot.Facing()], ctx.Defender);

    // ------------------------------------------------------------------ 判定

    private void TriggerStep(PlayerState me, PlayerState opp, AttackContext ctx)
    {
        int checks = ctx.Attacker.Has("DoubleTrigger") ? 2 : 1;
        for (int k = 0; k < checks; k++)
        {
            var card = TakeTopOfDeck(me);
            if (card == null) return;
            MoveTo(card, ZoneType.Stock);
            Info($"判定：{card}" + (card.Def.RawTriggers.Count > 0 ? $"（{string.Join(" ", card.Def.RawTriggers)}）" : "（无图示）"));
            foreach (var icon in card.Def.Triggers)
                ResolveTriggerIcon(me, opp, ctx, card, icon);
            var info = new TriggerInfo { Other = card, Climax = card.Def.IsClimax ? card : null };
            if (IsStillAttacking(me, ctx))
            {
                FireFor(ctx.Attacker, "ontriggercheck", info);
                if (card.Def.IsClimax) FireFor(ctx.Attacker, "ontriggercheckclimax", info);
            }
            FireFor(card, "onthistriggercheck", info, ScriptCache.Get(card.Def).Abilities.Select(a => (a, card)).ToList());
        }
    }

    private void ResolveTriggerIcon(PlayerState me, PlayerState opp, AttackContext ctx, CardInstance triggerCard, TriggerIcon icon)
    {
        switch (icon)
        {
            case TriggerIcon.Soul:
            case TriggerIcon.DoubleSoul:
                if (IsStillAttacking(me, ctx))
                {
                    int n = icon == TriggerIcon.Soul ? 1 : 2;
                    AddUntilEndOfTurn(ctx.Attacker, ModKind.Soul, n);
                    Info($"魂图示：{ctx.Attacker} 魂 +{n}");
                }
                break;

            case TriggerIcon.Wind:
            {
                var target = AskChooseCard(me.Index, "风吹：可选择对手的 1 个角色返回手牌", opp.StageCards.ToList(), optional: true);
                if (target != null)
                {
                    MoveTo(target, ZoneType.Hand);
                    Info($"风吹：{target} 返回手牌");
                }
                break;
            }

            case TriggerIcon.Bag:
                if (me.Deck.Count > 0 && AskYesNo(me.Index, "宝袋：是否将牌库顶 1 张放到能量区？"))
                {
                    var top = TakeTopOfDeck(me);
                    if (top != null) MoveTo(top, ZoneType.Stock);
                    Info("宝袋：牌库顶 1 张放到能量区");
                }
                break;

            case TriggerIcon.Door:
            {
                var target = AskChooseCard(me.Index, "木门：可从休息室选 1 张角色返回手牌", me.WaitingRoom.Where(c => c.Def.IsCharacter).ToList(), optional: true);
                if (target != null)
                {
                    MoveTo(target, ZoneType.Hand);
                    Info($"木门：{target} 返回手牌");
                }
                break;
            }

            case TriggerIcon.Book:
                if (AskYesNo(me.Index, "书：是否抽 1 张？"))
                {
                    DrawCard(me);
                    Info("书：抽 1 张");
                }
                break;

            case TriggerIcon.Gate:
            {
                var target = AskChooseCard(me.Index, "城门：可从休息室选 1 张名场面返回手牌", me.WaitingRoom.Where(c => c.Def.IsClimax).ToList(), optional: true);
                if (target != null)
                {
                    MoveTo(target, ZoneType.Hand);
                    Info($"城门：{target} 返回手牌");
                }
                break;
            }

            case TriggerIcon.Shot:
                ctx.ShotCount++;
                Info("火烧：这次攻击的伤害被取消时，再给予 1 点伤害");
                break;

            case TriggerIcon.Standby:
            {
                var cands = me.WaitingRoom.Where(c => c.Def.IsCharacter && c.Def.Level <= me.PlayerLevel + 1).ToList();
                var target = AskChooseCard(me.Index, "开机：可从休息室选 1 张等级 ≤ 自己等级 +1 的角色横置放到舞台", cands, optional: true);
                if (target != null)
                {
                    var slot = AskSlot(me.Index, "选择放置的格子", StageSlots.All);
                    var old = me.Stage[(int)slot];
                    if (old != null) MoveTo(old, ZoneType.WaitingRoom);
                    PlaceOnStage(target, slot, CardOrientation.Rested);
                    Info($"开机：{target} 横置放到{slot.ToChinese()}");
                }
                break;
            }

            case TriggerIcon.Choice:
            {
                var cands = me.WaitingRoom.Where(c => c.Def.IsCharacter && c.Def.SoulTriggerCount > 0).ToList();
                var target = AskChooseCard(me.Index, "箭头：可从休息室选 1 张有魂图示的角色", cands, optional: true);
                if (target != null)
                {
                    var opts = new[] { new DecisionOption("加入手牌"), new DecisionOption("放到能量区") };
                    var ans = Ask(me.Index, DecisionType.ChoiceDestination, $"{target.Def.Name}：加入手牌还是放到能量区？", opts, 1, 1);
                    MoveTo(target, ans[0] == 0 ? ZoneType.Hand : ZoneType.Stock);
                    Info($"箭头：{target} " + (ans[0] == 0 ? "加入手牌" : "放到能量区"));
                }
                break;
            }

            case TriggerIcon.Bar:
                if (triggerCard.Zone == ZoneType.Stock)
                {
                    MoveTo(triggerCard, ZoneType.Hand);
                    Info($"金砖：{triggerCard} 加入手牌");
                }
                if (me.Deck.Count > 0 && AskYesNo(me.Index, "金砖：是否将牌库顶 1 张放到能量区？"))
                {
                    var top = TakeTopOfDeck(me);
                    if (top != null) MoveTo(top, ZoneType.Stock);
                    Info("金砖：牌库顶 1 张放到能量区");
                }
                break;

            default:
                Info("特殊判定图示，P1 不处理");
                break;
        }
    }

    private StageSlot AskSlot(int player, string prompt, IReadOnlyList<StageSlot> slots)
    {
        var p = State.Players[player];
        var opts = slots.Select(s => new DecisionOption(s.ToChinese() + (p.Stage[(int)s] != null ? $"（替换 {p.Stage[(int)s]!.Def.Name}）" : "")) { Slot = s }).ToList();
        var ans = Ask(player, DecisionType.ChooseSlot, prompt, opts, 1, 1);
        return slots[ans[0]];
    }

    // ------------------------------------------------------------------ 反击

    /// <summary>助太刀的能量费用。数据里只有数值字段，费用规则见 docs/open-questions.md。</summary>
    public static int BackupStockCost(CardDefinition def) => def.Level >= 1 ? 1 : 0;

    internal List<CardInstance> UsableBackups(PlayerState defenderPlayer)
    {
        if (!CanUseBackup(defenderPlayer)) return new List<CardInstance>();
        return defenderPlayer.Hand.Where(c => c.Def.BackupPower != null
                                              && c.Level <= defenderPlayer.PlayerLevel
                                              && BackupStockCost(c.Def) <= defenderPlayer.Stock.Count).ToList();
    }

    private bool CanUseBackup(PlayerState p)
    {
        if (p.HasFlag("CannotUseBackup") || p.StageCards.Any(c => c.Has("CannotUseBackup"))) return false;
        if (_attack != null && (_attack.Attacker.Has("NoBackupOnAttack") || _attack.Attacker.Has("NoBackupInBattle"))) return false;
        return true;
    }

    /// <summary>反击步骤能用的反击事件（脚本里有 Counter 标记的事件卡）。</summary>
    internal List<CardInstance> UsableCounterEvents(PlayerState p)
    {
        if (!EffectsEnabled) return new List<CardInstance>();
        if (_attack != null && _attack.Attacker.Has("NoEventsOnAttack")) return new List<CardInstance>();
        return p.Hand.Where(c => c.Def.IsEvent && ScriptCache.Get(c.Def).Flags.Any(f => f.Key == "counter") && CanPlay(p, c)).ToList();
    }

    private void CounterStep(PlayerState opp, AttackContext ctx)
    {
        UpdateContinuous();
        var cands = UsableBackups(opp);
        var events = UsableCounterEvents(opp);
        if (cands.Count == 0 && events.Count == 0) return;
        var opts = cands.Select(c => new DecisionOption($"助太刀 {c.Def.Name} +{c.Def.BackupPower}") { CardInstanceId = c.InstanceId }).ToList();
        opts.AddRange(events.Select(c => new DecisionOption($"反击事件 {c.Def.Name}") { CardInstanceId = c.InstanceId, MainAction = MainActionKind.PlayEvent }));
        opts.Add(new DecisionOption("不使用") { IsPass = true });
        var ans = Ask(opp.Index, DecisionType.Counter, $"反击步骤：{ctx.Defender!.Def.Name} 被正面攻击，可使用助太刀", opts, 1, 1);
        if (opts[ans[0]].IsPass) return;
        if (ans[0] >= cands.Count)
        {
            PlayEvent(opp, events[ans[0] - cands.Count]);
            return;
        }
        var card = cands[ans[0]];
        PayStock(opp, BackupStockCost(card.Def));
        MoveTo(card, ZoneType.WaitingRoom);
        AddUntilEndOfTurn(ctx.Defender, ModKind.Power, card.Def.BackupPower!.Value);
        Info($"P{opp.Index} 助太刀 {card}：{ctx.Defender} 战力 +{card.Def.BackupPower}");
        var backupAbilities = ScriptCache.Get(card.Def).Abilities.Select(a => (a, card)).ToList();
        FireFor(card, "onusethiscardsbackup", new TriggerInfo { Other = ctx.Defender }, backupAbilities);
        FireFor(card, "usethiscardsbackup", new TriggerInfo { Other = ctx.Defender }, backupAbilities);
    }

    // ------------------------------------------------------------------ 伤害与战斗

    /// <summary>
    /// 给予伤害：逐张从牌库顶放到结算区；出现名场面则取消，结算区全部放到休息室；
    /// 没被取消就全部放到伤害区。返回是否被取消。伤害为 0 时不处理。
    /// </summary>
    internal bool DealDamage(PlayerState target, int amount, string source)
    {
        if (amount <= 0)
        {
            Info($"{source}：伤害为 0");
            return false;
        }
        bool canceled = false;
        for (int i = 0; i < amount; i++)
        {
            var c = TakeTopOfDeck(target);
            if (c == null) break;
            // TakeTopOfDeck 已把卡放进结算区。
            if (c.Def.IsClimax)
            {
                canceled = true;
                break;
            }
        }
        var moved = target.Resolution.ToList();
        if (canceled)
        {
            foreach (var c in moved) MoveTo(c, ZoneType.WaitingRoom);
            Info($"{source}：{amount} 点伤害被取消（翻出 {moved[^1]}）");
        }
        else
        {
            foreach (var c in moved) MoveTo(c, ZoneType.Clock);
            Info($"{source}：P{target.Index} 受到 {moved.Count} 点伤害（伤害区 {target.Clock.Count} 张）");
        }
        return canceled;
    }

    private void BattleStep(CardInstance attacker, CardInstance defender)
    {
        UpdateContinuous();
        int ap = attacker.Power, dp = defender.Power;
        Info($"战斗：{attacker}（{ap}） vs {defender}（{dp}）");
        if (ap > dp) ReverseCard(defender, inBattle: true);
        else if (ap < dp) ReverseCard(attacker, inBattle: true);
        else { ReverseCard(attacker, inBattle: true); ReverseCard(defender, inBattle: true); }
    }

    // ------------------------------------------------------------------ 再演步骤

    /// <summary>倒置的角色放到休息室，之后可支付规则再演费用横置放回原来的格子。回合玩家先处理。</summary>
    internal void EncoreStep()
    {
        foreach (var p in TurnOrder())
        {
            foreach (var s in StageSlots.All)
            {
                var c = p.Stage[(int)s];
                if (c == null || c.Orientation != CardOrientation.Reversed) continue;
                var encores = EffectsEnabled
                    ? c.Abilities.Where(x => x.Ability.Kind == AbilityKind.Auto && x.Ability.TriggerKey == "encore").Select(x => x.Ability).ToList()
                    : new List<Ability>();
                bool blocked = p.HasFlag("CannotEncore") || c.Has("CannotUseEncore");
                MoveTo(c, ZoneType.WaitingRoom);
                Info($"P{p.Index} 的 {c} 放到休息室");
                CheckTiming();
                if (blocked || c.Zone != ZoneType.WaitingRoom || p.Stage[(int)s] != null) continue;
                var ectx = new EffectContext { Source = c, Self = c, Controller = p.Index };
                var choices = new List<(string Label, string Cost)>();
                if (p.Stock.Count >= Options.RuleEncoreCost)
                    choices.Add(($"支付 {Options.RuleEncoreCost} 能量再演", Options.RuleEncoreCost.ToString()));
                foreach (var e in encores)
                    if (CanPayCost(e.HeaderArgs, ectx))
                        choices.Add(($"再演（{DescribeCost(e.HeaderArgs)}）", e.HeaderArgs));
                if (choices.Count == 0) continue;
                var opts = choices.Select(x => new DecisionOption(x.Label)).ToList();
                opts.Add(new DecisionOption("不再演") { IsPass = true });
                var ans = Ask(p.Index, DecisionType.Encore, $"再演：是否将 {c.Def.Name} 横置放回{s.ToChinese()}？", opts, 1, 1);
                if (opts[ans[0]].IsPass) continue;
                PayCost(ParseCost(choices[ans[0]].Cost), ectx);
                if (c.Zone == ZoneType.WaitingRoom && p.Stage[(int)s] == null)
                {
                    PlaceOnStage(c, s, CardOrientation.Rested);
                    Info($"P{p.Index} 再演 {c}");
                    OnPlacedByEffect(c, ZoneType.WaitingRoom, "Encore");
                }
                CheckTiming();
            }
        }
        CheckTiming();
    }
}

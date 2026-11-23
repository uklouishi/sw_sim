using WsSim.Core.Cards;

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

    internal void AttackPhase(PlayerState me)
    {
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
        var opts = new List<DecisionOption>();
        foreach (var s in StageSlots.Front)
        {
            var a = me.Stage[(int)s];
            if (a == null || a.Orientation != CardOrientation.Standing) continue;
            var facing = opp.Stage[(int)s];
            if (facing != null)
            {
                opts.Add(new DecisionOption($"{a.Def.Name} 正面攻击 {facing.Def.Name}") { Slot = s, AttackType = AttackType.Front, CardInstanceId = a.InstanceId });
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
        var defender = opp.Stage[(int)slot];
        if ((type == AttackType.Direct) != (defender == null)) throw new InvalidOperationException("攻击方式与对面是否有角色不符");

        // 宣告：横置攻击角色。
        attacker.Orientation = CardOrientation.Rested;
        State.AttacksThisTurn++;
        var ctx = new AttackContext
        {
            Attacker = attacker,
            AttackerSlot = slot,
            Type = type,
            Defender = defender,
            DeclarationSoulModifier = type switch
            {
                AttackType.Side => -defender!.Level,
                AttackType.Direct => 1,
                _ => 0,
            },
        };
        string typeName = type switch { AttackType.Front => "正面攻击", AttackType.Side => "侧面攻击", _ => "直接攻击" };
        Info($"P{me.Index} 的 {attacker} {typeName}" + (defender != null ? $" {defender}" : ""));
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
        if (IsStillAttacking(me, ctx))
        {
            int damage = Math.Max(0, attacker.Soul + ctx.DeclarationSoulModifier);
            bool canceled = DealDamage(opp, damage, $"{attacker} 的攻击");
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
        ctx.Defender != null && ReferenceEquals(opp.Stage[(int)ctx.AttackerSlot], ctx.Defender);

    // ------------------------------------------------------------------ 判定

    private void TriggerStep(PlayerState me, PlayerState opp, AttackContext ctx)
    {
        var card = TakeTopOfDeck(me);
        if (card == null) return;
        MoveTo(card, ZoneType.Stock);
        Info($"判定：{card}" + (card.Def.RawTriggers.Count > 0 ? $"（{string.Join(" ", card.Def.RawTriggers)}）" : "（无图示）"));
        foreach (var icon in card.Def.Triggers)
            ResolveTriggerIcon(me, opp, ctx, card, icon);
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
                    ctx.Attacker.SoulModifierUntilEndOfTurn += n;
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

    internal List<CardInstance> UsableBackups(PlayerState defenderPlayer) =>
        defenderPlayer.Hand.Where(c => c.Def.BackupPower != null
                                       && c.Def.Level <= defenderPlayer.PlayerLevel
                                       && BackupStockCost(c.Def) <= defenderPlayer.Stock.Count).ToList();

    private void CounterStep(PlayerState opp, AttackContext ctx)
    {
        var cands = UsableBackups(opp);
        if (cands.Count == 0) return;
        var opts = cands.Select(c => new DecisionOption($"助太刀 {c.Def.Name} +{c.Def.BackupPower}") { CardInstanceId = c.InstanceId }).ToList();
        opts.Add(new DecisionOption("不使用") { IsPass = true });
        var ans = Ask(opp.Index, DecisionType.Counter, $"反击步骤：{ctx.Defender!.Def.Name} 被正面攻击，可使用助太刀", opts, 1, 1);
        if (opts[ans[0]].IsPass) return;
        var card = cands[ans[0]];
        PayStock(opp, BackupStockCost(card.Def));
        MoveTo(card, ZoneType.WaitingRoom);
        ctx.Defender.PowerModifierUntilEndOfTurn += card.Def.BackupPower!.Value;
        Info($"P{opp.Index} 助太刀 {card}：{ctx.Defender} 战力 +{card.Def.BackupPower}");
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
        int ap = attacker.Power, dp = defender.Power;
        if (ap > dp) Reverse(defender);
        else if (ap < dp) Reverse(attacker);
        else { Reverse(attacker); Reverse(defender); }
        Info($"战斗：{attacker}（{ap}） vs {defender}（{dp}）");
    }

    private void Reverse(CardInstance c)
    {
        c.Orientation = CardOrientation.Reversed;
        Info($"{c} 倒置");
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
                MoveTo(c, ZoneType.WaitingRoom);
                Info($"P{p.Index} 的 {c} 放到休息室");
                if (p.Stock.Count >= Options.RuleEncoreCost)
                {
                    var opts = new[] { new DecisionOption($"支付 {Options.RuleEncoreCost} 能量再演"), new DecisionOption("不再演") { IsPass = true } };
                    var ans = Ask(p.Index, DecisionType.Encore, $"再演：是否支付 {Options.RuleEncoreCost} 能量将 {c.Def.Name} 横置放回{s.ToChinese()}？", opts, 1, 1);
                    if (ans[0] == 0)
                    {
                        PayStock(p, Options.RuleEncoreCost);
                        PlaceOnStage(c, s, CardOrientation.Rested);
                        Info($"P{p.Index} 再演 {c}");
                    }
                }
            }
        }
        CheckTiming();
    }
}

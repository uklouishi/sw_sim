using WsSim.Core.Cards;
using WsSim.Core.Effects;
using WsSim.Core.Randomness;

namespace WsSim.Core.Game;

public sealed class GameOptions
{
    public int StartingHandSize { get; init; } = 5;
    public int HandLimit { get; init; } = 7;
    /// <summary>先攻玩家第一回合可攻击次数（综合规则 7.2.1.3.1.2）。</summary>
    public int FirstTurnAttackLimit { get; init; } = 1;
    public int RuleEncoreCost { get; init; } = 3;
    public int ClockToLevelUp { get; init; } = 7;
    public int LosingLevel { get; init; } = 4;
    /// <summary>安全上限：超过这个回合数按平局结束，正常对局不会触发。</summary>
    public int MaxTurns { get; init; } = 500;
    /// <summary>安全上限：一个主要阶段内最多的行动数，超过后自动结束主要阶段。</summary>
    public int MaxMainActionsPerTurn { get; init; } = 200;
    /// <summary>执行卡片效果脚本。关掉就是 P1 的纯规则引擎（只有判定图示、助太刀数值等）。</summary>
    public bool EnableEffects { get; init; } = true;
}

public sealed record GameResult(int? Winner, bool IsDraw, int Turns, string Reason);

/// <summary>
/// P1 规则引擎：不含卡片效果脚本，按 spec/rules-summary.md 与官方综合规则执行对战流程。
/// 引擎同步运行，所有玩家选择都通过 <see cref="IPlayerAgent"/> 取得；所有随机都来自同一个
/// <see cref="DeterministicRandom"/>，所以同一种子 + 同一串选择 = 完全一样的对局。
/// </summary>
public sealed partial class GameEngine
{
    private readonly IPlayerAgent[] _agents;
    private readonly DeterministicRandom _rng;
    private readonly List<RecordedDecision> _decisions = new();
    private readonly List<string> _log = new();
    private int _decisionSeq;

    public GameState State { get; }
    public GameOptions Options { get; }
    public ulong Seed => _rng.Seed;
    public IReadOnlyList<RecordedDecision> Decisions => _decisions;
    public IReadOnlyList<string> Log => _log;

    /// <summary>每次向玩家提问前触发（可用于界面刷新、联机发送状态哈希）。</summary>
    public event Action<Decision>? DecisionRequested;

    /// <summary>局面每一步变化（卡移动、方向改变、洗牌、攻击宣告、阶段开始）后触发，在引擎线程上同步调用。</summary>
    public event Action<GameEvent>? EventRaised;

    private void Raise(GameEvent e) => EventRaised?.Invoke(e);

    private void SetPhase(Phase phase)
    {
        State.Phase = phase;
        Raise(new PhaseEvent(State.ActivePlayer, State.TurnNumber, phase));
    }

    private void SetOrientation(CardInstance c, CardOrientation o)
    {
        if (c.Orientation == o) return;
        var from = c.Orientation;
        c.Orientation = o;
        if (c.Zone == ZoneType.Stage && State.Players[c.Owner].SlotOf(c) is { } slot)
            Raise(new CardOrientationEvent(c.Owner, c.InstanceId, slot, from, o));
    }

    private void Shuffle(PlayerState p)
    {
        _rng.Shuffle(p.Deck);
        Raise(new ShuffleEvent(p.Index));
    }

    /// <summary>卡现在所在的位置（区域、列表序号、舞台格）。</summary>
    private (ZoneType Zone, int Index, StageSlot? Slot) Where(CardInstance c)
    {
        var p = State.Players[c.Owner];
        if (c.Zone == ZoneType.Marker)
        {
            var host = c.MarkerHost;
            return (ZoneType.Marker, host?.Markers.IndexOf(c) ?? -1, host != null ? State.Players[host.Owner].SlotOf(host) : null);
        }
        if (c.Zone == ZoneType.Stage)
        {
            var s = p.SlotOf(c);
            return (ZoneType.Stage, s is { } x ? (int)x : -1, s);
        }
        return (c.Zone, p.ZoneList(c.Zone).IndexOf(c), null);
    }

    public GameEngine(Deck deck0, Deck deck1, IPlayerAgent agent0, IPlayerAgent agent1, ulong seed, GameOptions? options = null)
    {
        _agents = new[] { agent0, agent1 };
        _rng = new DeterministicRandom(seed);
        Options = options ?? new GameOptions();
        var p0 = new PlayerState(0, deck0.Name);
        var p1 = new PlayerState(1, deck1.Name);
        State = new GameState(p0, p1);
        int nextId = 1;
        foreach (var (p, deck) in new[] { (p0, deck0), (p1, deck1) })
            foreach (var def in deck.Cards)
                p.Deck.Add(new CardInstance(nextId++, def, p.Index) { Zone = ZoneType.Deck });
    }

    public ulong StateHash() => StateHasher.Compute(State, _rng);

    /// <summary>从开局一直进行到分出胜负（或达到安全上限）。</summary>
    public GameResult Run()
    {
        try
        {
            Setup();
            while (true)
            {
                if (State.TurnNumber >= Options.MaxTurns)
                {
                    EndGame(null, $"达到回合数上限 {Options.MaxTurns}");
                    break;
                }
                PlayTurn();
            }
        }
        catch (GameEndedSignal)
        {
        }
        return new GameResult(State.Winner, State.IsDraw, State.TurnNumber, State.EndReason ?? "");
    }

    // ------------------------------------------------------------------ 开局

    internal void Setup()
    {
        SetPhase(Phase.Setup);
        foreach (var p in State.Players)
            Shuffle(p);

        // 猜拳：胜者由随机数决定，胜者选择先攻或后攻。
        int rpsWinner = _rng.NextInt(2);
        Info($"P{rpsWinner} 猜拳获胜");
        var ans = Ask(rpsWinner, DecisionType.ChooseFirstPlayer, "选择先攻还是后攻",
            new[] { new DecisionOption("先攻"), new DecisionOption("后攻") }, 1, 1);
        State.FirstPlayer = ans[0] == 0 ? rpsWinner : 1 - rpsWinner;
        State.ActivePlayer = State.FirstPlayer;
        Info($"P{State.FirstPlayer} 先攻");

        foreach (var p in State.Players)
            for (int i = 0; i < Options.StartingHandSize; i++)
                DrawCard(p);

        // 换牌：先攻玩家先进行。
        foreach (int idx in new[] { State.FirstPlayer, 1 - State.FirstPlayer })
        {
            var p = State.Players[idx];
            var hand = p.Hand.ToList();
            var picks = Ask(idx, DecisionType.Mulligan, "选择要换掉的手牌（任意张）",
                hand.Select(CardOption).ToList(), 0, hand.Count);
            foreach (int i in picks.OrderBy(x => x))
                MoveTo(hand[i], ZoneType.WaitingRoom);
            for (int i = 0; i < picks.Length; i++)
                DrawCard(p);
            Info($"P{idx} 换牌 {picks.Length} 张");
        }
        CheckTiming();
    }

    // ------------------------------------------------------------------ 回合

    private void PlayTurn()
    {
        State.TurnNumber++;
        State.AttacksThisTurn = 0;
        var me = State.Active;
        Info($"===== 第 {State.TurnNumber} 回合：P{me.Index} =====");

        // 1. 正置阶段
        StartPhase(Phase.Stand);
        foreach (var c in me.StageCards)
            if (!c.Has("CannotStand") && !c.Has("DontStandNext"))
                SetOrientation(c, CardOrientation.Standing);
        CheckTiming();

        // 2. 抽牌阶段
        StartPhase(Phase.Draw);
        DrawCard(me);
        CheckTiming();

        // 3. 伤害阶段
        StartPhase(Phase.Clock);
        ClockPhase(me);

        // 4. 主要阶段
        StartPhase(Phase.Main);
        MainPhase(me);

        // 5. 名场面阶段
        StartPhase(Phase.Climax);
        ClimaxPhase(me);

        // 6. 攻击阶段（含再演步骤）
        StartPhase(Phase.Attack);
        AttackPhase(me);
        StartPhase(Phase.Encore);
        EncoreStep();

        // 7. 结束阶段
        StartPhase(Phase.End);
        EndPhase(me);

        State.ActivePlayer = 1 - State.ActivePlayer;
    }

    /// <summary>进入阶段：发阶段事件，触发“……阶段开始时”的【自】并处理。</summary>
    private void StartPhase(Phase phase)
    {
        SetPhase(phase);
        FirePhaseStart(phase);
        CheckTiming();
    }

    internal void ClockPhase(PlayerState me)
    {
        var opts = me.Hand.Select(CardOption).ToList();
        opts.Add(new DecisionOption("不放置") { IsPass = true });
        var ans = Ask(me.Index, DecisionType.Clock, "可将 1 张手牌放到伤害区，然后抽 2 张", opts, 1, 1);
        if (!opts[ans[0]].IsPass)
        {
            var card = me.Hand[ans[0]];
            MoveTo(card, ZoneType.Clock);
            Info($"P{me.Index} 将 {card} 放到伤害区");
            DrawCard(me);
            DrawCard(me);
        }
        CheckTiming();
    }

    internal void MainPhase(PlayerState me)
    {
        for (int n = 0; n < Options.MaxMainActionsPerTurn; n++)
        {
            var opts = BuildMainActions(me);
            var ans = Ask(me.Index, DecisionType.MainAction, "主要阶段：选择行动", opts, 1, 1);
            var o = opts[ans[0]];
            if (o.MainAction == MainActionKind.End) return;
            switch (o.MainAction)
            {
                case MainActionKind.PlayCharacter:
                    PlayCharacter(me, FindInHand(me, o.CardInstanceId!.Value), o.Slot!.Value);
                    break;
                case MainActionKind.PlayEvent:
                    PlayEvent(me, FindInHand(me, o.CardInstanceId!.Value));
                    break;
                case MainActionKind.Move:
                    MoveOnStage(me, o.Slot!.Value, o.TargetSlot!.Value);
                    break;
                case MainActionKind.Activate:
                    if (me.FindInstance(o.CardInstanceId!.Value) is { } src)
                        ActivateAbility(src, o.AbilityIndex!.Value);
                    break;
            }
            CheckTiming();
        }
        Info("主要阶段行动数达到上限，自动结束");
    }

    internal List<DecisionOption> BuildMainActions(PlayerState me)
    {
        UpdateContinuous();
        var opts = new List<DecisionOption>();
        foreach (var c in me.Hand)
        {
            if (c.Def.IsCharacter && CanPlay(me, c))
                foreach (var s in StageSlots.All)
                    opts.Add(new DecisionOption($"使用 {c.Def.Name} 到{s.ToChinese()}")
                    { MainAction = MainActionKind.PlayCharacter, CardInstanceId = c.InstanceId, Slot = s });
            else if (c.Def.IsEvent && CanPlay(me, c) && !ScriptCache.Get(c.Def).Flags.Any(f => f.Key == "counter"))
                opts.Add(new DecisionOption($"使用事件 {c.Def.Name}") { MainAction = MainActionKind.PlayEvent, CardInstanceId = c.InstanceId });
        }
        foreach (var c in me.StageCards.Concat(me.Memory).ToList())
            foreach (var (idx, ab) in UsableActs(c))
                opts.Add(new DecisionOption($"发动 {c.Def.Name} 的【起】" + (ab.Text != null ? $"：{Short(ab.Text)}" : $"（{DescribeCost(ab.HeaderArgs)}）"))
                { MainAction = MainActionKind.Activate, CardInstanceId = c.InstanceId, AbilityIndex = idx });
        foreach (var from in StageSlots.All)
        {
            if (me.Stage[(int)from] == null || me.Stage[(int)from]!.Has("CannotMove")) continue;
            foreach (var to in StageSlots.All)
            {
                if (to == from) continue;
                // 两格都有角色时是交换，只列一次。
                if (me.Stage[(int)to] != null && (to < from || me.Stage[(int)to]!.Has("CannotMove"))) continue;
                opts.Add(new DecisionOption($"移动 {from.ToChinese()} → {to.ToChinese()}")
                { MainAction = MainActionKind.Move, Slot = from, TargetSlot = to });
            }
        }
        opts.Add(new DecisionOption("结束主要阶段") { MainAction = MainActionKind.End, IsPass = true });
        return opts;
    }

    /// <summary>等级、费用、颜色条件（1 级以上的卡和名场面需要等级区或伤害区有同色卡）。</summary>
    public bool CanPlay(PlayerState me, CardInstance c)
    {
        var d = c.Def;
        bool ignoreColor = c.Has("IgnoreColorRequirement");
        if (d.IsClimax)
            return (ignoreColor || me.HasColorInClockOrLevel(d.Color)) && !me.StageCards.Any(x => x.Has("CannotPlayClimax"));
        if (c.Has("CannotBePlayed")) return false;
        if (c.Level > me.PlayerLevel) return false;
        if (c.Cost > me.Stock.Count) return false;
        if (c.Level >= 1 && !ignoreColor && !me.HasColorInClockOrLevel(d.Color)) return false;
        if (d.IsEvent && !EventRequirementMet(me, c)) return false;
        return true;
    }

    private void PlayCharacter(PlayerState me, CardInstance card, StageSlot slot)
    {
        if (!card.Def.IsCharacter || !CanPlay(me, card)) throw new InvalidOperationException($"不能使用 {card}");
        PayStock(me, card.Cost);
        var old = me.Stage[(int)slot];
        if (old != null)
        {
            MoveTo(old, ZoneType.WaitingRoom);
            Info($"P{me.Index} 的 {old} 被替换，放到休息室");
        }
        PlaceOnStage(card, slot, CardOrientation.Standing);
        Info($"P{me.Index} 使用 {card} 到{slot.ToChinese()}");
        OnCardPlayed(card);
    }

    private void PlayEvent(PlayerState me, CardInstance card)
    {
        if (!card.Def.IsEvent || !CanPlay(me, card)) throw new InvalidOperationException($"不能使用 {card}");
        PayStock(me, card.Cost);
        MoveTo(card, ZoneType.Resolution);
        Info($"P{me.Index} 使用事件 {card}");
        if (EffectsEnabled) ResolveEvent(me, card);
        if (card.Zone == ZoneType.Resolution) MoveTo(card, ZoneType.WaitingRoom);
    }

    private void MoveOnStage(PlayerState me, StageSlot from, StageSlot to)
    {
        var a = me.Stage[(int)from] ?? throw new InvalidOperationException("移动的格子没有角色");
        var b = me.Stage[(int)to];
        me.Stage[(int)to] = a;
        me.Stage[(int)from] = b;
        a.LastSlot = to;
        if (b != null) b.LastSlot = from;
        Raise(new CardMovedEvent(me.Index, a.InstanceId, ZoneType.Stage, (int)from, from, ZoneType.Stage, (int)to, to, a.Orientation));
        if (b != null)
            Raise(new CardMovedEvent(me.Index, b.InstanceId, ZoneType.Stage, (int)to, to, ZoneType.Stage, (int)from, from, b.Orientation));
        Info($"P{me.Index} 移动 {a}：{from.ToChinese()} → {to.ToChinese()}" + (b != null ? $"（与 {b} 交换）" : ""));
    }

    private void ClimaxPhase(PlayerState me)
    {
        var cands = me.Hand.Where(c => c.Def.IsClimax && CanPlay(me, c)).ToList();
        if (cands.Count == 0) return;
        var opts = cands.Select(CardOption).ToList();
        opts.Add(new DecisionOption("不使用") { IsPass = true });
        var ans = Ask(me.Index, DecisionType.Climax, "名场面阶段：可使用 1 张名场面", opts, 1, 1);
        if (opts[ans[0]].IsPass) return;
        var card = cands[ans[0]];
        MoveTo(card, ZoneType.ClimaxArea);
        Info($"P{me.Index} 使用名场面 {card}");
        OnClimaxPlayed(card);
        CheckTiming();
    }

    internal void EndPhase(PlayerState me)
    {
        // 顺序：弃牌到上限 → 名场面区放到休息室 → “直到回合结束为止”的效果结束（见 docs/open-questions.md）。
        int excess = me.Hand.Count - Options.HandLimit;
        if (excess > 0)
        {
            var hand = me.Hand.ToList();
            var ans = Ask(me.Index, DecisionType.DiscardToHandLimit, $"手牌超过 {Options.HandLimit} 张，选择 {excess} 张放到休息室",
                hand.Select(CardOption).ToList(), excess, excess);
            foreach (int i in ans.OrderBy(x => x))
                MoveTo(hand[i], ZoneType.WaitingRoom);
        }
        foreach (var p in new[] { State.Active, State.NonActive })
            foreach (var cx in p.ClimaxArea.ToList())
                MoveTo(cx, ZoneType.WaitingRoom);
        CheckTiming();
        ExpireModifiers();
        UpdateContinuous();
    }

    // ------------------------------------------------------------------ 规则处理

    /// <summary>
    /// 规则处理时点：洗牌重整 → 升级 → 重整伤害 → 败北判定，重复直到没有可处理的事项。
    /// 回合玩家先处理。有人败北时结束对局。
    /// </summary>
    internal void CheckTiming()
    {
        do
        {
            RuleProcess();
        } while (ResolveOnePending());
        UpdateContinuous();
    }

    private void RuleProcess()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var p in TurnOrder())
            {
                if (p.HasLost) continue;
                if (p.Deck.Count == 0) { Refresh(p); changed = true; }
            }
            foreach (var p in TurnOrder())
            {
                while (!p.HasLost && p.Clock.Count >= Options.ClockToLevelUp)
                {
                    LevelUp(p);
                    changed = true;
                }
            }
            foreach (var p in TurnOrder())
            {
                if (!p.HasLost && p.PendingRefreshPoints > 0)
                {
                    p.PendingRefreshPoints--;
                    var top = TakeTopOfDeck(p);
                    if (top != null)
                    {
                        MoveTo(top, ZoneType.Clock);
                        Info($"P{p.Index} 重整伤害：{top} 放到伤害区");
                    }
                    changed = true;
                }
            }
        } while (changed && !State.Players.Any(p => p.HasLost));

        var losers = State.Players.Where(p => p.HasLost).ToList();
        if (losers.Count == 2)
            EndGame(null, "双方同时败北，平局");
        else if (losers.Count == 1)
            EndGame(1 - losers[0].Index, $"P{losers[0].Index} 败北：{losers[0].LossReason}");
    }

    private IEnumerable<PlayerState> TurnOrder() => new[] { State.Active, State.NonActive };

    private void LevelUp(PlayerState p)
    {
        var bottom = p.Clock.Take(Options.ClockToLevelUp).ToList();
        var ans = Ask(p.Index, DecisionType.LevelUp, "升级：选择 1 张放到等级区", bottom.Select(CardOption).ToList(), 1, 1);
        var chosen = bottom[ans[0]];
        MoveTo(chosen, ZoneType.Level);
        foreach (var c in bottom.Where(c => c != chosen))
            MoveTo(c, ZoneType.WaitingRoom);
        Info($"P{p.Index} 升级到 {p.PlayerLevel} 级（{chosen} 放到等级区）");
        if (p.PlayerLevel >= Options.LosingLevel)
            Lose(p, $"等级达到 {Options.LosingLevel}");
        FireWatchers(p.Index, "onlevelup", new TriggerInfo { Other = chosen });
        FireWatchers(1 - p.Index, "onopponentlevelup", new TriggerInfo { Other = chosen });
    }

    /// <summary>洗牌重整：休息室全部洗切成为新牌库，之后受到 1 点重整伤害。休息室也没有卡则败北。</summary>
    private void Refresh(PlayerState p)
    {
        if (p.WaitingRoom.Count == 0)
        {
            Lose(p, "需要洗牌重整时牌库和休息室都没有卡");
            return;
        }
        foreach (var c in p.WaitingRoom.ToList())
            MoveTo(c, ZoneType.Deck, toBottom: true);
        Shuffle(p);
        p.PendingRefreshPoints++;
        Info($"P{p.Index} 洗牌重整（新牌库 {p.Deck.Count} 张）");
    }

    private void Lose(PlayerState p, string reason)
    {
        if (p.HasLost) return;
        p.HasLost = true;
        p.LossReason = reason;
        Info($"P{p.Index} 败北：{reason}");
    }

    private void EndGame(int? winner, string reason)
    {
        State.IsOver = true;
        State.Winner = winner;
        State.IsDraw = winner == null;
        State.EndReason = reason;
        SetPhase(Phase.GameOver);
        Info($"对局结束：{reason}");
        throw new GameEndedSignal();
    }

    internal sealed class GameEndedSignal : Exception { }

    // ------------------------------------------------------------------ 卡片移动

    /// <summary>
    /// 取牌库顶 1 张（从牌库移出前不改变区域）。牌库在取之后变成 0 张时立即洗牌重整，
    /// 重整伤害留到下一个规则处理时点。牌库已空且无法重整时返回 null。
    /// </summary>
    internal CardInstance? TakeTopOfDeck(PlayerState p)
    {
        if (p.Deck.Count == 0)
        {
            if (p.HasLost) return null;
            Refresh(p);
            if (p.Deck.Count == 0) return null;
        }
        var top = p.Deck[0];
        p.Deck.RemoveAt(0);
        top.Zone = ZoneType.Resolution; // 临时，调用方会马上 MoveTo
        p.Resolution.Add(top);
        Raise(new CardMovedEvent(p.Index, top.InstanceId, ZoneType.Deck, 0, null, ZoneType.Resolution, p.Resolution.Count - 1, null, top.Orientation));
        if (p.Deck.Count == 0 && !p.HasLost)
            Refresh(p);
        return top;
    }

    internal void DrawCard(PlayerState p)
    {
        var c = TakeTopOfDeck(p);
        if (c != null) MoveTo(c, ZoneType.Hand);
    }

    private void PayStock(PlayerState p, int n)
    {
        if (p.Stock.Count < n) throw new InvalidOperationException("能量不足");
        for (int i = 0; i < n; i++)
            MoveTo(p.Stock[^1], ZoneType.WaitingRoom);
    }

    private void PlaceOnStage(CardInstance card, StageSlot slot, CardOrientation orientation)
    {
        var p = State.Players[card.Owner];
        if (p.Stage[(int)slot] != null) throw new InvalidOperationException("格子上已有角色");
        var from = Where(card);
        RemoveFromCurrentZone(card);
        card.ResetTransient();
        card.Orientation = orientation;
        card.Zone = ZoneType.Stage;
        card.LastSlot = slot;
        card.EnteredStageTurn = State.TurnNumber;
        p.Stage[(int)slot] = card;
        Raise(new CardMovedEvent(p.Index, card.InstanceId, from.Zone, from.Index, from.Slot, ZoneType.Stage, (int)slot, slot, orientation));
    }

    /// <summary>把卡移到列表区域的顶端（或底部）。离开舞台的卡清除状态与修正。</summary>
    private void MoveTo(CardInstance card, ZoneType zone, bool toBottom = false)
    {
        if (zone == ZoneType.Stage) throw new ArgumentException("上舞台用 PlaceOnStage");
        var p = State.Players[card.Owner];
        bool fromStage = card.Zone == ZoneType.Stage;
        var from = Where(card);
        List<(Ability, CardInstance)>? abilities = fromStage && EffectsEnabled ? card.Abilities.ToList() : null;
        RemoveFromCurrentZone(card);
        if (fromStage && card.Markers.Count > 0)
        {
            // 标记跟着离开：放到各自主人的休息室。
            foreach (var m in card.Markers.ToList()) MoveTo(m, ZoneType.WaitingRoom);
        }
        card.ResetTransient();
        card.Zone = zone;
        var list = p.ZoneList(zone);
        // 牌库 Deck[0] 是顶；其他区域末尾是顶。
        if (zone == ZoneType.Deck)
        {
            if (toBottom) list.Add(card); else list.Insert(0, card);
        }
        else
        {
            if (toBottom) list.Insert(0, card); else list.Add(card);
        }
        Raise(new CardMovedEvent(p.Index, card.InstanceId, from.Zone, from.Index, from.Slot, zone, list.IndexOf(card), null, card.Orientation));
        if (abilities != null && from.Slot is { } fromSlot) OnLeftStage(card, zone, fromSlot, abilities);
    }

    private void RemoveFromCurrentZone(CardInstance card)
    {
        var p = State.Players[card.Owner];
        if (card.Zone == ZoneType.Stage)
        {
            var slot = p.SlotOf(card) ?? throw new InvalidOperationException($"{card} 不在舞台上");
            p.Stage[(int)slot] = null;
        }
        else if (card.Zone == ZoneType.Marker)
        {
            card.MarkerHost?.Markers.Remove(card);
            card.MarkerHost = null;
        }
        else if (!p.ZoneList(card.Zone).Remove(card))
        {
            throw new InvalidOperationException($"{card} 不在 {card.Zone}");
        }
    }

    private static CardInstance FindInHand(PlayerState p, int instanceId) =>
        p.Hand.First(c => c.InstanceId == instanceId);

    // ------------------------------------------------------------------ 选择

    private static DecisionOption CardOption(CardInstance c) =>
        new($"{c.Def.Name}（{c.Def.Id}）") { CardInstanceId = c.InstanceId, Card = c.Def };

    private int[] Ask(int player, DecisionType type, string prompt, IReadOnlyList<DecisionOption> options, int min, int max)
    {
        UpdateContinuous();
        var d = new Decision(player, type, prompt, options, min, max) { Sequence = _decisionSeq++ };
        DecisionRequested?.Invoke(d);
        var answer = _agents[player].Decide(State, d);
        if (answer == null || !d.IsValidAnswer(answer))
            throw new InvalidOperationException($"P{player} 对第 {d.Sequence} 个选择（{type}）的回答无效：[{string.Join(",", answer ?? Array.Empty<int>())}]");
        _decisions.Add(new RecordedDecision(d.Sequence, player, type, answer.ToArray()));
        return answer;
    }

    /// <summary>从候选卡中选 1 张；optional 时可以不选（返回 null）。</summary>
    private CardInstance? AskChooseCard(int player, string prompt, IReadOnlyList<CardInstance> cands, bool optional)
    {
        if (cands.Count == 0) return null;
        var opts = cands.Select(CardOption).ToList();
        if (optional) opts.Add(new DecisionOption("不选") { IsPass = true });
        var ans = Ask(player, DecisionType.ChooseCard, prompt, opts, 1, 1);
        return opts[ans[0]].IsPass ? null : cands[ans[0]];
    }

    private bool AskYesNo(int player, string prompt)
    {
        var opts = new[] { new DecisionOption("是"), new DecisionOption("否") { IsPass = true } };
        return Ask(player, DecisionType.YesNo, prompt, opts, 1, 1)[0] == 0;
    }

    private void Info(string s) => _log.Add(s);
}

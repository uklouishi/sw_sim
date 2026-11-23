using WsSim.Core.Cards;
using WsSim.Core.Game;
using Xunit;

namespace WsSim.Core.Tests;

/// <summary>规则单元测试：用合成卡摆出局面，检查单个规则的处理结果。</summary>
public class RulesTests
{
    private static List<CardDefinition> Filler(int n = 20) => Make.Many(n, () => Make.Char());

    // ---------------------------------------------------------------- 伤害与取消

    [Fact]
    public void Damage_IsCanceledByClimax_AndAllRevealedCardsGoToWaitingRoom()
    {
        var deck1 = new List<CardDefinition> { Make.Char(), Make.Climax(), Make.Char() };
        deck1.AddRange(Filler());
        var b = new Board(Filler(), deck1);
        bool canceled = b.Engine.DealDamage(b.P1, 3, "测试");
        Assert.True(canceled);
        Assert.Empty(b.P1.Clock);
        Assert.Equal(2, b.P1.WaitingRoom.Count);
        Assert.Empty(b.P1.Resolution);
        Assert.True(b.P1.Deck[0].Def.IsCharacter); // 第 3 张没被翻
    }

    [Fact]
    public void Damage_NotCanceled_GoesToClock()
    {
        var b = new Board(Filler(), Filler());
        bool canceled = b.Engine.DealDamage(b.P1, 3, "测试");
        Assert.False(canceled);
        Assert.Equal(3, b.P1.Clock.Count);
        Assert.Empty(b.P1.Resolution);
        Assert.All(b.P1.Clock, c => Assert.Equal(ZoneType.Clock, c.Zone));
    }

    [Fact]
    public void ZeroDamage_DoesNothing()
    {
        var b = new Board(Filler(), Filler());
        Assert.False(b.Engine.DealDamage(b.P1, 0, "测试"));
        Assert.Empty(b.P1.Clock);
        Assert.Equal(20, b.P1.Deck.Count);
    }

    // ---------------------------------------------------------------- 升级

    [Fact]
    public void SeventhClock_LevelsUp_RestGoToWaitingRoom()
    {
        var b = new Board(Filler(), Filler(30));
        for (int i = 0; i < 6; i++) b.Put(b.P1, _ => true, ZoneType.Clock);
        b.Engine.DealDamage(b.P1, 1, "测试");
        Assert.Equal(7, b.P1.Clock.Count);
        Assert.False(b.Step(b.Engine.CheckTiming));
        Assert.Equal(1, b.P1.PlayerLevel);
        Assert.Empty(b.P1.Clock);
        Assert.Equal(6, b.P1.WaitingRoom.Count);
    }

    [Fact]
    public void LevelFour_Loses()
    {
        var b = new Board(Filler(), Filler(30));
        for (int i = 0; i < 3; i++) b.Put(b.P1, _ => true, ZoneType.Level);
        for (int i = 0; i < 6; i++) b.Put(b.P1, _ => true, ZoneType.Clock);
        b.Engine.DealDamage(b.P1, 1, "测试");
        Assert.True(b.Step(b.Engine.CheckTiming));
        Assert.True(b.State.IsOver);
        Assert.Equal(0, b.State.Winner);
        Assert.Equal(4, b.P1.PlayerLevel);
    }

    [Fact]
    public void BothPlayersLosingAtOnce_IsDraw()
    {
        var b = new Board(Filler(30), Filler(30));
        foreach (var p in b.State.Players)
        {
            for (int i = 0; i < 3; i++) b.Put(p, _ => true, ZoneType.Level);
            for (int i = 0; i < 7; i++) b.Put(p, _ => true, ZoneType.Clock);
        }
        Assert.True(b.Step(b.Engine.CheckTiming));
        Assert.True(b.State.IsDraw);
        Assert.Null(b.State.Winner);
    }

    // ---------------------------------------------------------------- 洗牌重整

    [Fact]
    public void EmptyDeck_RefreshesImmediately_PenaltyAtCheckTiming()
    {
        var b = new Board(Filler(6), Filler());
        for (int i = 0; i < 4; i++) b.Put(b.P0, _ => true, ZoneType.WaitingRoom);
        b.Engine.DrawCard(b.P0);
        b.Engine.DrawCard(b.P0);
        Assert.Equal(2, b.P0.Hand.Count);
        Assert.Equal(4, b.P0.Deck.Count);
        Assert.Empty(b.P0.WaitingRoom);
        Assert.Equal(1, b.P0.PendingRefreshPoints);

        Assert.False(b.Step(b.Engine.CheckTiming));
        Assert.Equal(0, b.P0.PendingRefreshPoints);
        Assert.Single(b.P0.Clock);
        Assert.Equal(3, b.P0.Deck.Count);
    }

    [Fact]
    public void RefreshDuringDamage_DoesNotUseResolutionCards()
    {
        // 牌库 2 张、休息室 3 张，受到 3 点伤害：翻 2 张后牌库空，立即用休息室的 3 张重整，再翻第 3 张。
        var b = new Board(Filler(), Filler(5));
        for (int i = 0; i < 3; i++) b.Put(b.P1, _ => true, ZoneType.WaitingRoom);
        b.Engine.DealDamage(b.P1, 3, "测试");
        Assert.Equal(3, b.P1.Clock.Count);
        Assert.Equal(2, b.P1.Deck.Count);
        Assert.Equal(1, b.P1.PendingRefreshPoints);
        Assert.False(b.Step(b.Engine.CheckTiming));
        Assert.Equal(4, b.P1.Clock.Count);
    }

    [Fact]
    public void RefreshWithEmptyWaitingRoom_Loses()
    {
        var b = new Board(Filler(1), Filler());
        b.Engine.DrawCard(b.P0);
        Assert.True(b.P0.HasLost);
        Assert.True(b.Step(b.Engine.CheckTiming));
        Assert.Equal(1, b.State.Winner);
    }

    // ---------------------------------------------------------------- 使用条件

    [Fact]
    public void CanPlay_ChecksLevelCostAndColor()
    {
        var lv1 = Make.Char(level: 1, cost: 1, color: CardColor.Red);
        var deck = new List<CardDefinition> { lv1, Make.Char(color: CardColor.Yellow), Make.Char(color: CardColor.Red), Make.Char() };
        deck.AddRange(Filler());
        var b = new Board(deck, Filler());
        var card = b.Put(b.P0, d => d == lv1, ZoneType.Hand);
        Assert.False(b.Engine.CanPlay(b.P0, card)); // 等级 0

        b.Put(b.P0, d => d.Color == CardColor.Yellow, ZoneType.Level);
        Assert.False(b.Engine.CanPlay(b.P0, card)); // 没有能量

        b.Put(b.P0, d => d.Color == CardColor.Yellow && d != lv1, ZoneType.Stock);
        Assert.False(b.Engine.CanPlay(b.P0, card)); // 没有红色

        b.Put(b.P0, d => d.Color == CardColor.Red, ZoneType.Clock);
        Assert.True(b.Engine.CanPlay(b.P0, card));
    }

    [Fact]
    public void Climax_NeedsColorEvenAtLevelZero()
    {
        var cx = Make.Climax(CardColor.Blue);
        var deck = new List<CardDefinition> { cx, Make.Char(color: CardColor.Blue) };
        deck.AddRange(Filler());
        var b = new Board(deck, Filler());
        var card = b.Put(b.P0, d => d == cx, ZoneType.Hand);
        Assert.False(b.Engine.CanPlay(b.P0, card));
        b.Put(b.P0, d => d.Color == CardColor.Blue, ZoneType.Clock);
        Assert.True(b.Engine.CanPlay(b.P0, card));
    }

    // ---------------------------------------------------------------- 攻击

    private static Board AttackBoard(int attackerPower, int defenderPower, int attackerSoul = 1, int defenderLevel = 0,
        CardDefinition? p0Top = null, IEnumerable<CardDefinition>? p1Top = null, bool withDefender = true)
    {
        var atk = Make.Char(power: attackerPower, soul: attackerSoul, name: "攻击者");
        var def = Make.Char(level: defenderLevel, power: defenderPower, name: "防守者");
        var deck0 = new List<CardDefinition>();
        if (p0Top != null) deck0.Add(p0Top);
        deck0.AddRange(Filler());
        deck0.Add(atk);
        var deck1 = new List<CardDefinition>();
        if (p1Top != null) deck1.AddRange(p1Top);
        deck1.AddRange(Filler());
        deck1.Add(def);
        var b = new Board(deck0, deck1);
        b.PutOnStage(b.P0, d => d == atk, StageSlot.FrontCenter);
        if (withDefender) b.PutOnStage(b.P1, d => d == def, StageSlot.FrontCenter);
        return b;
    }

    [Fact]
    public void FrontAttack_HigherPowerReversesDefender()
    {
        var b = AttackBoard(5000, 3000);
        Assert.False(b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Front)));
        var atk = b.P0.Stage[1]!;
        var def = b.P1.Stage[1]!;
        Assert.Equal(CardOrientation.Rested, atk.Orientation);
        Assert.Equal(CardOrientation.Reversed, def.Orientation);
        Assert.Single(b.P1.Clock);
        Assert.Single(b.P0.Stock); // 判定卡
    }

    [Fact]
    public void FrontAttack_EqualPowerReversesBoth()
    {
        var b = AttackBoard(4000, 4000);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Front));
        Assert.Equal(CardOrientation.Reversed, b.P0.Stage[1]!.Orientation);
        Assert.Equal(CardOrientation.Reversed, b.P1.Stage[1]!.Orientation);
    }

    [Fact]
    public void SideAttack_SoulMinusDefenderLevel_NoBattle()
    {
        var b = AttackBoard(1000, 9000, attackerSoul: 2, defenderLevel: 1);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Side));
        Assert.Single(b.P1.Clock);
        Assert.Equal(CardOrientation.Rested, b.P0.Stage[1]!.Orientation);
        Assert.Equal(CardOrientation.Standing, b.P1.Stage[1]!.Orientation);
    }

    [Fact]
    public void DirectAttack_SoulPlusOne()
    {
        var b = AttackBoard(3000, 0, attackerSoul: 1, withDefender: false);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Direct));
        Assert.Equal(2, b.P1.Clock.Count);
    }

    [Fact]
    public void SoulTrigger_AddsSoul()
    {
        var b = AttackBoard(3000, 0, attackerSoul: 1, withDefender: false, p0Top: Make.Climax(triggers: new[] { TriggerIcon.DoubleSoul }));
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Direct));
        Assert.Equal(4, b.P1.Clock.Count); // 魂 1 + 直接 1 + 判定 2
        Assert.Equal(2, b.P0.Stage[1]!.SoulModifierUntilEndOfTurn);
    }

    [Fact]
    public void ShotTrigger_DealsOneMoreDamageWhenCanceled()
    {
        var b = AttackBoard(3000, 0, attackerSoul: 1, withDefender: false,
            p0Top: Make.Climax(triggers: new[] { TriggerIcon.Shot }),
            p1Top: new[] { Make.Climax() });
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Direct));
        Assert.Single(b.P1.WaitingRoom); // 被取消的名场面
        Assert.Single(b.P1.Clock);       // 火烧的 1 点
    }

    [Fact]
    public void BarTrigger_PutsTriggerCardIntoHand()
    {
        var b = AttackBoard(3000, 0, withDefender: false, p0Top: Make.Climax(triggers: new[] { TriggerIcon.Bar }));
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Direct));
        Assert.Single(b.P0.Hand);
        Assert.True(b.P0.Hand[0].Def.IsClimax);
        Assert.Single(b.P0.Stock); // SimpleAgent 选择把牌库顶放到能量区
    }

    [Fact]
    public void Backup_RaisesDefenderPower()
    {
        var backup = Make.Char(backup: 3000, name: "助太刀");
        var b = AttackBoard(5000, 3000, p1Top: new[] { backup });
        b.Put(b.P1, d => d == backup, ZoneType.Hand);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Front));
        Assert.Equal(CardOrientation.Reversed, b.P0.Stage[1]!.Orientation);
        Assert.Equal(CardOrientation.Standing, b.P1.Stage[1]!.Orientation);
        Assert.Contains(b.P1.WaitingRoom, c => c.Def == backup);
    }

    [Fact]
    public void Backup_NeedsLevel()
    {
        var backup = Make.Char(level: 1, backup: 2000);
        var b = AttackBoard(5000, 3000, p1Top: new[] { backup });
        b.Put(b.P1, d => d == backup, ZoneType.Hand);
        b.Put(b.P1, _ => true, ZoneType.Stock);
        Assert.Empty(b.Engine.UsableBackups(b.P1)); // 等级 0
        b.Put(b.P1, _ => true, ZoneType.Level);
        Assert.Single(b.Engine.UsableBackups(b.P1));
    }

    // ---------------------------------------------------------------- 再演

    [Fact]
    public void RuleEncore_Pays3AndReturnsRested()
    {
        var b = AttackBoard(5000, 3000);
        for (int i = 0; i < 3; i++) b.Put(b.P1, _ => true, ZoneType.Stock);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Front));
        var def = b.P1.Stage[1]!;
        Assert.False(b.Step(b.Engine.EncoreStep));
        Assert.Same(def, b.P1.Stage[1]);
        Assert.Equal(CardOrientation.Rested, def.Orientation);
        Assert.Empty(b.P1.Stock);
        Assert.Equal(3, b.P1.WaitingRoom.Count);
    }

    [Fact]
    public void ReversedWithoutStock_GoesToWaitingRoom()
    {
        var b = AttackBoard(5000, 3000);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Front));
        b.Step(b.Engine.EncoreStep);
        Assert.Null(b.P1.Stage[1]);
        Assert.Single(b.P1.WaitingRoom);
    }

    [Fact]
    public void Encore_ClearsModifiers()
    {
        var backup = Make.Char(backup: 1000);
        var b = AttackBoard(5000, 3000, p1Top: new[] { backup });
        b.Put(b.P1, d => d == backup, ZoneType.Hand);
        for (int i = 0; i < 3; i++) b.Put(b.P1, _ => true, ZoneType.Stock);
        b.Step(() => b.Engine.PerformAttack(b.P0, b.P1, StageSlot.FrontCenter, AttackType.Front));
        b.Step(b.Engine.EncoreStep);
        Assert.Equal(0, b.P1.Stage[1]!.PowerModifierUntilEndOfTurn);
    }

    // ---------------------------------------------------------------- 结束阶段

    [Fact]
    public void EndPhase_DiscardsToSeven_AndClearsClimaxArea()
    {
        var b = new Board(Filler(30), Filler());
        for (int i = 0; i < 9; i++) b.Put(b.P0, _ => true, ZoneType.Hand);
        var cx = b.Put(b.P0, _ => true, ZoneType.ClimaxArea);
        b.Step(() => b.Engine.EndPhase(b.P0));
        Assert.Equal(7, b.P0.Hand.Count);
        Assert.Empty(b.P0.ClimaxArea);
        Assert.Equal(3, b.P0.WaitingRoom.Count);
        Assert.Contains(cx, b.P0.WaitingRoom);
    }
}

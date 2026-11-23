using WsSim.Core.Cards;
using WsSim.Core.Game;

namespace WsSim.Core.Effects;

/// <summary>
/// 静态检查卡片脚本里有没有解释器还不认识的东西（指令、时机、【永】类型、条件、费用）。
/// “可用”的卡 = 一个不认识的都没有。用于卡组编辑里的标记和 docs 里的覆盖率报告。
/// </summary>
public static class ScriptCoverage
{
    /// <summary>引擎会触发的【自】时机（小写）。</summary>
    public static readonly HashSet<string> SupportedTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnPlay", "OnAttack", "OnAttacked", "OnCancel", "OnClimaxPlay", "OnDamageCancel", "OnDamageCanceled", "OnDamageNotCanceled",
        "OnDirectAttack", "OnDiscard", "OnEndOfAttack", "OnFacingAttacks", "OnFrontalAttack", "OnFrontalAttacked", "OnLeaveStage",
        "OnLevelUp", "OnOpponentClimaxPlay", "OnOpponentLevelUp", "OnOpponentReverse", "OnOtherAttacks", "OnOtherCharacterAttacks",
        "OnOtherFrontalAttacked", "OnOtherOpponentReversed", "OnOtherPlacedOnStageFromHand", "OnOtherPlacedOnStageFromWaitingRoom",
        "OnOtherReversed", "OnOtherSentFromStageToWaitingRoom", "OnPlacedOnStageFromDeck", "OnPlacedOnStageFromWaitingRoom",
        "OnPlacedOnStageFromMemory", "OnPlacedOnStageFromHand", "OnPlacedOnStageFromClock", "OnPlacedOnStageFromStock",
        "OnPlacedOnStageFromLevel", "OnPlacedOnStageFromMarker", "OnReceiveDamage", "OnReversed", "OnReversedInBattle",
        "OnReverseOpponent", "OnReverseOpponent_Conditional", "OnSentFromStageToElsewhere", "OnSentFromStageToWaitingRoom",
        "OnSideAttack", "OnSpecificClimaxPlay", "OnThisTriggerCheck", "OnTriggerCheck", "OnTriggerCheckClimax", "OnUseAct",
        "OnUseEvent", "OnUseThisCardsBackup", "OnHealToWaitingRoom", "OnOpponentHealToWaitingRoom", "OnGainMarker", "UseThisCardsBackup", "PlacedBy", "PlacedByIf", "Encore",
        "StartEncoreStep", "StartEncorePhase", "StartMyEncoreStep", "StartMyEncorePhase", "StartOpponentEncoreStep",
        "StartStandPhase", "StartDrawPhase", "StartClockPhase", "StartMainPhase", "StartClimaxPhase", "StartAttackPhase", "StartEndPhase",
        "StartMyStandPhase", "StartMyDrawPhase", "StartMyClockPhase", "StartMyMainPhase", "StartMyClimaxPhase", "StartMyAttackPhase",
        "StartOpponentStandPhase", "StartOpponentDrawPhase", "StartOpponentClockPhase", "StartOpponentMainPhase",
        "StartOpponentClimaxPhase", "StartOpponentAttackPhase", "StartOpponentEndPhase",
        "StartOpponentsDrawPhase", "StartOpponentsClimaxPhase", "StartOpponentsAttackPhase",
    };

    public static readonly HashSet<string> SupportedContKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "GiveSelf", "GiveAll", "GiveAllYourCards", "Assist", "GiveFacing", "InHand", "GiveHand", "GiveSelfEverywhere",
        "Global", "Memory", "GiveSelfInMemory", "During",
    };

    public sealed record CardReport(CardDefinition Card, IReadOnlyList<string> Missing)
    {
        public bool FullySupported => Missing.Count == 0;
    }

    public static CardReport Analyze(CardDefinition def)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var script = ScriptCache.Get(def);
        foreach (var p in script.Problems) missing.Add("语法：" + p);
        foreach (var f in script.Flags)
        {
            if (ScriptParser.IsHeader(f)) { missing.Add("能力头 " + f.Name); continue; }
            if (!GameEngine.SupportedFlags.Contains(f.Name)) missing.Add(f.Name);
        }
        foreach (var ab in script.Abilities) CheckAbility(ab, missing);
        return new CardReport(def, missing.ToList());
    }

    private static void CheckAbility(Ability ab, SortedSet<string> missing)
    {
        switch (ab.Kind)
        {
            case AbilityKind.Auto:
                foreach (var t in ab.Trigger.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!SupportedTriggers.Contains(t)) missing.Add("Auto: " + t);
                if (ab.TriggerKey == "encore") CheckCost(ab.HeaderArgs, missing);
                break;
            case AbilityKind.Cont:
                if (!SupportedContKinds.Contains(ab.Trigger)) missing.Add("Cont: " + ab.Trigger);
                break;
            case AbilityKind.Act:
                CheckCost(ab.HeaderArgs, missing);
                break;
        }
        foreach (var m in ab.Modifiers)
            if (m.Key.StartsWith("activationrequirement")) CheckCond(m.Args, missing);
        CheckBody(ab.Body, missing);
    }

    private static void CheckBody(List<ScriptNode>? body, SortedSet<string> missing)
    {
        if (body == null) return;
        foreach (var n in body)
        {
            if (ScriptParser.IsHeader(n))
            {
                missing.Add("能力头 " + n.Name);
                continue;
            }
            if (!GameEngine.SupportedCommands.Contains(n.Name)) missing.Add(n.Name);
            switch (n.Key)
            {
                case "if": case "iftarget": case "ifthatcard": case "ifother": case "ifowner": case "narrowtargets":
                case "setxtonumberofcharacters": case "searchdeck": case "searchwaitingroom": case "searchwaitingroomfortarget":
                    CheckCond(n.Args, missing);
                    break;
                case "paycost": case "forcepaycost":
                    CheckCost(n.Args, missing);
                    break;
                case "setxvalue": case "addtoxvalue":
                    CheckQuantity(n.Args, missing);
                    break;
                case "searchlocationfortargets": case "opponentsearchlocationfortargets":
                {
                    var w = n.Args.Split(' ', 4);
                    if (w.Length > 3) CheckCond(w[3], missing);
                    break;
                }
                case "choosecardsfromtargets": case "choosemultiplecharacters":
                {
                    var w = n.Args.Split(' ', 3);
                    if (w.Length > 2) CheckCond(w[2], missing);
                    break;
                }
                case "gaineffect": case "scheduleeffect": case "scheduleeffectfortargets": case "scheduleeffectuntil":
                    foreach (var ab in GameEngine.AbilitiesInBlock(n)) CheckAbility(ab, missing);
                    continue;
            }
            CheckBody(n.Block, missing);
        }
    }

    private static readonly HashSet<string> Quantities = new(StringComparer.OrdinalIgnoreCase)
    {
        "X", "Answer", "ThatCardsLevel", "ThatCardsCost", "ThatCardsSoul", "ThatCardsPower", "ThisCardsLevel", "ThisCardsPower",
        "ThisCardsSoul", "TargetCardsLevel", "TargetCardsCost", "TargetCardsSoul", "TargetCardsPower", "TargetCount", "MyLevel",
        "OpponentsLevel", "OpponentLevel", "Experience", "Expeirience", "TotalLevel", "BrainstormedClimax", "MarkersUnderThis", "ThisCardsMarkers",
        "ClimaxInWaitingRoom", "Characters", "NumberOfCharacters", "OpponentsCharacters",
    };

    private static void CheckQuantity(string q, SortedSet<string> missing)
    {
        q = q.Trim().Split(' ')[0].TrimStart('-');
        if (q.Length == 0 || int.TryParse(q, out _) || Quantities.Contains(q) || q.StartsWith("CardsIn", StringComparison.OrdinalIgnoreCase)) return;
        missing.Add("数量 " + q);
    }

    private static void CheckCond(string text, SortedSet<string> missing) => CheckCond(CondParser.Parse(text), missing);

    private static void CheckCond(Cond c, SortedSet<string> missing)
    {
        switch (c)
        {
            case AndCond a: foreach (var p in a.Parts) CheckCond(p, missing); break;
            case OrCond o: foreach (var p in o.Parts) CheckCond(p, missing); break;
            case NotCond n: CheckCond(n.Inner, missing); break;
            case AtomCond atom:
                if (!CondParser.KnownAtoms.Contains(atom.Name)) missing.Add("条件 " + atom.Name);
                if (atom.Key == "compare")
                {
                    var w = atom.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length > 0) CheckQuantity(w[0], missing);
                }
                if (atom.Filter != null) CheckCond(atom.Filter, missing);
                break;
        }
    }

    private static void CheckCost(string text, SortedSet<string> missing)
    {
        foreach (var item in GameEngine.ParseCost(text))
        {
            if (item.Kind == "Unknown") missing.Add("费用 " + item.Cond.Split(' ')[0]);
            else if (item.Cond.Length > 0 && item.Kind != "Resonate") CheckCond(item.Cond, missing);
        }
    }
}

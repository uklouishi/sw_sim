using Godot;
using WsSim.Core.Cards;
using WsSim.Core.Game;

namespace WsSim.Client;

/// <summary>界面用的中文名称和颜色。</summary>
public static class Texts
{
    public static string Phase(Phase p) => p switch
    {
        WsSim.Core.Game.Phase.Setup => "开局",
        WsSim.Core.Game.Phase.Stand => "正置阶段",
        WsSim.Core.Game.Phase.Draw => "抽牌阶段",
        WsSim.Core.Game.Phase.Clock => "伤害阶段",
        WsSim.Core.Game.Phase.Main => "主要阶段",
        WsSim.Core.Game.Phase.Climax => "名场面阶段",
        WsSim.Core.Game.Phase.Attack => "攻击阶段",
        WsSim.Core.Game.Phase.Encore => "再演步骤",
        WsSim.Core.Game.Phase.End => "结束阶段",
        _ => "对局结束",
    };

    public static string Type(CardType t) => t switch
    {
        CardType.Character => "角色",
        CardType.Event => "事件",
        _ => "名场面",
    };

    public static string ColorName(CardColor c) => c switch
    {
        CardColor.Yellow => "黄",
        CardColor.Green => "绿",
        CardColor.Red => "红",
        CardColor.Blue => "蓝",
        _ => "无",
    };

    public static Color CardTint(CardColor c) => c switch
    {
        CardColor.Yellow => new Color("#d9b334"),
        CardColor.Green => new Color("#4f9a4a"),
        CardColor.Red => new Color("#c2453c"),
        CardColor.Blue => new Color("#3f6fc0"),
        _ => new Color("#888888"),
    };

    public static string Trigger(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "soul" => "魂",
        "doublesoul" or "soul2" or "2soul" => "魂×2",
        "wind" or "return" => "风吹",
        "bag" or "pool" => "宝袋",
        "door" or "comeback" => "木门",
        "book" or "draw" => "书",
        "gate" => "城门",
        "shot" => "火烧",
        "standby" => "开机",
        "choice" => "箭头",
        "bar" or "treasure" => "金砖",
        "chance" or "bulb" or "light" => "灯泡",
        "telescope" or "discovery" => "望远镜",
        "focus" => "橘子",
        _ => raw,
    };

    /// <summary>数据里没有、需要按字段生成的效果文字（助太刀、反击）。</summary>
    public static IEnumerable<string> GeneratedTexts(CardDefinition def)
    {
        if (def.BackupPower is int bp)
        {
            int cost = GameEngine.BackupStockCost(def);
            yield return $"【起】【反击】助太刀 {bp}　等级 {def.Level}　[{(cost > 0 ? $"({cost}) " : "")}将这张卡从手牌放到休息室]";
        }
        if (def.IsCounter) yield return "【反击】";
    }
}

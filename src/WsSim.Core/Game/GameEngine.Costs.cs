using WsSim.Core.Effects;

namespace WsSim.Core.Game;

/// <summary>效果脚本：费用（PayCost、【起】的头、再演）。</summary>
public sealed partial class GameEngine
{
    /// <summary>一项费用。Kind：Stock、Discard、Rest、RestSelf、SendSelf、Clock、StockClock、HandClock、Send、
    /// SendFromHand、SendFromWaitingRoom、ShuffleFromWaitingRoom、SendClimax、SendMarker、Resonate。</summary>
    internal sealed record CostItem(string Kind, int N, string Loc, string Cond);

    private static readonly HashSet<string> CostKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discard", "Rest", "RestSelf", "SendSelf", "Clock", "StockClock", "HandClock", "Send", "SendFromHand",
        "SendFromWaitingRoom", "ShuffleFromWaitingRoom", "SendClimax", "SendMarker", "Resonate", "RevealFromHand",
        "ReturnSelf", "MarkerToWaitingRoom", "SendMarkers",
    };

    /// <summary>这些词后面跟的 Rest / Clock 是条件的一部分，不是新的费用。</summary>
    private static readonly HashSet<string> CondContextWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CardPosition", "InLocation", "LocationContains", "LocationContainsOrLess", "Not", "&&", "||", "CardsIn",
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<CostItem>> CostCache = new();

    internal static List<CostItem> ParseCost(string text) => CostCache.GetOrAdd(text ?? "", ParseCostUncached);

    private static List<CostItem> ParseCostUncached(string text)
    {
        var items = new List<CostItem>();
        var tokens = text.Replace("&&&", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int i = 0;
        // 开头的数字是能量。
        if (i < tokens.Count && int.TryParse(tokens[i], out var stock))
        {
            if (stock > 0) items.Add(new CostItem("Stock", stock, "", ""));
            i++;
        }
        while (i < tokens.Count)
        {
            var kw = tokens[i];
            if (!CostKeywords.Contains(kw))
            {
                // 不认识的词：作为未知费用整体记下。
                items.Add(new CostItem("Unknown", 0, "", string.Join(" ", tokens.Skip(i))));
                break;
            }
            i++;
            // 收集参数，到下一个费用关键词为止（条件里 CardPosition Rest 这类不算）。
            var rest = new List<string>();
            bool noArgs = kw.ToLowerInvariant() is "restself" or "clock" or "stockclock";
            while (!noArgs && i < tokens.Count)
            {
                if (rest.Count > 0 && CostKeywords.Contains(tokens[i]) && !CondContextWords.Contains(rest[^1])) break;
                rest.Add(tokens[i]);
                i++;
            }
            items.Add(MakeCostItem(kw, rest));
        }
        return items;
    }

    private static CostItem MakeCostItem(string kw, List<string> a)
    {
        int N(int idx, int def = 1) => idx < a.Count && int.TryParse(a[idx], out var n) ? n : def;
        string Join(int from) => string.Join(" ", a.Skip(from));
        var k = kw.ToLowerInvariant();
        return k switch
        {
            "discard" => new CostItem("Discard", N(0), "", Join(1)),
            "rest" => new CostItem("Rest", N(0), "", Join(1)),
            "restself" => new CostItem("RestSelf", 1, "", ""),
            "sendself" or "returnself" => new CostItem("SendSelf", 1, a.Count > 0 ? a[0] : (k == "returnself" ? "Hand" : "WaitingRoom"), ""),
            "clock" => new CostItem("Clock", 1, "", ""),
            "stockclock" => new CostItem("StockClock", 1, "", ""),
            "handclock" => new CostItem("HandClock", 1, "", Join(0)),
            "send" => new CostItem("Send", N(0), a.Count > 1 ? a[1] : "WaitingRoom", Join(2)),
            "sendfromhand" => new CostItem("SendFromHand", N(0), a.Count > 1 ? a[1] : "WaitingRoom", Join(2)),
            "sendfromwaitingroom" => new CostItem("SendFromWaitingRoom", N(0), a.Count > 1 ? a[1] : "Deck", Join(2)),
            "shufflefromwaitingroom" => new CostItem("ShuffleFromWaitingRoom", N(0), "Deck", Join(1)),
            "sendclimax" => new CostItem("SendClimax", 1, a.Count > 0 ? a[0] : "WaitingRoom", ""),
            "sendmarker" or "sendmarkers" or "markertowaitingroom" => new CostItem("SendMarker", N(0), a.Count > 1 ? a[1] : "WaitingRoom", ""),
            "resonate" or "revealfromhand" => new CostItem("Resonate", 1, "", Join(0)),
            _ => new CostItem("Unknown", 0, "", kw + " " + Join(0)),
        };
    }

    private List<CardInstance> CostCandidates(CostItem c, EffectContext ctx)
    {
        var p = State.Players[ctx.Controller];
        bool Match(CardInstance x) => Eval(c.Cond.Length == 0 ? "Any" : c.Cond, ctx, x);
        return c.Kind switch
        {
            "Discard" or "HandClock" or "SendFromHand" => p.Hand.Where(Match).ToList(),
            "Resonate" => p.Hand.Where(x => NameMatches(x, c.Cond)).ToList(),
            "Rest" => p.StageCards.Where(x => x.Orientation == CardOrientation.Standing && Match(x)).ToList(),
            "Send" => p.StageCards.Where(Match).ToList(),
            "SendFromWaitingRoom" or "ShuffleFromWaitingRoom" => p.WaitingRoom.Where(Match).ToList(),
            "SendMarker" => ctx.Source.Markers.ToList(),
            _ => new List<CardInstance>(),
        };
    }

    internal bool CanPayCost(string? text, EffectContext ctx) => CanPayCost(ParseCost(text ?? ""), ctx);

    internal bool CanPayCost(List<CostItem> items, EffectContext ctx)
    {
        var p = State.Players[ctx.Controller];
        var src = ctx.Source;
        int stockNeeded = 0;
        foreach (var c in items)
        {
            switch (c.Kind)
            {
                case "Stock": stockNeeded += c.N; break;
                case "StockClock": stockNeeded += 1; break;
                case "RestSelf":
                    if (src.Zone != ZoneType.Stage || src.Orientation != CardOrientation.Standing) return false;
                    break;
                case "SendSelf":
                    if (src.Zone is ZoneType.Deck or ZoneType.Resolution) return false;
                    break;
                case "Clock": if (p.Deck.Count == 0 && p.WaitingRoom.Count == 0) return false; break;
                case "SendClimax": if (p.ClimaxArea.Count == 0) return false; break;
                case "Unknown": return false;
                default:
                    if (CostCandidates(c, ctx).Count < Math.Max(1, c.N)) return false;
                    break;
            }
        }
        if (stockNeeded > 0 && p.HasFlag("CannotPayStock")) return false;
        return p.Stock.Count >= stockNeeded;
    }

    /// <summary>支付费用（调用前先 <see cref="CanPayCost(List{CostItem}, EffectContext)"/>）。</summary>
    internal void PayCost(List<CostItem> items, EffectContext ctx)
    {
        var p = State.Players[ctx.Controller];
        var src = ctx.Source;
        // 能量先付。
        int stock = items.Where(c => c.Kind == "Stock").Sum(c => c.N);
        if (stock > 0) PayStock(p, stock);
        foreach (var c in items)
        {
            switch (c.Kind)
            {
                case "Stock": break;
                case "RestSelf":
                    if (src.Zone == ZoneType.Stage) SetOrientation(src, CardOrientation.Rested);
                    break;
                case "SendSelf":
                    if (src.Zone == ZoneType.Stage) ctx.RememberedSlot ??= p.SlotOf(src);
                    MoveToNamedZone(src, c.Loc, ctx);
                    break;
                case "Clock":
                {
                    var top = TakeTopOfDeck(p);
                    if (top != null) MoveTo(top, ZoneType.Clock);
                    break;
                }
                case "StockClock":
                    if (p.Stock.Count > 0) MoveTo(p.Stock[^1], ZoneType.Clock);
                    break;
                case "SendClimax":
                    if (p.ClimaxArea.Count > 0) MoveToNamedZone(p.ClimaxArea[^1], c.Loc, ctx);
                    break;
                case "Resonate":
                {
                    var r = AskChooseCards(ctx.Controller, "共鸣：选择要展示的手牌", CostCandidates(c, ctx), 1, 1);
                    foreach (var x in r) Info($"P{ctx.Controller} 展示 {x}");
                    break;
                }
                default:
                {
                    var cands = CostCandidates(c, ctx);
                    int n = Math.Min(Math.Max(1, c.N), cands.Count);
                    string what = c.Kind switch
                    {
                        "Discard" => $"选择 {n} 张手牌放到休息室",
                        "HandClock" => "选择 1 张手牌放到伤害区",
                        "Rest" => $"选择 {n} 个角色横置",
                        "Send" => $"选择 {n} 个角色放到{ZoneName(c.Loc)}",
                        "SendFromHand" => $"选择 {n} 张手牌放到{ZoneName(c.Loc)}",
                        "SendFromWaitingRoom" => $"从休息室选择 {n} 张放到{ZoneName(c.Loc)}",
                        "ShuffleFromWaitingRoom" => $"从休息室选择 {n} 张放回牌库",
                        "SendMarker" => $"选择 {n} 张标记放到{ZoneName(c.Loc)}",
                        _ => "支付费用",
                    };
                    var chosen = AskChooseCards(ctx.Controller, $"费用：{what}", cands, n, n);
                    foreach (var x in chosen)
                    {
                        switch (c.Kind)
                        {
                            case "Discard": MoveTo(x, ZoneType.WaitingRoom); break;
                            case "HandClock": MoveTo(x, ZoneType.Clock); break;
                            case "Rest": SetOrientation(x, CardOrientation.Rested); break;
                            case "ShuffleFromWaitingRoom": MoveTo(x, ZoneType.Deck); break;
                            default: MoveToNamedZone(x, c.Loc, ctx); break;
                        }
                    }
                    if (c.Kind == "ShuffleFromWaitingRoom") Shuffle(p);
                    if (c.Kind == "Discard") foreach (var x in chosen) FireDiscarded(x);
                    break;
                }
            }
        }
    }

    internal static string ZoneName(string loc)
    {
        var z = ZoneOf(loc);
        var name = z?.Zone switch
        {
            ZoneType.Hand => "手牌",
            ZoneType.Deck => z.Value.Bottom ? "牌库底" : "牌库顶",
            ZoneType.WaitingRoom => "休息室",
            ZoneType.Stock => "能量区",
            ZoneType.Clock => "伤害区",
            ZoneType.Level => "等级区",
            ZoneType.Memory => "记忆区",
            ZoneType.ClimaxArea => "名场面区",
            ZoneType.Stage => "舞台",
            ZoneType.Marker => "标记",
            _ => loc,
        };
        return (z?.Opponent == true ? "对手的" : "") + name;
    }

    /// <summary>费用的中文说明（界面显示用）。</summary>
    internal static string DescribeCost(string text)
    {
        var parts = new List<string>();
        foreach (var c in ParseCost(text))
        {
            parts.Add(c.Kind switch
            {
                "Stock" => $"({c.N})",
                "Discard" => $"弃 {c.N} 张手牌",
                "Rest" => $"横置 {c.N} 个角色",
                "RestSelf" => "横置这张卡",
                "SendSelf" => $"这张卡放到{ZoneName(c.Loc)}",
                "Clock" => "牌库顶 1 张放到伤害区",
                "StockClock" => "能量区顶 1 张放到伤害区",
                "HandClock" => "1 张手牌放到伤害区",
                "Send" => $"{c.N} 个角色放到{ZoneName(c.Loc)}",
                "SendFromWaitingRoom" => $"休息室 {c.N} 张放到{ZoneName(c.Loc)}",
                "ShuffleFromWaitingRoom" => $"休息室 {c.N} 张洗回牌库",
                "SendClimax" => $"名场面放到{ZoneName(c.Loc)}",
                "SendMarker" => $"{c.N} 张标记放到{ZoneName(c.Loc)}",
                "Resonate" => "展示手牌",
                _ => c.Cond,
            });
        }
        return parts.Count == 0 ? "无" : string.Join("、", parts);
    }
}

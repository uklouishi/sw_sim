using System.Text.Json;
using System.Text.RegularExpressions;

namespace WsSim.Core.Cards;

/// <summary>
/// 卡片数据库：读取开发包的 cards.json（或同格式的 subset_*.json）。
/// 同一卡号重复出现时保留第一份。卡号区分大小写查找失败时再不区分大小写查一次。
/// </summary>
public sealed class CardDatabase
{
    private readonly Dictionary<string, CardDefinition> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CardDefinition> _byIdIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byId.Count;
    public IEnumerable<CardDefinition> All => _byId.Values;

    public static CardDatabase LoadFiles(params string[] paths)
    {
        var db = new CardDatabase();
        foreach (var p in paths)
        {
            using var fs = File.OpenRead(p);
            db.AddFromJson(fs);
        }
        return db;
    }

    public void AddFromJson(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("cards.json 的根元素应为数组");
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (CloneTarget(e) != null)
            {
                _pendingClones.Add(e.Clone());
                continue;
            }
            Add(Parse(e, null));
        }
        ResolveClones();
    }

    private readonly List<JsonElement> _pendingClones = new();

    /// <summary>还没找到被复制对象的 Clone 卡数量（被复制的卡可能在之后加载的文件里）。</summary>
    public int UnresolvedCloneCount => _pendingClones.Count;

    private static readonly Regex CloneRe = new(@"^\s*Clone\s+(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static string? CloneTarget(JsonElement e)
    {
        var m = CloneRe.Match(Str(e, "script") ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// <c>Clone X</c>：这张卡视为卡号 X 的卡（再录、异画）。没写的字段全部沿用 X，写了的字段以自己为准。
    /// Clone 链按加载顺序反复解析，直到不再有进展。
    /// </summary>
    private void ResolveClones()
    {
        bool progress = true;
        while (progress && _pendingClones.Count > 0)
        {
            progress = false;
            for (int i = _pendingClones.Count - 1; i >= 0; i--)
            {
                var e = _pendingClones[i];
                if (!TryGet(CloneTarget(e)!, out var target)) continue;
                Add(Parse(e, target));
                _pendingClones.RemoveAt(i);
                progress = true;
            }
        }
    }

    public void Add(CardDefinition card)
    {
        if (_byId.TryAdd(card.Id, card))
            _byIdIgnoreCase.TryAdd(card.Id, card);
    }

    public bool TryGet(string id, out CardDefinition card)
    {
        if (_byId.TryGetValue(id, out card!)) return true;
        return _byIdIgnoreCase.TryGetValue(id, out card!);
    }

    public CardDefinition Get(string id) =>
        TryGet(id, out var c) ? c : throw new KeyNotFoundException($"找不到卡号 {id}");

    private static readonly Regex BackupRe = new(@"^\s*Backup\s+(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex CounterRe = new(@"^\s*Counter\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static CardDefinition Parse(JsonElement e) => Parse(e, null);

    /// <summary>解析一张卡；@base 不为 null 时（Clone 卡），JSON 里没有的字段沿用 @base。</summary>
    internal static CardDefinition Parse(JsonElement e, CardDefinition? @base)
    {
        string id = Str(e, "id") ?? throw new InvalidDataException("卡片缺少 id");
        var type = (Str(e, "type") ?? "").ToLowerInvariant() switch
        {
            "character" => CardType.Character,
            "event" => CardType.Event,
            "climax" => CardType.Climax,
            var t => throw new InvalidDataException($"{id}: 未知卡片类型 {t}")
        };
        if (@base != null)
        {
            bool Has(string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null &&
                                     !(v.ValueKind == JsonValueKind.String && v.GetString() == "") &&
                                     !(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);
            var own = Parse(e, null);
            string ownScript = Str(e, "script_expanded") ?? "";
            return new CardDefinition
            {
                Id = id,
                Type = @base.Type,
                Name = Has("name") ? own.Name : @base.Name,
                JpName = Has("jp_name") ? own.JpName : @base.JpName,
                Color = Has("color") ? own.Color : @base.Color,
                Level = Has("level") ? own.Level : @base.Level,
                Cost = Has("cost") ? own.Cost : @base.Cost,
                Power = Has("power") ? own.Power : @base.Power,
                Soul = Has("soul") ? own.Soul : @base.Soul,
                RawTriggers = Has("trigger") ? own.RawTriggers : @base.RawTriggers,
                Triggers = Has("trigger") ? own.Triggers : @base.Triggers,
                Traits = Has("traits") ? own.Traits : @base.Traits,
                Combo = Has("combo") ? own.Combo : @base.Combo,
                Series = own.Series.Count > 0 ? own.Series : @base.Series,
                Image = own.Image,
                TextsEn = Has("texts_en") ? own.TextsEn : @base.TextsEn,
                ScriptExpanded = CloneRe.Replace(ownScript, "").Trim().Length > 0 ? ownScript : @base.ScriptExpanded,
                BackupPower = own.BackupPower ?? @base.BackupPower,
                IsCounter = own.IsCounter || @base.IsCounter,
                CloneOf = @base.Id,
            };
        }
        var rawTriggers = StrList(e, "trigger");
        string script = Str(e, "script_expanded") ?? "";
        var bm = BackupRe.Match(script);
        return new CardDefinition
        {
            Id = id,
            Type = type,
            Name = Str(e, "name") ?? "",
            JpName = Str(e, "jp_name"),
            Color = ParseColor(Str(e, "color")),
            Level = Int(e, "level"),
            Cost = Int(e, "cost"),
            Power = Int(e, "power"),
            // 原数据大部分角色不写 Soul 字段（2.1 万张），缺省按 1 处理；明确写 0 的保留 0。
            Soul = type == CardType.Character && !e.TryGetProperty("soul", out _) ? 1 : Int(e, "soul"),
            RawTriggers = rawTriggers,
            Triggers = rawTriggers.Select(ParseTrigger).ToArray(),
            Traits = StrList(e, "traits"),
            Combo = Str(e, "combo"),
            Series = StrList(e, "series"),
            Image = Str(e, "image"),
            TextsEn = StrList(e, "texts_en"),
            ScriptExpanded = script,
            BackupPower = bm.Success ? int.Parse(bm.Groups[1].Value) : null,
            IsCounter = CounterRe.IsMatch(script),
        };
    }

    public static CardColor ParseColor(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "y" or "yellow" => CardColor.Yellow,
        "g" or "green" => CardColor.Green,
        "r" or "red" => CardColor.Red,
        "b" or "blue" => CardColor.Blue,
        _ => CardColor.None,
    };

    public static TriggerIcon ParseTrigger(string s) => s.Trim().ToLowerInvariant() switch
    {
        "soul" => TriggerIcon.Soul,
        "doublesoul" => TriggerIcon.DoubleSoul,
        "wind" => TriggerIcon.Wind,
        "bag" => TriggerIcon.Bag,
        "door" => TriggerIcon.Door,
        "book" => TriggerIcon.Book,
        "gate" => TriggerIcon.Gate,
        "shot" => TriggerIcon.Shot,
        "standby" => TriggerIcon.Standby,
        "choice" => TriggerIcon.Choice,
        "bar" => TriggerIcon.Bar,
        _ => TriggerIcon.Other,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>数值字段偶尔是字符串（数据不规范），读不出数字时按 0 处理。</summary>
    private static int Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;
        return 0;
    }

    private static IReadOnlyList<string> StrList(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
    }
}

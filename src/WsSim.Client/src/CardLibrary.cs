using System.Text.Json;
using Godot;
using WsSim.Core.Cards;

namespace WsSim.Client;

/// <summary>
/// 卡片数据（ws-devkit 的 cards.json）、简体中文效果文字和卡图。
/// 卡图不打包，运行时从原模拟器的 StreamingAssets/Cards 读取。
/// </summary>
public sealed class CardLibrary
{
    public CardDatabase Db { get; }
    public string DevkitPath { get; }
    public string ImageRoot { get; set; }

    private readonly Dictionary<string, string> _zh;
    private readonly Dictionary<string, Texture2D?> _textures = new();

    private CardLibrary(CardDatabase db, Dictionary<string, string> zh, string devkit, string imageRoot)
    {
        Db = db;
        _zh = zh;
        DevkitPath = devkit;
        ImageRoot = imageRoot;
    }

    public static bool IsDevkit(string path) =>
        !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "data", "cards.json"));

    /// <summary>读取开发包（几秒钟，请在后台线程调用）。</summary>
    public static CardLibrary Load(string devkit, string imageRoot)
    {
        var db = CardDatabase.LoadFiles(Path.Combine(devkit, "data", "cards.json"));
        var zh = new Dictionary<string, string>();
        var zhPath = Path.Combine(devkit, "i18n", "cards.zh-CN.json");
        if (File.Exists(zhPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(zhPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("text", out var t) && t.GetString() is { } s)
                    zh[prop.Name] = s;
        }
        return new CardLibrary(db, zh, devkit, imageRoot);
    }

    /// <summary>可选的卡组：开发包 tests/decks、原模拟器 StreamingAssets/Decks、user://decks。键是显示名，值是路径。</summary>
    public SortedDictionary<string, string> FindDecks()
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<(string Dir, string Tag)> { (Path.Combine(DevkitPath, "tests", "decks"), "") };
        if (!string.IsNullOrEmpty(ImageRoot))
        {
            var sa = Directory.GetParent(ImageRoot.TrimEnd('/', '\\'))?.FullName;
            if (sa != null) dirs.Add((Path.Combine(sa, "Decks"), "原模拟器/"));
        }
        dirs.Add((ProjectSettings.GlobalizePath("user://decks"), "我的/"));
        foreach (var (dir, tag) in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.txt"))
            {
                var name = tag + Path.GetFileNameWithoutExtension(f);
                if (name.EndsWith("Placeholder", StringComparison.OrdinalIgnoreCase)) continue;
                result[name] = f;
            }
        }
        return result;
    }

    /// <summary>读卡组；有不认识的卡号时抛出异常并说明是哪张。</summary>
    public Deck LoadDeck(string path)
    {
        var lines = File.ReadAllLines(path);
        var unknown = lines.Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("//") && !Db.TryGet(l, out _))
            .Distinct().ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"卡组 {Path.GetFileName(path)} 里有不认识的卡号：{string.Join("、", unknown.Take(5))}");
        return Deck.Parse(Path.GetFileNameWithoutExtension(path), lines, Db);
    }

    /// <summary>效果文字：优先简体中文，没有时用英文。</summary>
    public IReadOnlyList<string> EffectTexts(CardDefinition def)
    {
        var list = new List<string>();
        for (int i = 0; i < def.TextsEn.Count; i++)
        {
            if (_zh.TryGetValue($"{def.Id}#{i}", out var zh) ||
                (def.CloneOf != null && _zh.TryGetValue($"{def.CloneOf}#{i}", out zh)))
                list.Add(zh);
            else
                list.Add(def.TextsEn[i]);
        }
        return list;
    }

    /// <summary>卡图；找不到返回 null（界面改为画文字卡面）。结果会缓存。</summary>
    public Texture2D? GetTexture(CardDefinition def)
    {
        if (string.IsNullOrEmpty(def.Image) || string.IsNullOrEmpty(ImageRoot)) return null;
        if (_textures.TryGetValue(def.Image, out var cached)) return cached;
        Texture2D? tex = null;
        var path = Path.Combine(ImageRoot, def.Image);
        if (File.Exists(path))
        {
            var img = new Image();
            if (img.Load(path) == Error.Ok)
            {
                // 原图较大，缩到 400 高以节省显存。
                if (img.GetHeight() > 400)
                    img.Resize(img.GetWidth() * 400 / img.GetHeight(), 400, Image.Interpolation.Lanczos);
                tex = ImageTexture.CreateFromImage(img);
            }
        }
        _textures[def.Image] = tex;
        return tex;
    }
}

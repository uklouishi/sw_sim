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

    /// <summary>卡图文件的完整路径（不检查是否存在）；卡片没有登记卡图或没设卡图目录时为 null。</summary>
    public string? ImagePath(CardDefinition def)
    {
        if (string.IsNullOrEmpty(def.Image) || string.IsNullOrEmpty(ImageRoot)) return null;
        var rel = def.Image.Replace('\\', '/').TrimStart('/');
        return Path.GetFullPath(Path.Combine(ImageRoot.Trim(), rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>上一张没能显示的卡图的原因（显示在卡片详情里，方便排查）。</summary>
    public string? ImageProblem(CardDefinition def) => _problems.TryGetValue(def.Id, out var p) ? p : null;

    private readonly Dictionary<string, string> _problems = new();

    /// <summary>卡图；找不到返回 null（界面改为画文字卡面）。结果会缓存。</summary>
    public Texture2D? GetTexture(CardDefinition def)
    {
        var path = ImagePath(def);
        if (path == null)
        {
            _problems[def.Id] = string.IsNullOrEmpty(ImageRoot) ? "没有设置卡图目录" : "开发包里这张卡没有登记卡图";
            return null;
        }
        if (_textures.TryGetValue(path, out var cached))
        {
            if (cached == null && !_problems.ContainsKey(def.Id)) _problems[def.Id] = "读取失败";
            return cached;
        }
        Texture2D? tex = null;
        try
        {
            if (!File.Exists(path))
            {
                _problems[def.Id] = "文件不存在：" + path;
            }
            else
            {
                // 自己读文件、按文件头判断格式再解码：不依赖 Godot 对 Windows 路径的处理，
                // 也能读扩展名和实际格式不符的图片。
                var bytes = File.ReadAllBytes(path);
                var img = Decode(bytes);
                if (img == null)
                {
                    _problems[def.Id] = "无法解码：" + path;
                }
                else
                {
                    // 原图较大，缩到 400 高以节省显存。
                    if (img.GetHeight() > 400)
                        img.Resize(img.GetWidth() * 400 / img.GetHeight(), 400, Image.Interpolation.Lanczos);
                    tex = ImageTexture.CreateFromImage(img);
                }
            }
        }
        catch (Exception e)
        {
            _problems[def.Id] = $"读取出错（{e.Message}）：{path}";
        }
        if (tex == null) GD.PushWarning($"卡图 {def.Id}：{_problems.GetValueOrDefault(def.Id)}");
        _textures[path] = tex;
        return tex;
    }

    private static Image? Decode(byte[] b)
    {
        var img = new Image();
        bool png = b.Length > 4 && b[0] == 0x89 && b[1] == 0x50;
        bool webp = b.Length > 12 && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
        Func<Error>[] tries = png
            ? new Func<Error>[] { () => img.LoadPngFromBuffer(b), () => img.LoadJpgFromBuffer(b), () => img.LoadWebpFromBuffer(b) }
            : webp
                ? new Func<Error>[] { () => img.LoadWebpFromBuffer(b), () => img.LoadJpgFromBuffer(b), () => img.LoadPngFromBuffer(b) }
                : new Func<Error>[] { () => img.LoadJpgFromBuffer(b), () => img.LoadPngFromBuffer(b), () => img.LoadWebpFromBuffer(b) };
        foreach (var t in tries)
            if (t() == Error.Ok && img.GetWidth() > 0) return img;
        return null;
    }

    /// <summary>抽查一些有登记卡图的卡，看卡图文件在不在（菜单里显示结果）。</summary>
    public (int Checked, int Found, string? FirstMissing) CheckImages(int sample = 200)
    {
        int n = 0, found = 0;
        string? missing = null;
        foreach (var def in Db.All)
        {
            var p = ImagePath(def);
            if (p == null) continue;
            n++;
            if (File.Exists(p)) found++;
            else missing ??= p;
            if (n >= sample) break;
        }
        return (n, found, missing);
    }
}

using Godot;

namespace WsSim.Client;

/// <summary>
/// 界面素材：放在开发包的 <c>assets/</c> 文件夹（不进仓库，和卡图一样只在本机使用）。
/// 每个素材都是可选的，缺少时界面用代码画的版本代替。文件清单见 README。
/// </summary>
public static class Assets
{
    public const string CardBack = "ws-cardback.png";
    public const string Playmat = "playmat.png";
    public const string MenuBackground = "menu-bg.png";
    public const string PanelFrame = "panel-frame.png";
    public const string Emblem = "emblem.png";
    public const string TableEdge = "table-edge.png";

    private static readonly Dictionary<string, Texture2D?> Cache = new();

    /// <summary>素材文件夹（开发包目录/assets）。</summary>
    public static string Folder { get; set; } = "";

    public static Texture2D? Get(string name)
    {
        if (Cache.TryGetValue(name, out var t)) return t;
        Texture2D? tex = null;
        try
        {
            var path = Path.Combine(Folder, name);
            if (Folder != "" && File.Exists(path))
            {
                var img = new Image();
                var bytes = File.ReadAllBytes(path);
                var err = bytes.Length > 4 && bytes[0] == 0x89 ? img.LoadPngFromBuffer(bytes) : img.LoadJpgFromBuffer(bytes);
                if (err == Error.Ok)
                {
                    img.GenerateMipmaps();
                    tex = ImageTexture.CreateFromImage(img);
                }
                else GD.PushWarning($"素材读取失败：{path}");
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"素材 {name} 读取出错：{e.Message}");
        }
        Cache[name] = tex;
        return tex;
    }

    /// <summary>换了开发包目录后清掉缓存。</summary>
    public static void Reset(string folder)
    {
        Folder = folder;
        Cache.Clear();
    }
}

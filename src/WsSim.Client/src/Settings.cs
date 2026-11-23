using Godot;

namespace WsSim.Client;

/// <summary>保存在 user://settings.cfg 的设置。</summary>
public sealed class Settings
{
    private const string FilePath = "user://settings.cfg";

    /// <summary>ws-devkit 文件夹（里面有 data/cards.json）。</summary>
    public string DevkitPath { get; set; } = "";
    /// <summary>原模拟器卡图目录（…/Weiss Schwarz_Data/StreamingAssets/Cards）。</summary>
    public string CardImagePath { get; set; } = "";
    public string PlayerDeck { get; set; } = "";
    public string OpponentDeck { get; set; } = "";
    public int AiDelayMs { get; set; } = 600;

    public static Settings Load()
    {
        var s = new Settings();
        var cfg = new ConfigFile();
        if (cfg.Load(FilePath) == Error.Ok)
        {
            s.DevkitPath = (string)cfg.GetValue("paths", "devkit", "");
            s.CardImagePath = (string)cfg.GetValue("paths", "card_images", "");
            s.PlayerDeck = (string)cfg.GetValue("game", "player_deck", "");
            s.OpponentDeck = (string)cfg.GetValue("game", "opponent_deck", "");
            s.AiDelayMs = (int)cfg.GetValue("game", "ai_delay_ms", 600);
        }
        if (s.DevkitPath == "") s.DevkitPath = GuessDevkit() ?? "";
        if (s.CardImagePath == "") s.CardImagePath = GuessCardImages() ?? "";
        return s;
    }

    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("paths", "devkit", DevkitPath);
        cfg.SetValue("paths", "card_images", CardImagePath);
        cfg.SetValue("game", "player_deck", PlayerDeck);
        cfg.SetValue("game", "opponent_deck", OpponentDeck);
        cfg.SetValue("game", "ai_delay_ms", AiDelayMs);
        cfg.Save(FilePath);
    }

    /// <summary>环境变量 WS_DEVKIT，或从程序 / 项目目录往上找 ws-devkit/。</summary>
    private static string? GuessDevkit()
    {
        var env = System.Environment.GetEnvironmentVariable("WS_DEVKIT");
        if (!string.IsNullOrEmpty(env) && CardLibrary.IsDevkit(env)) return env;
        foreach (var start in new[] { ProjectSettings.GlobalizePath("res://"), OS.GetExecutablePath() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var p = Path.Combine(dir.FullName, "ws-devkit");
                if (CardLibrary.IsDevkit(p)) return p;
                dir = dir.Parent;
            }
        }
        return null;
    }

    private static string? GuessCardImages()
    {
        foreach (var root in new[] { "D:/Weiss Schwarz 0.6.5.3 Windows", "C:/Weiss Schwarz 0.6.5.3 Windows" })
        {
            var p = Path.Combine(root, "Weiss Schwarz_Data", "StreamingAssets", "Cards");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}

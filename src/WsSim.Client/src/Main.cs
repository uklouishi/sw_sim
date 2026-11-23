using Godot;
using WsSim.Core.Cards;

namespace WsSim.Client;

/// <summary>开一局需要的东西。</summary>
public sealed record GameConfig(Deck PlayerDeck, Deck OpponentDeck, bool Spectate, ulong Seed, int AiDelayMs);

/// <summary>
/// 根节点：持有设置和卡片库，在菜单和对局界面之间切换。
/// <para>
/// 命令行（Godot 的 <c>--</c> 之后）：<c>--auto</c> 直接开一局 AI 对 AI，<c>--play</c> 直接开一局人机，
/// <c>--seed N</c>，<c>--selftest N</c> 模拟点击做前 N 个选择，<c>--shot 文件.png</c> 在 <c>--shot-delay 秒</c> 后截图并退出。用于自动检查。
/// </para>
/// </summary>
public partial class Main : Control
{
    public Settings Settings { get; private set; } = new();
    public CardLibrary? Library { get; private set; }
    /// <summary>自检：由程序模拟点击，代替玩家做前 N 个选择（命令行 --selftest N）。</summary>
    public int SelfTestSteps { get; private set; }
    /// <summary>命令行指定了自检或动画速度（这时不把动画速度存进设置）。</summary>
    public bool SelfTestOrShot => SelfTestSteps > 0 || AnimFromArgs;
    public bool AnimFromArgs => _args.ContainsKey("anim");

    private Node? _screen;
    private readonly Dictionary<string, string> _args = new();

    public override void _Ready()
    {
        Theme = UiTheme.Build();
        var bg = new ColorRect { Color = UiTheme.Background, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var raw = OS.GetCmdlineUserArgs();
        for (int i = 0; i < raw.Length; i++)
        {
            if (!raw[i].StartsWith("--")) continue;
            bool hasValue = i + 1 < raw.Length && !raw[i + 1].StartsWith("--");
            _args[raw[i][2..]] = hasValue ? raw[++i] : "";
        }

        Settings = Settings.Load();
        if (_args.TryGetValue("devkit", out var dk)) Settings.DevkitPath = dk;
        if (_args.TryGetValue("images", out var im)) Settings.CardImagePath = im;
        if (_args.TryGetValue("ai-delay", out var ad) && int.TryParse(ad, out var adMs)) Settings.AiDelayMs = adMs;
        if (_args.TryGetValue("anim", out var an) && int.TryParse(an, out var anv)) Settings.AnimSpeed = Math.Clamp(anv, 0, 3);
        Assets.Reset(Path.Combine(Settings.DevkitPath, "assets"));
        if (_args.TryGetValue("selftest", out var st)) SelfTestSteps = int.TryParse(st, out var k) ? k : int.MaxValue;

        var menu = new MenuScreen(this);
        ShowScreen(menu);

        if (_args.ContainsKey("shot"))
        {
            float delay = _args.TryGetValue("shot-delay", out var d) && float.TryParse(d, out var f) ? f : 3f;
            if (_args.TryGetValue("shot-count", out var sc) && int.TryParse(sc, out var count) && count > 1)
            {
                // 连拍（检查动画用）：从 delay 秒开始，每隔 --shot-every 秒拍一张，文件名加序号。
                float every = _args.TryGetValue("shot-every", out var se) && float.TryParse(se, out var e) ? e : 0.1f;
                for (int i = 0; i < count; i++)
                {
                    int k = i;
                    GetTree().CreateTimer(delay + every * i).Timeout += () => TakeShot(k, k == count - 1);
                }
            }
            else GetTree().CreateTimer(delay).Timeout += TakeShotAndQuit;
        }
        if (_args.ContainsKey("auto") || _args.ContainsKey("play"))
            menu.AutoStart(spectate: _args.ContainsKey("auto"),
                seed: _args.TryGetValue("seed", out var s) && ulong.TryParse(s, out var n) ? n : null);
    }

    /// <summary>卡片库读好后由菜单设置。</summary>
    public void SetLibrary(CardLibrary lib)
    {
        Library = lib;
        CardControl.Library = lib;
    }

    public void ShowScreen(Control screen)
    {
        _screen?.QueueFree();
        _screen = screen;
        screen.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(screen);
    }

    public void StartGame(GameConfig config) => ShowScreen(new GameScreen(this, config));

    public void BackToMenu() => ShowScreen(new MenuScreen(this));

    private void TakeShot(int k, bool quit)
    {
        var path = _args["shot"].Replace(".png", $"_{k:000}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        if (quit) GetTree().Quit();
    }

    private void TakeShotAndQuit()
    {
        var path = _args["shot"];
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(path);
        GD.Print($"screenshot saved: {path}");
        GetTree().Quit();
    }
}

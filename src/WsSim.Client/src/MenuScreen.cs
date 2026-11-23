using Godot;
using WsSim.Core.Cards;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>
/// 开始菜单：左边标题和主按钮（开始对局 / 观战 / 关于 / 退出），右边卡组选择（带封面和构成）、
/// 对局设置，目录设置收在下面。背景用素材 menu-bg.png，没有时画渐变和斜放的卡背。
/// </summary>
public partial class MenuScreen : Control
{
    private readonly Main _main;
    private LineEdit _devkit = null!;
    private LineEdit _images = null!;
    private Label _status = null!;
    private DeckPicker _playerDeck = null!;
    private DeckPicker _opponentDeck = null!;
    private HSlider _delay = null!;
    private Label _delayLabel = null!;
    private OptionButton _anim = null!;
    private Button _start = null!;
    private Button _watch = null!;
    private VBoxContainer _pathsBox = null!;
    private MenuBackground _background = null!;
    private Task<CardLibrary>? _loading;
    private SortedDictionary<string, string> _decks = new();
    private (bool Spectate, ulong? Seed)? _autoStart;

    public MenuScreen(Main main)
    {
        _main = main;
    }

    public override void _Ready()
    {
        _background = new MenuBackground { MouseFilter = MouseFilterEnum.Ignore };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_background);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var (side, v) in new[] { ("left", 80), ("right", 60), ("top", 60), ("bottom", 50) })
            margin.AddThemeConstantOverride("margin_" + side, v);
        AddChild(margin);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 60);
        margin.AddChild(row);

        // ---------------------------------------------------------------- 左：标题和主按钮
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        left.AddThemeConstantOverride("separation", 14);
        row.AddChild(left);
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 14);
        if (Assets.Get(Assets.Emblem) is { } emblem)
            titleRow.AddChild(new TextureRect { Texture = emblem, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(84, 84) });
        var titleCol = new VBoxContainer();
        titleCol.AddThemeConstantOverride("separation", 0);
        var title = UiTheme.Label("WS Sim", 64, new Color("#f4f5f8"));
        title.AddThemeColorOverride("font_shadow_color", new Color(0.3f, 0.45f, 1f, 0.6f));
        title.AddThemeConstantOverride("shadow_offset_x", 0);
        title.AddThemeConstantOverride("shadow_offset_y", 3);
        title.AddThemeConstantOverride("shadow_outline_size", 10);
        titleCol.AddChild(title);
        titleCol.AddChild(UiTheme.Label("Weiss Schwarz 对战模拟器", 18, new Color("#c9ccd6")));
        titleRow.AddChild(titleCol);
        left.AddChild(titleRow);
        left.AddChild(new Control { CustomMinimumSize = new Vector2(0, 40) });

        _start = MainButton("开始对局", "和电脑对战", primary: true);
        _start.Pressed += () => Start(false, null);
        left.AddChild(_start);
        _watch = MainButton("观战", "双方都由电脑操作");
        _watch.Pressed += () => Start(true, null);
        left.AddChild(_watch);
        var about = MainButton("关于", "版权与致谢");
        about.Pressed += ShowAbout;
        left.AddChild(about);
        var quit = MainButton("退出", "");
        quit.Pressed += () => GetTree().Quit();
        left.AddChild(quit);

        left.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _status = UiTheme.Label("", 13, UiTheme.TextDim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        left.AddChild(_status);

        // ---------------------------------------------------------------- 右：卡组和设置
        var right = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.07f, 0.08f, 0.11f, 0.72f), 14, 1, new Color(1, 1, 1, 0.12f), 26, 22));
        row.AddChild(right);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        right.AddChild(scroll);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 14);
        scroll.AddChild(col);

        col.AddChild(Section("卡组"));
        var decks = new HBoxContainer();
        decks.AddThemeConstantOverride("separation", 18);
        _playerDeck = new DeckPicker("我的卡组", UiTheme.Selected, OnDeckChanged);
        _opponentDeck = new DeckPicker("对手卡组", UiTheme.Opponent, OnDeckChanged);
        decks.AddChild(_playerDeck);
        decks.AddChild(_opponentDeck);
        col.AddChild(decks);

        col.AddChild(Section("对局设置"));
        var g2 = new GridContainer { Columns = 2 };
        g2.AddThemeConstantOverride("h_separation", 16);
        g2.AddThemeConstantOverride("v_separation", 10);
        col.AddChild(g2);
        g2.AddChild(UiTheme.Label("对手操作间隔", 15, UiTheme.TextDim));
        var delayRow = new HBoxContainer();
        _delay = new HSlider { MinValue = 0, MaxValue = 2000, Step = 100, Value = _main.Settings.AiDelayMs, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 0) };
        _delayLabel = UiTheme.Label("", 14, UiTheme.Text);
        _delay.ValueChanged += v => _delayLabel.Text = $"{v / 1000:0.0} 秒";
        _delayLabel.Text = $"{_delay.Value / 1000:0.0} 秒";
        delayRow.AddChild(_delay);
        delayRow.AddChild(_delayLabel);
        g2.AddChild(delayRow);
        g2.AddChild(UiTheme.Label("动画速度", 15, UiTheme.TextDim));
        _anim = new OptionButton();
        foreach (var n in Settings.AnimSpeedNames) _anim.AddItem(n);
        _anim.Select(_main.Settings.AnimSpeed);
        g2.AddChild(_anim);

        // 目录设置（默认收起；没找到开发包时展开）
        var pathsHeader = new Button { Text = "目录设置 ▸", Flat = true, Alignment = HorizontalAlignment.Left };
        pathsHeader.AddThemeColorOverride("font_color", UiTheme.Accent);
        col.AddChild(pathsHeader);
        _pathsBox = new VBoxContainer { Visible = false };
        _pathsBox.AddThemeConstantOverride("separation", 8);
        pathsHeader.Pressed += () =>
        {
            _pathsBox.Visible = !_pathsBox.Visible;
            pathsHeader.Text = _pathsBox.Visible ? "目录设置 ▾" : "目录设置 ▸";
        };
        col.AddChild(_pathsBox);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        _pathsBox.AddChild(grid);
        _devkit = PathRow(grid, "开发包", _main.Settings.DevkitPath, "ws-devkit 文件夹（里面有 data/cards.json，界面素材在 assets/）");
        _images = PathRow(grid, "卡图", _main.Settings.CardImagePath, "原模拟器 …/Weiss Schwarz_Data/StreamingAssets/Cards");
        var reload = new Button { Text = "重新载入卡片", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        reload.Pressed += BeginLoad;
        _pathsBox.AddChild(reload);

        SetReady(false);
        if (_main.Library != null && _main.Library.DevkitPath == _devkit.Text) OnLoaded(_main.Library);
        else BeginLoad();
    }

    private static Control Section(string text)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        box.AddChild(UiTheme.Label(text, 18, UiTheme.Accent));
        var line = new ColorRect { Color = new Color(1, 1, 1, 0.1f), CustomMinimumSize = new Vector2(0, 1) };
        box.AddChild(line);
        return box;
    }

    private static Button MainButton(string text, string sub, bool primary = false)
    {
        var b = new Button
        {
            Text = sub == "" ? text : $"{text}　　{sub}",
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(380, primary ? 64 : 50),
        };
        b.AddThemeFontSizeOverride("font_size", primary ? 22 : 18);
        var normal = primary ? new Color(0.85f, 0.65f, 0.2f, 0.9f) : new Color(1, 1, 1, 0.06f);
        var border = primary ? UiTheme.Accent : new Color(1, 1, 1, 0.18f);
        b.AddThemeStyleboxOverride("normal", UiTheme.Box(normal, 8, 1, border, 22, 8));
        b.AddThemeStyleboxOverride("hover", UiTheme.Box(normal.Lightened(0.15f), 8, 2, UiTheme.Accent, 26, 8));
        b.AddThemeStyleboxOverride("pressed", UiTheme.Box(normal.Darkened(0.2f), 8, 2, UiTheme.Accent, 26, 8));
        b.AddThemeStyleboxOverride("disabled", UiTheme.Box(new Color(1, 1, 1, 0.03f), 8, 1, new Color(1, 1, 1, 0.08f), 22, 8));
        if (primary) b.AddThemeColorOverride("font_color", new Color("#1b1f27"));
        if (primary) b.AddThemeColorOverride("font_hover_color", new Color("#1b1f27"));
        return b;
    }

    private void SetReady(bool ready)
    {
        _start.Disabled = !ready;
        _watch.Disabled = !ready;
    }

    /// <summary>命令行自动开局：卡片读好后立即开始。</summary>
    public void AutoStart(bool spectate, ulong? seed) => _autoStart = (spectate, seed);

    private LineEdit PathRow(GridContainer grid, string label, string value, string placeholder)
    {
        grid.AddChild(UiTheme.Label(label, 15, UiTheme.TextDim));
        var edit = new LineEdit { Text = value, PlaceholderText = placeholder, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(420, 0) };
        grid.AddChild(edit);
        var browse = new Button { Text = "浏览…" };
        browse.Pressed += () =>
        {
            var dlg = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true,
                Title = label,
            };
            if (Directory.Exists(edit.Text)) dlg.CurrentDir = edit.Text;
            dlg.DirSelected += dir =>
            {
                edit.Text = dir;
                BeginLoad();
            };
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(900, 600));
        };
        grid.AddChild(browse);
        return edit;
    }

    private void BeginLoad()
    {
        SetReady(false);
        var devkit = _devkit.Text.Trim();
        var images = _images.Text.Trim();
        if (!CardLibrary.IsDevkit(devkit))
        {
            _status.Text = "找不到 data/cards.json，请在“目录设置”里选择 ws-devkit 文件夹。";
            _pathsBox.Visible = true;
            return;
        }
        _status.Text = "正在载入卡片数据…";
        _loading = Task.Run(() => CardLibrary.Load(devkit, images));
    }

    public override void _Process(double delta)
    {
        if (_loading is not { IsCompleted: true } t) return;
        _loading = null;
        if (t.IsFaulted)
        {
            _status.Text = "载入失败：" + t.Exception!.GetBaseException().Message;
            _pathsBox.Visible = true;
            return;
        }
        _main.SetLibrary(t.Result);
        OnLoaded(t.Result);
    }

    private void OnLoaded(CardLibrary lib)
    {
        lib.ImageRoot = _images.Text.Trim();
        _main.Settings.DevkitPath = _devkit.Text.Trim();
        Assets.Reset(Path.Combine(_main.Settings.DevkitPath, "assets"));
        _main.Settings.CardImagePath = lib.ImageRoot;
        _main.Settings.Save();

        _decks = lib.FindDecks();
        string imageStatus;
        if (!Directory.Exists(lib.ImageRoot))
            imageStatus = "没有找到卡图目录，将用文字卡面显示。";
        else
        {
            var (n, found, missing) = lib.CheckImages();
            imageStatus = n == 0 ? "开发包的 cards.json 里没有卡图路径（image 全空），请在有卡图的电脑上重新生成开发包。"
                : found == n ? "卡图正常。"
                : $"卡图抽查 {n} 张只找到 {found} 张，例如找不到 {missing}";
        }
        string assets = Assets.Get(Assets.CardBack) == null ? "　开发包 assets/ 里没有 ws-cardback.png，卡背用简易版。" : "";
        _status.Text = $"已载入 {lib.Db.Count:N0} 张卡、{_decks.Count} 副卡组。{imageStatus}{assets}";
        _playerDeck.Fill(_decks, _main.Settings.PlayerDeck, "TEST_SAO");
        _opponentDeck.Fill(_decks, _main.Settings.OpponentDeck, "TEST_NIKKE");
        OnDeckChanged();
        _background.QueueRedraw();

        if (_autoStart is { } a && _decks.Count > 0)
        {
            _autoStart = null;
            Start(a.Spectate, a.Seed);
        }
    }

    private void OnDeckChanged()
    {
        var lib = _main.Library;
        bool ok = lib != null;
        foreach (var p in new[] { _playerDeck, _opponentDeck })
        {
            if (lib == null || p.SelectedName is not { } name || !_decks.TryGetValue(name, out var path))
            {
                ok = false;
                continue;
            }
            try
            {
                p.Display(lib.LoadDeck(path), null);
            }
            catch (Exception e)
            {
                p.Display(null, e.Message);
                ok = false;
            }
        }
        SetReady(ok);
    }

    private void Start(bool spectate, ulong? seed)
    {
        var lib = _main.Library;
        if (lib == null || _playerDeck.SelectedName is not { } pName || _opponentDeck.SelectedName is not { } oName) return;
        try
        {
            var p = lib.LoadDeck(_decks[pName]);
            var o = lib.LoadDeck(_decks[oName]);
            _main.Settings.PlayerDeck = pName;
            _main.Settings.OpponentDeck = oName;
            _main.Settings.AiDelayMs = (int)_delay.Value;
            if (!_main.SelfTestOrShot) _main.Settings.AnimSpeed = _anim.Selected;
            _main.Settings.Save();
            _main.StartGame(new GameConfig(p, o, spectate, seed ?? (ulong)System.Random.Shared.NextInt64(), (int)_delay.Value));
        }
        catch (Exception e)
        {
            _status.Text = e.Message;
        }
    }

    private void ShowAbout()
    {
        var lib = _main.Library;
        var credits = lib != null && File.Exists(Path.Combine(lib.DevkitPath, "CREDITS.md"))
            ? File.ReadAllText(Path.Combine(lib.DevkitPath, "CREDITS.md"))
            : "请先载入开发包（CREDITS.md 在开发包根目录）。";
        var dlg = new AcceptDialog { Title = "关于 WS Sim", OkButtonText = "关闭" };
        var text = new RichTextLabel { Text = credits, CustomMinimumSize = new Vector2(700, 460), SelectionEnabled = true };
        dlg.AddChild(text);
        AddChild(dlg);
        dlg.PopupCentered();
    }
}

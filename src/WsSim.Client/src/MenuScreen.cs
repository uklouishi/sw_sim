using Godot;

namespace WsSim.Client;

/// <summary>开始菜单：路径设置、选卡组、开局。</summary>
public partial class MenuScreen : Control
{
    private readonly Main _main;
    private LineEdit _devkit = null!;
    private LineEdit _images = null!;
    private Label _status = null!;
    private OptionButton _playerDeck = null!;
    private OptionButton _opponentDeck = null!;
    private HSlider _delay = null!;
    private Label _delayLabel = null!;
    private CheckBox _spectate = null!;
    private Button _start = null!;
    private Task<CardLibrary>? _loading;
    private SortedDictionary<string, string> _decks = new();
    private (bool Spectate, ulong? Seed)? _autoStart;

    public MenuScreen(Main main)
    {
        _main = main;
    }

    public override void _Ready()
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(780, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 10, 0, default, 28, 24));
        center.AddChild(panel);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        panel.AddChild(col);

        col.AddChild(UiTheme.Label("WS Sim", 34, UiTheme.Accent));
        col.AddChild(UiTheme.Label("Weiss Schwarz 对战模拟器 · 人机对战（P1 规则引擎，暂不含卡片效果）", 14, UiTheme.TextDim));
        col.AddChild(new HSeparator());

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        col.AddChild(grid);

        _devkit = PathRow(grid, "开发包目录", _main.Settings.DevkitPath, "ws-devkit 文件夹（里面有 data/cards.json）");
        _images = PathRow(grid, "卡图目录", _main.Settings.CardImagePath, "原模拟器 …/Weiss Schwarz_Data/StreamingAssets/Cards");

        var reloadRow = new HBoxContainer();
        var reload = new Button { Text = "重新载入卡片" };
        reload.Pressed += BeginLoad;
        reloadRow.AddChild(reload);
        _status = UiTheme.Label("", 14, UiTheme.TextDim);
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        reloadRow.AddChild(_status);
        col.AddChild(reloadRow);
        col.AddChild(new HSeparator());

        var g2 = new GridContainer { Columns = 2 };
        g2.AddThemeConstantOverride("h_separation", 10);
        g2.AddThemeConstantOverride("v_separation", 10);
        col.AddChild(g2);
        g2.AddChild(UiTheme.Label("我的卡组"));
        _playerDeck = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        g2.AddChild(_playerDeck);
        g2.AddChild(UiTheme.Label("对手卡组"));
        _opponentDeck = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        g2.AddChild(_opponentDeck);
        g2.AddChild(UiTheme.Label("对手操作间隔"));
        var delayRow = new HBoxContainer();
        _delay = new HSlider { MinValue = 0, MaxValue = 2000, Step = 100, Value = _main.Settings.AiDelayMs, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 0) };
        _delayLabel = UiTheme.Label("", 14, UiTheme.TextDim);
        _delay.ValueChanged += v => _delayLabel.Text = $"{v / 1000:0.0} 秒";
        _delayLabel.Text = $"{_delay.Value / 1000:0.0} 秒";
        delayRow.AddChild(_delay);
        delayRow.AddChild(_delayLabel);
        g2.AddChild(delayRow);

        _spectate = new CheckBox { Text = "观战：双方都由 AI 操作" };
        col.AddChild(_spectate);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 10);
        var about = new Button { Text = "关于" };
        about.Pressed += ShowAbout;
        buttons.AddChild(about);
        var quit = new Button { Text = "退出" };
        quit.Pressed += () => GetTree().Quit();
        buttons.AddChild(quit);
        _start = new Button { Text = "开始对局", Disabled = true };
        _start.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.Accent.Darkened(0.35f), 4, 0, default, 18, 6));
        _start.Pressed += () => Start(_spectate.ButtonPressed, null);
        buttons.AddChild(_start);
        col.AddChild(buttons);

        if (_main.Library != null && _main.Library.DevkitPath == _devkit.Text) OnLoaded(_main.Library);
        else BeginLoad();
    }

    /// <summary>命令行自动开局：卡片读好后立即开始。</summary>
    public void AutoStart(bool spectate, ulong? seed) => _autoStart = (spectate, seed);

    private LineEdit PathRow(GridContainer grid, string label, string value, string placeholder)
    {
        grid.AddChild(UiTheme.Label(label));
        var edit = new LineEdit { Text = value, PlaceholderText = placeholder, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(480, 0) };
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
        _start.Disabled = true;
        var devkit = _devkit.Text.Trim();
        var images = _images.Text.Trim();
        if (!CardLibrary.IsDevkit(devkit))
        {
            _status.Text = "找不到 data/cards.json，请选择 ws-devkit 文件夹。";
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
            return;
        }
        _main.SetLibrary(t.Result);
        OnLoaded(t.Result);
    }

    private void OnLoaded(CardLibrary lib)
    {
        lib.ImageRoot = _images.Text.Trim();
        _main.Settings.DevkitPath = _devkit.Text.Trim();
        _main.Settings.CardImagePath = lib.ImageRoot;
        _main.Settings.Save();

        bool hasImages = Directory.Exists(lib.ImageRoot);
        _decks = lib.FindDecks();
        _status.Text = $"已载入 {lib.Db.Count:N0} 张卡、{_decks.Count} 副卡组。" +
                       (hasImages ? "" : "没有找到卡图目录，将用文字卡面显示。");
        Fill(_playerDeck, _main.Settings.PlayerDeck, "TEST_SAO");
        Fill(_opponentDeck, _main.Settings.OpponentDeck, "TEST_NIKKE");
        _start.Disabled = _decks.Count == 0;

        if (_autoStart is { } a && _decks.Count > 0)
        {
            _autoStart = null;
            Start(a.Spectate, a.Seed);
        }
    }

    private void Fill(OptionButton ob, string saved, string fallback)
    {
        ob.Clear();
        int select = 0, i = 0;
        foreach (var name in _decks.Keys)
        {
            ob.AddItem(name);
            if (name == saved || (saved == "" && name == fallback)) select = i;
            i++;
        }
        if (_decks.Count > 0) ob.Select(select);
    }

    private void Start(bool spectate, ulong? seed)
    {
        var lib = _main.Library;
        if (lib == null || _playerDeck.Selected < 0 || _opponentDeck.Selected < 0) return;
        var pName = _playerDeck.GetItemText(_playerDeck.Selected);
        var oName = _opponentDeck.GetItemText(_opponentDeck.Selected);
        try
        {
            var p = lib.LoadDeck(_decks[pName]);
            var o = lib.LoadDeck(_decks[oName]);
            _main.Settings.PlayerDeck = pName;
            _main.Settings.OpponentDeck = oName;
            _main.Settings.AiDelayMs = (int)_delay.Value;
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

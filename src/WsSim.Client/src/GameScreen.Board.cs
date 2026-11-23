using Godot;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>对局界面：版面搭建和按快照重画场地。</summary>
public partial class GameScreen
{
    private Control _boardHost = null!;
    private Control _boardRoot = null!;
    private Control _cardsLayer = null!;
    private Control _fxLayer = null!;
    private Label _phaseLabel = null!;
    private Label _turnLabel = null!;
    private PanelContainer _phasePanel = null!;
    private OptionButton _animSpeed = null!;

    /// <summary>界面上每张卡的位置，键是（玩家、区域、序号）；一叠卡（牌库、休息室、思出、名场面）序号为 -1。</summary>
    private readonly Dictionary<(int Owner, ZoneType Zone, int Index), CardControl> _byKey = new();
    private readonly Dictionary<int, CardControl> _byId = new();

    private void BuildLayout()
    {
        var root = new HBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        // 左：场地（固定设计尺寸，按窗口缩放居中）
        _boardHost = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        _boardHost.Resized += FitBoard;
        root.AddChild(_boardHost);
        var bg = new ColorRect { Color = new Color("#0b0c10"), MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        _boardHost.AddChild(bg);
        _boardRoot = new Control { Size = BoardLayout.Size, MouseFilter = MouseFilterEnum.Ignore };
        _boardHost.AddChild(_boardRoot);
        var mat = new PlaymatControl { Size = BoardLayout.Size };
        _boardRoot.AddChild(mat);
        _cardsLayer = new Control { Size = BoardLayout.Size, MouseFilter = MouseFilterEnum.Ignore };
        _boardRoot.AddChild(_cardsLayer);
        _fxLayer = new Control { Size = BoardLayout.Size, MouseFilter = MouseFilterEnum.Ignore };
        _boardRoot.AddChild(_fxLayer);

        // 中线左侧：回合 / 阶段
        _phasePanel = new PanelContainer { Position = new Vector2(18, BoardLayout.CenterY - 22), MouseFilter = MouseFilterEnum.Ignore };
        _phasePanel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0, 0, 0, 0.55f), 14, 1, new Color(1, 1, 1, 0.15f), 14, 4));
        var phaseRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        phaseRow.AddThemeConstantOverride("separation", 10);
        _turnLabel = UiTheme.Label("", 14, UiTheme.Text);
        _phaseLabel = UiTheme.Label("", 14, UiTheme.Accent);
        phaseRow.AddChild(_turnLabel);
        phaseRow.AddChild(_phaseLabel);
        _phasePanel.AddChild(phaseRow);
        _boardRoot.AddChild(_phasePanel);

        BuildSidePanel(root);
        CallDeferred(MethodName.FitBoard);
    }

    private void FitBoard()
    {
        var avail = _boardHost.Size;
        if (avail.X <= 0 || avail.Y <= 0) return;
        float s = Math.Min(avail.X / BoardLayout.Size.X, avail.Y / BoardLayout.Size.Y);
        _boardRoot.Scale = new Vector2(s, s);
        _boardRoot.Position = (avail - BoardLayout.Size * s) / 2;
    }

    private void BuildSidePanel(HBoxContainer root)
    {
        var side = new PanelContainer { CustomMinimumSize = new Vector2(400, 0) };
        side.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 0, 0, default, 12, 12));
        root.AddChild(side);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        side.AddChild(col);

        var detailRow = new HBoxContainer();
        detailRow.AddThemeConstantOverride("separation", 10);
        _detailCard = CardControl.Create(null, new Vector2(150, 210));
        _detailCard.MouseFilter = MouseFilterEnum.Ignore;
        detailRow.AddChild(_detailCard);
        var detailInfoCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _detailName = UiTheme.Label("把鼠标移到卡上查看详情", 16, UiTheme.Accent);
        _detailName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detailInfo = UiTheme.Label("", 13, UiTheme.TextDim);
        _detailInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        detailInfoCol.AddChild(_detailName);
        detailInfoCol.AddChild(_detailInfo);
        detailRow.AddChild(detailInfoCol);
        col.AddChild(detailRow);
        _detailText = new RichTextLabel { CustomMinimumSize = new Vector2(0, 110), BbcodeEnabled = false, SelectionEnabled = true };
        _detailText.AddThemeFontSizeOverride("normal_font_size", 13);
        col.AddChild(_detailText);

        var decisionPanel = new PanelContainer();
        decisionPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelLight, 8, 1, UiTheme.Accent.Darkened(0.5f), 10, 8));
        col.AddChild(decisionPanel);
        var dcol = new VBoxContainer();
        decisionPanel.AddChild(dcol);
        _prompt = UiTheme.Label("", 15, UiTheme.Text);
        _prompt.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        dcol.AddChild(_prompt);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 190), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _options = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_options);
        dcol.AddChild(scroll);

        _log = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        _log.AddThemeFontSizeOverride("normal_font_size", 13);
        col.AddChild(_log);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 8);
        bottom.AddChild(UiTheme.Label("动画", 13, UiTheme.TextDim));
        _animSpeed = new OptionButton();
        foreach (var n in Settings.AnimSpeedNames) _animSpeed.AddItem(n);
        _animSpeed.Select(_main.Settings.AnimSpeed);
        _animSpeed.ItemSelected += i =>
        {
            _main.Settings.AnimSpeed = (int)i;
            _main.Settings.Save();
        };
        bottom.AddChild(_animSpeed);
        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var back = new Button { Text = "返回菜单" };
        back.Pressed += () => _main.BackToMenu();
        bottom.AddChild(back);
        col.AddChild(bottom);
    }

    // ================================================================ 重画

    private void Rebuild()
    {
        if (_view == null) return;
        _controls.RemoveAll(c => !IsInstanceValid(c) || c.GetParent() == _cardsLayer);
        _byKey.Clear();
        _byId.Clear();
        foreach (var child in _cardsLayer.GetChildren())
        {
            _cardsLayer.RemoveChild(child);
            child.QueueFree();
        }

        var v = _view;
        foreach (var p in v.Players)
            BuildSide(p, p.Index == Me);

        bool myTurn = v.ActivePlayer == Me;
        _turnLabel.Text = v.TurnNumber > 0
            ? $"第 {v.TurnNumber} 回合　{(_config.Spectate ? $"P{v.ActivePlayer}" : myTurn ? "我的回合" : "对手回合")}"
            : "";
        _turnLabel.AddThemeColorOverride("font_color", myTurn ? UiTheme.Selected : UiTheme.Opponent);
        _turnLabel.Visible = _turnLabel.Text != "";
        _phaseLabel.Text = Texts.Phase(v.Phase) + (v.Phase == Phase.Attack ? $"　已攻击 {v.AttacksThisTurn} 次" : "");

        RefreshHighlights();
        BuildDecisionPanel();
        ShowOverlayIfOver();
    }

    private void BuildSide(PlayerView p, bool bottom)
    {
        int o = p.Index;

        // 舞台
        foreach (var s in StageSlots.All)
        {
            var c = Place(p.Stage[(int)s], BoardLayout.Stage(s, bottom), BoardLayout.StageCard, o, ZoneType.Stage, (int)s);
            c.Slot = s;
            c.ShowStats = true;
        }

        // 名场面区（横放）
        var cx = Place(p.ClimaxArea.LastOrDefault(), BoardLayout.Climax(bottom), BoardLayout.StageCard, o, ZoneType.ClimaxArea, -1);
        cx.Angle = Mathf.Pi / 2;

        // 能量区（背面、横放竖叠）
        for (int i = 0; i < p.Stock.Count; i++)
        {
            var c = Place(p.Stock[i], BoardLayout.StockCard(i, p.Stock.Count, bottom), BoardLayout.SmallCard, o, ZoneType.Stock, i);
            c.Angle = Mathf.Pi / 2;
            c.FaceDown = p.Stock[i].IsHidden;
            c.TooltipText = $"能量 {p.Stock.Count}";
        }
        if (p.Stock.Count > 0)
            _byKey[(o, ZoneType.Stock, -1)] = _byKey[(o, ZoneType.Stock, p.Stock.Count - 1)];

        // 等级区（正面、横放）
        for (int i = 0; i < p.Level.Count; i++)
            Place(p.Level[i], BoardLayout.LevelCard(i, bottom), BoardLayout.SmallCard, o, ZoneType.Level, i).Angle = Mathf.Pi / 2;

        // 伤害区
        for (int i = 0; i < p.Clock.Count; i++)
            Place(p.Clock[i], BoardLayout.ClockCard(Math.Min(i, 6), bottom), BoardLayout.SmallCard, o, ZoneType.Clock, i);

        // 牌库
        var deck = Place(null, BoardLayout.Deck(bottom), BoardLayout.PileCard, o, ZoneType.Deck, -1);
        deck.FaceDown = p.DeckCount > 0;
        deck.StackDepth = p.DeckCount / 8;
        deck.Badge = p.DeckCount.ToString();
        deck.TooltipText = $"牌库 {p.DeckCount} 张";

        // 休息室、思出区（正面一叠，点击查看）
        Pile(p.WaitingRoom, BoardLayout.WaitingRoom(bottom), BoardLayout.PileCard, o, ZoneType.WaitingRoom, "休息室");
        Pile(p.Memory, BoardLayout.Memory(bottom), BoardLayout.SmallCard, o, ZoneType.Memory, "思出区");

        // 判定区（结算中的卡）
        for (int i = 0; i < p.Resolution.Count; i++)
            Place(p.Resolution[i], BoardLayout.ResolutionCard(i, p.Resolution.Count, bottom), BoardLayout.PileCard, o, ZoneType.Resolution, i);

        // 手牌
        for (int i = 0; i < p.Hand.Count; i++)
        {
            var r = bottom ? BoardLayout.MyHandCard(i, p.Hand.Count) : BoardLayout.OppHandCard(i, p.Hand.Count);
            var c = Place(p.Hand[i], r, r.Size, o, ZoneType.Hand, i);
            if (bottom)
            {
                var baseY = r.Position.Y;
                c.Hovered += x => LiftHand(x, baseY - 22);
                c.Unhovered += x => LiftHand(x, baseY);
            }
        }
        _byKey.TryAdd((o, ZoneType.Hand, -1), PlaceAnchor(BoardLayout.HandArea(bottom), o, ZoneType.Hand));

        BuildInfo(p, bottom);
    }

    private void LiftHand(CardControl c, float y)
    {
        if (!IsInstanceValid(c)) return;
        var t = c.CreateTween();
        t.TweenProperty(c, "position:y", y, 0.12f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        c.ZIndex = y < c.Position.Y ? 5 : 0;
    }

    /// <summary>玩家信息牌：名字、等级、伤害、手牌、能量。</summary>
    private void BuildInfo(PlayerView p, bool bottom)
    {
        var r = BoardLayout.Info(bottom);
        var panel = new PanelContainer { Position = r.Position, Size = r.Size, MouseFilter = MouseFilterEnum.Ignore };
        var accent = bottom ? UiTheme.Selected : UiTheme.Opponent;
        bool active = _view!.ActivePlayer == p.Index && _view.TurnNumber > 0;
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0, 0, 0, 0.5f), 8, active ? 2 : 1, active ? accent : new Color(1, 1, 1, 0.12f), 10, 6));
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 2);
        string who = _config.Spectate ? $"P{p.Index}" : bottom ? "我" : "对手";
        col.AddChild(UiTheme.Label($"{who}　{p.DeckName}", 13, accent));
        col.AddChild(UiTheme.Label($"等级 {p.PlayerLevel}　伤害 {p.Clock.Count}/7", 15, p.Clock.Count >= 6 ? UiTheme.Opponent : UiTheme.Text));
        col.AddChild(UiTheme.Label($"手牌 {p.Hand.Count}　能量 {p.Stock.Count}　牌库 {p.DeckCount}", 12, UiTheme.TextDim));
        panel.AddChild(col);
        _cardsLayer.AddChild(panel);
    }

    private CardControl Place(CardView? card, Rect2 rect, Vector2 cardSize, int owner, ZoneType zone, int index)
    {
        var c = Card(card, cardSize, owner, zone);
        c.Position = rect.Position;
        c.Size = rect.Size;
        c.ZoneIndex = index;
        _cardsLayer.AddChild(c);
        _byKey[(owner, zone, index)] = c;
        if (card is { InstanceId: >= 0 }) _byId[card.InstanceId] = c;
        return c;
    }

    /// <summary>不可见的定位点（动画找不到具体位置时用整个区域）。</summary>
    private CardControl PlaceAnchor(Rect2 rect, int owner, ZoneType zone)
    {
        var c = new CardControl { Position = rect.Position, Size = rect.Size, MouseFilter = MouseFilterEnum.Ignore, Visible = false, OwnerIndex = owner, Zone = zone };
        _cardsLayer.AddChild(c);
        return c;
    }

    private void Pile(IReadOnlyList<CardView> cards, Rect2 rect, Vector2 cardSize, int owner, ZoneType zone, string title)
    {
        var c = Place(cards.LastOrDefault(), rect, cardSize, owner, zone, -1);
        c.Badge = cards.Count > 0 ? cards.Count.ToString() : null;
        c.StackDepth = cards.Count / 8;
        c.TooltipText = $"{title} {cards.Count} 张（点击查看）";
        foreach (var cv in cards)
            if (cv.InstanceId >= 0) _byId.TryAdd(cv.InstanceId, c);
    }

    private CardControl Card(CardView? card, Vector2 size, int owner, ZoneType zone, bool square = false)
    {
        var c = CardControl.Create(card, size, square);
        c.OwnerIndex = owner;
        c.Zone = zone;
        c.Clicked += OnClicked;
        c.Hovered += x => ShowDetail(x.Card);
        _controls.Add(c);
        return c;
    }

    private static void Clear(Node n)
    {
        foreach (var child in n.GetChildren())
        {
            n.RemoveChild(child);
            child.QueueFree();
        }
    }
}

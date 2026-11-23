using Godot;
using WsSim.Core.Agents;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>
/// 对局界面。规则全部在 <see cref="GameHost"/>（后台线程上的 WsSim.Core 引擎）里计算，
/// 这里只显示快照、把点击翻译成引擎给出的选项序号。
/// <para>
/// 操作方式：可以操作的卡有黄框。点一张卡，如果只对应一个选项就直接执行；
/// 如果对应多个（出角色要选格子、移动要选目标、攻击要选方式），这张卡变蓝框，
/// 可去的格子变绿框，点格子或右侧按钮完成。多选（换牌、弃牌）点卡切换选中，再按“确定”。
/// </para>
/// </summary>
public partial class GameScreen : Control
{
    private static readonly Vector2 StageCard = new(80, 112);
    private static readonly Vector2 HandCard = new(80, 112);
    private static readonly Vector2 SmallCard = new(46, 64);
    private static readonly Vector2 PileCard = new(70, 98);

    private readonly Main _main;
    private readonly GameConfig _config;
    private GameHost _host = null!;
    private GameView? _view;
    private Decision? _decision;
    private GameResult? _result;
    private string? _error;

    // 选择中的状态
    private readonly HashSet<int> _multi = new();
    private CardControl? _armed;
    private List<int> _armedOptions = new();
    private bool _showAllActions;

    private readonly List<CardControl> _controls = new();
    private bool _openZoneForDecision;

    // 版面
    private HBoxContainer _oppHand = null!;
    private VBoxContainer _oppArea = null!;
    private HBoxContainer _midBar = null!;
    private VBoxContainer _myArea = null!;
    private HBoxContainer _myHand = null!;
    private Label _detailName = null!;
    private CardControl _detailCard = null!;
    private Label _detailInfo = null!;
    private RichTextLabel _detailText = null!;
    private Label _prompt = null!;
    private VBoxContainer _options = null!;
    private RichTextLabel _log = null!;
    private PopupPanel? _zonePopup;
    private Control? _overlay;

    public GameScreen(Main main, GameConfig config)
    {
        _main = main;
        _config = config;
    }

    private int Me => 0;
    private bool IsMyDecision => _decision != null && !_config.Spectate;

    public override void _Ready()
    {
        BuildLayout();
        _host = new GameHost(_config.PlayerDeck, _config.OpponentDeck,
            _config.Spectate ? new SimpleAgent() : null, new SimpleAgent(), _config.Seed,
            new GameHostOptions
            {
                Viewer = Me,
                RevealAll = _config.Spectate,
                AiDelay = TimeSpan.FromMilliseconds(_config.AiDelayMs),
            });
        AppendLog($"[color=#9aa3b2]种子 {_config.Seed}　{_config.PlayerDeck.Name}（我） 对 {_config.OpponentDeck.Name}[/color]");
        _host.Start();
    }

    public override void _ExitTree() => _host?.Dispose();

    // ================================================================ 事件

    public override void _Process(double delta)
    {
        bool changed = false;
        while (_host.TryDequeue(out var ev))
        {
            changed = true;
            _view = ev.View;
            foreach (var line in ev.View.NewLog) AppendLog(Escape(PlayerNames(line)));
            switch (ev)
            {
                case DecisionRequired dr:
                    _decision = dr.Decision;
                    ResetSelection();
                    _openZoneForDecision = true;
                    break;
                case StateUpdated:
                    _decision = null;
                    ResetSelection();
                    break;
                case GameFinished f:
                    _decision = null;
                    _result = f.Result;
                    if (_main.SelfTestSteps > 0)
                        GD.Print($"SELFTEST done: {_selfTestDone} 个选择, {_selfTestFailures} 个失败；{f.Result.Reason}，{f.Result.Turns} 回合");
                    break;
                case HostFailed hf:
                    _decision = null;
                    _error = hf.Error.ToString();
                    GD.PrintErr(_error);
                    break;
            }
        }
        if (changed) Rebuild();
        if (changed && _openZoneForDecision && IsMyDecision) OpenZoneForDecision();
        _openZoneForDecision = false;
        if (changed && IsMyDecision && _selfTestDone < _main.SelfTestSteps) SelfTestStep();
        else if (changed && IsMyDecision && _main.SelfTestSteps > 0)
            ShowDetail(_controls.FirstOrDefault(c => c.Zone == ZoneType.Stage && c.Card != null)?.Card);
    }

    // ================================================================ 自检

    private int _selfTestDone;
    private int _selfTestFailures;

    /// <summary>像玩家一样点击界面做出选择，检查点击能对应到选项（用于无人值守的自动检查）。</summary>
    private void SelfTestStep()
    {
        var d = _decision!;
        _selfTestDone++;
        var controls = _controls.ToList();
        if (IsMulti(d))
        {
            foreach (var c in controls.Where(c => c.Highlight == CardHighlight.Selectable))
                if (_multi.Count < d.MinPicks) OnClicked(c);
            if (_multi.Count != d.MinPicks) SelfTestFail(d, $"点卡后选中 {_multi.Count} 张，应为 {d.MinPicks}");
            Submit(_multi.OrderBy(x => x).ToArray());
            return;
        }
        if (d.Type == DecisionType.MainAction)
        {
            foreach (var h in controls.Where(c => c.Zone == ZoneType.Hand && c.Highlight == CardHighlight.Selectable))
            {
                OnClicked(h);
                if (!ReferenceEquals(_armed, h)) continue;
                var dest = _controls.FirstOrDefault(c => c.Highlight == CardHighlight.Target && c.Card == null);
                if (dest != null)
                {
                    OnClicked(dest);
                    if (_decision != null) SelfTestFail(d, "点了绿框格子但没有出牌");
                    return;
                }
                OnClicked(h);
            }
            Submit(d.Options.ToList().FindIndex(o => o.IsPass));
            return;
        }
        var first = controls.OrderBy(c => c.GetParent() is HFlowContainer ? 0 : 1).FirstOrDefault(c => c.Highlight == CardHighlight.Selectable);
        if (first != null)
        {
            OnClicked(first);
            if (_decision != null && _armedOptions.Count > 0) Submit(_armedOptions[0]);
            if (_decision != null) SelfTestFail(d, "点了黄框的卡但没有做出选择");
            return;
        }
        Submit(0);
    }

    private void SelfTestFail(Decision d, string why)
    {
        _selfTestFailures++;
        GD.PrintErr($"SELFTEST FAIL #{d.Sequence} {d.Type}: {why}");
    }

    private void ResetSelection()
    {
        _multi.Clear();
        _armed = null;
        _armedOptions = new();
        _showAllActions = false;
    }

    private void Submit(params int[] answer)
    {
        if (_decision == null) return;
        if (!_host.Submit(_decision.Sequence, answer))
        {
            AppendLog("[color=#e0736e]这个选择不合法[/color]");
            return;
        }
        _decision = null;
        ResetSelection();
        _zonePopup?.Hide();
        Rebuild();
    }

    // ================================================================ 版面

    private void BuildLayout()
    {
        var root = new HBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        var board = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        board.AddThemeConstantOverride("separation", 4);
        var boardMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var m in new[] { "left", "right", "top", "bottom" })
            boardMargin.AddThemeConstantOverride("margin_" + m, 8);
        boardMargin.AddChild(board);
        root.AddChild(boardMargin);

        _oppHand = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, CustomMinimumSize = new Vector2(0, 50) };
        _oppArea = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _midBar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 34), Alignment = BoxContainer.AlignmentMode.Center };
        _midBar.AddThemeConstantOverride("separation", 16);
        _myArea = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _myHand = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, CustomMinimumSize = new Vector2(0, HandCard.Y + 6) };
        _myHand.AddThemeConstantOverride("separation", 4);
        board.AddChild(_oppHand);
        board.AddChild(_oppArea);
        var midPanel = new PanelContainer();
        midPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 4, 0, default, 8, 2));
        midPanel.AddChild(_midBar);
        board.AddChild(midPanel);
        board.AddChild(_myArea);
        board.AddChild(_myHand);

        // 右侧：卡片详情、选择、日志
        var side = new PanelContainer { CustomMinimumSize = new Vector2(400, 0) };
        side.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 0, 0, default, 10, 10));
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
        decisionPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelLight, 6, 0, default, 10, 8));
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

        var bottom = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var back = new Button { Text = "返回菜单" };
        back.Pressed += () => _main.BackToMenu();
        bottom.AddChild(back);
        col.AddChild(bottom);
    }

    private void Rebuild()
    {
        if (_view == null) return;
        _controls.Clear();
        var v = _view;
        var me = v.Players[Me];
        var opp = v.Players[1 - Me];

        // 对手手牌
        Clear(_oppHand);
        _oppHand.AddChild(UiTheme.Label($"对手手牌 {opp.Hand.Count}", 13, UiTheme.TextDim));
        var backs = new HBoxContainer();
        backs.AddThemeConstantOverride("separation", -14);
        foreach (var c in opp.Hand)
            backs.AddChild(Card(c, new Vector2(34, 48), 1 - Me, ZoneType.Hand));
        _oppHand.AddChild(backs);

        BuildArea(_oppArea, opp, mirrored: true);
        BuildArea(_myArea, me, mirrored: false);
        BuildMidBar(v);

        Clear(_myHand);
        foreach (var c in me.Hand)
            _myHand.AddChild(Card(c, HandCard, Me, ZoneType.Hand));

        RefreshHighlights();
        BuildDecisionPanel();
        ShowOverlayIfOver();
    }

    private void BuildArea(VBoxContainer area, PlayerView p, bool mirrored)
    {
        Clear(area);
        var row = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 14);
        area.AddChild(row);

        // 左：等级区、能量区、思出区
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(170, 0) };
        left.AddThemeConstantOverride("separation", 4);
        left.AddChild(UiTheme.Label($"等级 {p.PlayerLevel}", 14, UiTheme.Accent));
        var levelRow = new HBoxContainer();
        foreach (var c in p.Level) levelRow.AddChild(Card(c, SmallCard, p.Index, ZoneType.Level));
        if (p.Level.Count == 0) levelRow.AddChild(Placeholder(SmallCard));
        left.AddChild(levelRow);
        var piles = new HBoxContainer();
        piles.AddThemeConstantOverride("separation", 8);
        piles.AddChild(Pile("能量", p.Stock, p.Index, ZoneType.Stock, faceDown: !(_view!.Players[p.Index].Stock.FirstOrDefault()?.IsHidden == false)));
        piles.AddChild(Pile("思出", p.Memory, p.Index, ZoneType.Memory, faceDown: false));
        left.AddChild(piles);

        // 中：舞台 + 伤害区
        var center = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        center.AddThemeConstantOverride("separation", 2);
        var front = SlotRow(p, StageSlots.Front);
        var back = SlotRow(p, new[] { StageSlot.BackLeft, StageSlot.BackRight });
        var clock = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        clock.AddThemeConstantOverride("separation", 3);
        clock.AddChild(UiTheme.Label($"伤害 {p.Clock.Count}/7 ", 13, p.Clock.Count >= 6 ? UiTheme.Opponent : UiTheme.TextDim));
        foreach (var c in p.Clock) clock.AddChild(Card(c, SmallCard, p.Index, ZoneType.Clock));
        for (int i = p.Clock.Count; i < 7; i++) clock.AddChild(Placeholder(SmallCard));
        if (mirrored) { center.AddChild(clock); center.AddChild(back); center.AddChild(front); }
        else { center.AddChild(front); center.AddChild(back); center.AddChild(clock); }

        // 名场面区
        var cxCol = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        cxCol.AddChild(UiTheme.Label("名场面区", 12, UiTheme.TextDim));
        var cx = Card(p.ClimaxArea.LastOrDefault(), StageCard, p.Index, ZoneType.ClimaxArea);
        cx.EmptyLabel = "";
        cxCol.AddChild(cx);

        // 右：牌库、休息室
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(90, 0), Alignment = BoxContainer.AlignmentMode.Center };
        right.AddThemeConstantOverride("separation", 6);
        var deck = Card(null, PileCard, p.Index, ZoneType.Deck);
        deck.FaceDown = p.DeckCount > 0;
        deck.EmptyLabel = "牌库";
        deck.Badge = p.DeckCount.ToString();
        right.AddChild(UiTheme.Label("牌库", 12, UiTheme.TextDim));
        right.AddChild(deck);
        right.AddChild(UiTheme.Label("休息室", 12, UiTheme.TextDim));
        right.AddChild(Pile("休息室", p.WaitingRoom, p.Index, ZoneType.WaitingRoom, faceDown: false, size: PileCard, showTitle: false));

        var nodes = new Control[] { left, center, cxCol, right };
        if (mirrored) Array.Reverse(nodes);
        foreach (var n in nodes) row.AddChild(n);

        var name = UiTheme.Label((p.Index == Me ? "我　" : "对手　") + p.DeckName, 12, p.Index == Me ? UiTheme.Selected : UiTheme.Opponent);
        area.AddChild(name);
        if (mirrored) area.MoveChild(name, 0);
    }

    private HBoxContainer SlotRow(PlayerView p, StageSlot[] slots)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        foreach (var s in slots)
        {
            var c = Card(p.Stage[(int)s], StageCard, p.Index, ZoneType.Stage, square: true);
            c.Slot = s;
            c.EmptyLabel = s.ToChinese();
            c.ShowStats = true;
            row.AddChild(c);
        }
        return row;
    }

    private void BuildMidBar(GameView v)
    {
        Clear(_midBar);
        bool myTurn = v.ActivePlayer == Me;
        if (v.TurnNumber > 0)
            _midBar.AddChild(UiTheme.Label($"第 {v.TurnNumber} 回合　{(myTurn ? "我的回合" : "对手回合")}", 15,
                myTurn ? UiTheme.Selected : UiTheme.Opponent));
        _midBar.AddChild(UiTheme.Label(Texts.Phase(v.Phase), 15, UiTheme.Accent));
        if (v.Phase == Phase.Attack) _midBar.AddChild(UiTheme.Label($"已攻击 {v.AttacksThisTurn} 次", 13, UiTheme.TextDim));
        foreach (var p in v.Players)
            foreach (var c in p.Resolution)
                _midBar.AddChild(Card(c, new Vector2(22, 30), p.Index, ZoneType.Resolution));
    }

    private CardControl Pile(string title, IReadOnlyList<CardView> cards, int owner, ZoneType zone, bool faceDown, Vector2? size = null, bool showTitle = true)
    {
        var c = Card(cards.LastOrDefault(), size ?? SmallCard, owner, zone);
        c.FaceDown = faceDown && cards.Count > 0;
        c.EmptyLabel = showTitle || cards.Count == 0 ? title : null;
        c.Badge = cards.Count.ToString();
        c.TooltipText = $"{title} {cards.Count} 张" + (faceDown ? "" : "（点击查看）");
        return c;
    }

    private static Control Placeholder(Vector2 size)
    {
        var c = new CardControl { CardSize = size, CustomMinimumSize = size, EmptyLabel = "", MouseFilter = MouseFilterEnum.Ignore };
        return c;
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

    // ================================================================ 选项与点击

    private static bool IsMulti(Decision d) => !(d.MinPicks == 1 && d.MaxPicks == 1);

    private bool SourceMatches(DecisionOption o, CardControl c)
    {
        var d = _decision!;
        bool ownStage = c.Zone == ZoneType.Stage && c.OwnerIndex == d.Player;
        if (o.MainAction == MainActionKind.Move) return ownStage && c.Card != null && c.Slot == o.Slot;
        if (o.CardInstanceId is int id) return c.Card != null && c.Card.InstanceId == id;
        if (o.Slot is StageSlot s && o.AttackType == null) return ownStage && c.Slot == s;
        return false;
    }

    private static StageSlot? Dest(DecisionOption o) => o.MainAction switch
    {
        MainActionKind.PlayCharacter => o.Slot,
        MainActionKind.Move => o.TargetSlot,
        _ => null,
    };

    /// <summary>已选中一张卡时，点这个格子对应的选项。</summary>
    private int? DestOption(CardControl c)
    {
        if (_armed == null || c.Zone != ZoneType.Stage || c.OwnerIndex != _decision!.Player || c.Slot == null) return null;
        var opts = _decision.Options;
        var hits = _armedOptions.Where(i => Dest(opts[i]) == c.Slot).ToList();
        if (hits.Count == 1) return hits[0];
        // 两格都有角色时只列了一个方向的“移动”（交换），反方向点也算。
        if (_armed.Zone == ZoneType.Stage && _armed.Slot is { } from)
            for (int i = 0; i < opts.Count; i++)
                if (opts[i].MainAction == MainActionKind.Move && opts[i].Slot == c.Slot && opts[i].TargetSlot == from)
                    return i;
        return null;
    }

    private void OnClicked(CardControl c)
    {
        if (c.Zone is ZoneType.WaitingRoom or ZoneType.Memory && c.GetParent() is not HFlowContainer)
        {
            var list = c.Zone == ZoneType.WaitingRoom ? _view!.Players[c.OwnerIndex].WaitingRoom : _view!.Players[c.OwnerIndex].Memory;
            OpenZone(c.Zone == ZoneType.WaitingRoom ? "休息室" : "思出区", list, c.OwnerIndex, c.Zone);
            return;
        }
        if (!IsMyDecision) return;
        var d = _decision!;
        var matches = Enumerable.Range(0, d.Options.Count).Where(i => SourceMatches(d.Options[i], c)).ToList();

        if (IsMulti(d))
        {
            if (matches.Count == 0) return;
            if (!_multi.Remove(matches[0]))
            {
                if (_multi.Count >= d.MaxPicks) return;
                _multi.Add(matches[0]);
            }
            RefreshHighlights();
            BuildDecisionPanel();
            return;
        }

        if (DestOption(c) is int dest)
        {
            Submit(dest);
            return;
        }
        if (_armed != null && ReferenceEquals(_armed, c) || matches.Count == 0)
        {
            _armed = null;
            _armedOptions = new();
        }
        else if (matches.Count == 1 && Dest(d.Options[matches[0]]) == null)
        {
            Submit(matches[0]);
            return;
        }
        else
        {
            _armed = c;
            _armedOptions = matches;
        }
        RefreshHighlights();
        BuildDecisionPanel();
    }

    private void RefreshHighlights()
    {
        foreach (var c in _controls)
        {
            var h = CardHighlight.None;
            if (IsMyDecision)
            {
                var d = _decision!;
                if (IsMulti(d))
                {
                    for (int i = 0; i < d.Options.Count; i++)
                        if (SourceMatches(d.Options[i], c))
                            h = _multi.Contains(i) ? CardHighlight.Selected : CardHighlight.Selectable;
                }
                else if (_armed != null)
                {
                    if (_armed.Card != null && c.Card != null && c.Card.InstanceId == _armed.Card.InstanceId && c.Zone == _armed.Zone
                        || ReferenceEquals(c, _armed)
                        || (_armed.Zone == ZoneType.Stage && c.Zone == ZoneType.Stage && c.OwnerIndex == _armed.OwnerIndex && c.Slot == _armed.Slot))
                        h = CardHighlight.Selected;
                    else if (DestOption(c) != null)
                        h = CardHighlight.Target;
                }
                else if (d.Options.Any(o => SourceMatches(o, c)))
                {
                    h = CardHighlight.Selectable;
                }
            }
            c.Highlight = h;
            c.QueueRedraw();
        }
    }

    private void BuildDecisionPanel()
    {
        Clear(_options);
        if (_result != null || _error != null)
        {
            _prompt.Text = "对局已结束";
            return;
        }
        if (_decision == null)
        {
            _prompt.Text = _config.Spectate ? "观战中…" : (_view?.ActivePlayer == Me ? "处理中…" : "对手行动中…");
            return;
        }
        var d = _decision;
        if (_config.Spectate)
        {
            _prompt.Text = $"P{d.Player}：{d.Prompt}";
            return;
        }
        _prompt.Text = d.Prompt;

        if (IsMulti(d))
        {
            _options.AddChild(UiTheme.Label($"点击卡片选择，已选 {_multi.Count} 张（需要 {d.MinPicks}–{d.MaxPicks} 张）", 13, UiTheme.TextDim));
            for (int i = 0; i < d.Options.Count; i++)
            {
                int idx = i;
                var b = OptionButton(d.Options[i]);
                b.ToggleMode = true;
                b.ButtonPressed = _multi.Contains(i);
                b.Toggled += on =>
                {
                    if (on && _multi.Count < d.MaxPicks) _multi.Add(idx); else _multi.Remove(idx);
                    RefreshHighlights();
                    BuildDecisionPanel();
                };
                _options.AddChild(b);
            }
            var ok = new Button { Text = $"确定（{_multi.Count} 张）", Disabled = _multi.Count < d.MinPicks || _multi.Count > d.MaxPicks };
            ok.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.Accent.Darkened(0.35f), 4, 0, default, 10, 5));
            ok.Pressed += () => Submit(_multi.OrderBy(x => x).ToArray());
            _options.AddChild(ok);
            return;
        }

        IEnumerable<int> shown;
        if (_armed != null)
        {
            if (_armedOptions.Any(i => Dest(d.Options[i]) != null))
                _options.AddChild(UiTheme.Label("点击绿框的格子，或选择：", 13, UiTheme.TextDim));
            shown = _armedOptions;
        }
        else if (d.Type == DecisionType.MainAction && !_showAllActions)
        {
            _options.AddChild(UiTheme.Label("点击黄框的手牌出牌，点击舞台上的角色移动。", 13, UiTheme.TextDim));
            shown = Enumerable.Range(0, d.Options.Count).Where(i => d.Options[i].IsPass || d.Options[i].MainAction == MainActionKind.PlayEvent);
            var all = new Button { Text = "列出全部行动" };
            all.Pressed += () => { _showAllActions = true; BuildDecisionPanel(); };
            _options.AddChild(all);
        }
        else
        {
            shown = Enumerable.Range(0, d.Options.Count);
        }

        foreach (int i in shown)
        {
            int idx = i;
            var b = OptionButton(d.Options[i]);
            b.Pressed += () => Submit(idx);
            _options.AddChild(b);
        }
        if (_armed != null)
        {
            var cancel = new Button { Text = "取消" };
            cancel.Pressed += () => { _armed = null; _armedOptions = new(); RefreshHighlights(); BuildDecisionPanel(); };
            _options.AddChild(cancel);
        }
    }

    private Button OptionButton(DecisionOption o)
    {
        var b = new Button { Text = o.Label, Alignment = HorizontalAlignment.Left, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        if (o.IsPass) b.AddThemeColorOverride("font_color", UiTheme.TextDim);
        if (o.CardInstanceId is int id)
            b.MouseEntered += () => ShowDetail(_view?.FindCard(id));
        return b;
    }

    /// <summary>选项里的卡都在休息室 / 思出区时（箭头判定等），直接打开那个区域。</summary>
    private void OpenZoneForDecision()
    {
        var d = _decision!;
        var ids = d.Options.Where(o => o.CardInstanceId != null).Select(o => o.CardInstanceId!.Value).ToHashSet();
        if (ids.Count == 0) return;
        foreach (var p in _view!.Players)
            foreach (var (zone, list, title) in new[] { (ZoneType.WaitingRoom, p.WaitingRoom, "休息室"), (ZoneType.Memory, p.Memory, "思出区") })
                if (list.Any(c => ids.Contains(c.InstanceId)))
                {
                    OpenZone(title, list, p.Index, zone);
                    return;
                }
    }

    private void OpenZone(string title, IReadOnlyList<CardView> cards, int owner, ZoneType zone)
    {
        _zonePopup?.QueueFree();
        var popup = new PopupPanel();
        var col = new VBoxContainer();
        col.AddChild(UiTheme.Label($"{(owner == Me ? "我的" : "对手的")}{title}（{cards.Count} 张）", 16, UiTheme.Accent));
        int rows = Math.Max(1, (cards.Count + 8) / 9);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(780, Math.Min(420, rows * (HandCard.Y + 4) + 6)), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(flow);
        col.AddChild(scroll);
        popup.AddChild(col);
        foreach (var cv in cards)
            flow.AddChild(Card(cv, HandCard, owner, zone));
        AddChild(popup);
        _zonePopup = popup;
        RefreshHighlights();
        popup.PopupHide += () => _controls.RemoveAll(c => c.GetParent() == flow);
        popup.PopupCentered();
    }

    // ================================================================ 详情、日志、结束

    private void ShowDetail(CardView? cv)
    {
        if (cv?.Def is not { } def) return;
        var lib = _main.Library;
        _detailCard.Card = new CardView(cv.InstanceId, def, CardOrientation.Standing, cv.Power, cv.Soul);
        _detailCard.QueueRedraw();
        _detailName.Text = def.Name;
        var parts = new List<string> { def.Id, $"{Texts.ColorName(def.Color)}·{Texts.Type(def.Type)}" };
        if (!def.IsClimax) parts.Add($"等级 {def.Level}　费用 {def.Cost}");
        if (def.IsCharacter) parts.Add($"战力 {cv.Power}" + (cv.Power != def.Power ? $"（原 {def.Power}）" : "") + $"　魂 {cv.Soul}");
        if (def.RawTriggers.Count > 0) parts.Add("判定：" + string.Join(" ", def.RawTriggers.Select(Texts.Trigger)));
        if (def.Traits.Count > 0) parts.Add("特征：" + string.Join("・", def.Traits));
        _detailInfo.Text = string.Join("\n", parts);
        var texts = Texts.GeneratedTexts(def).Concat(lib?.EffectTexts(def) ?? def.TextsEn);
        _detailText.Text = string.Join("\n\n", texts);
    }

    private void AppendLog(string bbcode)
    {
        if (bbcode.Contains("=====")) bbcode = $"[color=#f2c14e]{bbcode}[/color]";
        _log.AppendText(bbcode + "\n");
    }

    private static string Escape(string s) => s.Replace("[", "[lb]");

    /// <summary>日志里的 P0 / P1 换成“我 / 对手”（观战时保持 P0 / P1）。</summary>
    private string PlayerNames(string line) => _config.Spectate
        ? line
        : System.Text.RegularExpressions.Regex.Replace(line, @"\bP([01])\b ?", m => m.Groups[1].Value == Me.ToString() ? "我" : "对手");

    private void ShowOverlayIfOver()
    {
        if (_overlay != null || (_result == null && _error == null)) return;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.55f) };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        shade.AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 10, 2, UiTheme.Accent, 30, 24));
        center.AddChild(panel);
        var col = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        col.AddThemeConstantOverride("separation", 12);
        panel.AddChild(col);

        string title, detail;
        if (_error != null)
        {
            title = "引擎出错";
            detail = _error.Split('\n')[0];
        }
        else
        {
            var r = _result!;
            title = r.IsDraw ? "平局" : _config.Spectate ? $"P{r.Winner} 获胜" : r.Winner == Me ? "你赢了！" : "你输了";
            detail = $"{PlayerNames(r.Reason)}　共 {r.Turns} 回合";
        }
        col.AddChild(UiTheme.Label(title, 32, UiTheme.Accent));
        var dl = UiTheme.Label(detail, 15, UiTheme.Text);
        dl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        dl.CustomMinimumSize = new Vector2(420, 0);
        col.AddChild(dl);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 10);
        var look = new Button { Text = "查看场面" };
        look.Pressed += () => shade.Visible = false;
        var again = new Button { Text = "再来一局" };
        again.Pressed += () => _main.StartGame(_config with { Seed = (ulong)System.Random.Shared.NextInt64() });
        var menu = new Button { Text = "返回菜单" };
        menu.Pressed += () => _main.BackToMenu();
        buttons.AddChild(look);
        buttons.AddChild(again);
        buttons.AddChild(menu);
        col.AddChild(buttons);
        AddChild(shade);
        _overlay = shade;
    }
}

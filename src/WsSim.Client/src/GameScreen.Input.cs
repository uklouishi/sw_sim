using Godot;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>对局界面：选项与点击。</summary>
public partial class GameScreen
{
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
            b.MouseEntered += () => ShowDetail(_view?.FindCard(id) is { IsHidden: false } v ? v
                : o.Card is { } def ? new CardView(id, def, CardOrientation.Standing, def.Power, def.Soul) : null);
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

}

using Godot;
using WsSim.Core.Cards;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>选卡组：下拉框 + 封面卡 + 构成（角色 / 事件 / 名场面、各等级张数、颜色）。</summary>
public partial class DeckPicker : PanelContainer
{
    private readonly OptionButton _select;
    private readonly CardControl _cover;
    private readonly Label _summary;
    private readonly Label _levels;
    private readonly HFlowContainer _thumbs;

    public DeckPicker(string title, Color accent, Action changed)
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(1, 1, 1, 0.04f), 10, 1, new Color(accent, 0.45f), 14, 12));
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        AddChild(col);
        col.AddChild(UiTheme.Label(title, 15, accent));
        _select = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
        _select.ItemSelected += _ => changed();
        col.AddChild(_select);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        col.AddChild(row);
        _cover = CardControl.Create(null, new Vector2(100, 140));
        _cover.MouseFilter = MouseFilterEnum.Ignore;
        _cover.EmptyLabel = "";
        row.AddChild(_cover);
        var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _summary = UiTheme.Label("", 14, UiTheme.Text);
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _levels = UiTheme.Label("", 13, UiTheme.TextDim);
        _levels.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.AddChild(_summary);
        info.AddChild(_levels);
        row.AddChild(info);
        _thumbs = new HFlowContainer();
        _thumbs.AddThemeConstantOverride("h_separation", 4);
        _thumbs.AddThemeConstantOverride("v_separation", 4);
        col.AddChild(_thumbs);
    }

    public string? SelectedName => _select.Selected >= 0 ? _select.GetItemText(_select.Selected) : null;

    public void Fill(SortedDictionary<string, string> decks, string saved, string fallback)
    {
        _select.Clear();
        int select = 0, i = 0;
        foreach (var name in decks.Keys)
        {
            _select.AddItem(name);
            if (name == saved || (saved == "" && name == fallback)) select = i;
            i++;
        }
        if (decks.Count > 0) _select.Select(select);
    }

    public void Display(Deck? deck, string? error)
    {
        if (deck == null)
        {
            _cover.Card = null;
            _summary.Text = error ?? "";
            _levels.Text = "";
            _cover.QueueRedraw();
            return;
        }
        var cards = deck.Cards;
        // 封面：等级最高的角色里张数最多的那张。
        var cover = cards.Where(c => c.IsCharacter).GroupBy(c => c.Id).OrderByDescending(g => g.First().Level).ThenByDescending(g => g.Count())
            .Select(g => g.First()).FirstOrDefault() ?? cards.FirstOrDefault();
        _cover.Card = cover == null ? null : new CardView(-1, cover, WsSim.Core.Game.CardOrientation.Standing, cover.Power, cover.Soul);
        _cover.QueueRedraw();
        int ch = cards.Count(c => c.IsCharacter), ev = cards.Count(c => c.IsEvent), cx = cards.Count(c => c.IsClimax);
        var colors = cards.GroupBy(c => c.Color).OrderByDescending(g => g.Count()).Select(g => $"{Texts.ColorName(g.Key)}{g.Count()}");
        _summary.Text = $"{cards.Count} 张　角色 {ch}　事件 {ev}　名场面 {cx}\n颜色：{string.Join(" ", colors)}";
        var levels = cards.Where(c => !c.IsClimax).GroupBy(c => c.Level).OrderBy(g => g.Key).Select(g => $"L{g.Key}×{g.Count()}");
        foreach (var child in _thumbs.GetChildren()) child.QueueFree();
        // 卡组一览：每种卡一张（按等级、名场面最后），鼠标停留显示名字和张数。
        foreach (var g in cards.GroupBy(c => c.Id).OrderBy(g => g.First().IsClimax).ThenBy(g => g.First().Level))
        {
            var d = g.First();
            var t = CardControl.Create(new CardView(-1, d, WsSim.Core.Game.CardOrientation.Standing, d.Power, d.Soul), new Vector2(42, 59));
            t.Badge = g.Count().ToString();
            t.TooltipText = $"{d.Name}（{d.Id}）×{g.Count()}";
            _thumbs.AddChild(t);
        }
        var problems = deck.Validate();
        _levels.Text = string.Join("　", levels) + (problems.Count > 0 ? "\n⚠ " + string.Join("；", problems.Take(2)) : "");
    }
}

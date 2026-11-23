using Godot;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>对局界面：卡片详情、日志、结束画面。</summary>
public partial class GameScreen
{
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
        if (lib != null && lib.GetTexture(def) == null && lib.ImageProblem(def) is { } problem)
            parts.Add("没有卡图：" + problem);
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

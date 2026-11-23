using Godot;

namespace WsSim.Client;

/// <summary>全局主题：中文系统字体和统一的配色。</summary>
public static class UiTheme
{
    public static readonly Color Background = new("#1b1f27");
    public static readonly Color Panel = new("#262c37");
    public static readonly Color PanelLight = new("#323a48");
    public static readonly Color Text = new("#e8eaed");
    public static readonly Color TextDim = new("#9aa3b2");
    public static readonly Color Accent = new("#f2c14e");
    public static readonly Color Selectable = new("#f2c14e");
    public static readonly Color Selected = new("#4ec9f2");
    public static readonly Color Target = new("#6ee07a");
    public static readonly Color Opponent = new("#e0736e");

    public static Theme Build()
    {
        var font = new SystemFont
        {
            FontNames = new[]
            {
                "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Noto Sans SC",
                "Source Han Sans SC", "WenQuanYi Zen Hei", "WenQuanYi Micro Hei", "sans-serif",
            },
            Antialiasing = TextServer.FontAntialiasing.Gray,
        };
        ThemeDB.FallbackFont = font;
        ThemeDB.FallbackFontSize = 15;

        var theme = new Theme { DefaultFont = font, DefaultFontSize = 15 };
        theme.SetStylebox("panel", "PanelContainer", Box(Panel, 6));
        theme.SetStylebox("panel", "PopupPanel", Box(Panel, 6, 2, PanelLight));
        theme.SetStylebox("normal", "Button", Box(PanelLight, 4, 0, default, 10, 5));
        theme.SetStylebox("hover", "Button", Box(PanelLight.Lightened(0.15f), 4, 0, default, 10, 5));
        theme.SetStylebox("pressed", "Button", Box(Accent.Darkened(0.4f), 4, 0, default, 10, 5));
        theme.SetStylebox("disabled", "Button", Box(Panel, 4, 0, default, 10, 5));
        theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
        theme.SetColor("font_color", "Label", Text);
        theme.SetColor("font_color", "Button", Text);
        theme.SetColor("font_disabled_color", "Button", TextDim);
        theme.SetColor("default_color", "RichTextLabel", Text);
        return theme;
    }

    public static StyleBoxFlat Box(Color bg, int radius, int border = 0, Color borderColor = default, int padX = 8, int padY = 6)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            ContentMarginLeft = padX,
            ContentMarginRight = padX,
            ContentMarginTop = padY,
            ContentMarginBottom = padY,
        };
        sb.SetCornerRadiusAll(radius);
        if (border > 0)
        {
            sb.SetBorderWidthAll(border);
            sb.BorderColor = borderColor;
        }
        return sb;
    }

    public static Label Label(string text, int size = 15, Color? color = null)
    {
        var l = new Label { Text = text };
        if (size != 15) l.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) l.AddThemeColorOverride("font_color", c);
        return l;
    }
}

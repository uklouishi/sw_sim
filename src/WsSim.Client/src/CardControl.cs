using Godot;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

public enum CardHighlight { None, Selectable, Selected, Target }

/// <summary>
/// 一张卡（或一个空的舞台格 / 一叠卡）。有卡图时画卡图，没有时画文字卡面；
/// 横置转 90°，倒置转 180°。点击和悬停通过事件交给 <see cref="GameScreen"/>。
/// </summary>
public partial class CardControl : Control
{
    public static CardLibrary? Library { get; set; }

    /// <summary>竖放时卡的大小（宽:高 = 5:7）。</summary>
    public Vector2 CardSize { get; set; } = new(80, 112);
    public CardView? Card { get; set; }
    /// <summary>画卡背（牌库、能量区、对手手牌）。</summary>
    public bool FaceDown { get; set; }
    /// <summary>没有卡时显示的文字（舞台空格的“前列左”等）。为 null 时空着不画。</summary>
    public string? EmptyLabel { get; set; }
    /// <summary>右上角的数字（牌库张数等）。</summary>
    public string? Badge { get; set; }
    /// <summary>在卡下方显示当前战力 / 魂（舞台上的角色）。</summary>
    public bool ShowStats { get; set; }
    public CardHighlight Highlight { get; set; }

    // 由 GameScreen 填写，用来把点击对应到选项。
    public int OwnerIndex { get; set; }
    public ZoneType Zone { get; set; }
    public StageSlot? Slot { get; set; }

    public event Action<CardControl>? Clicked;
    public event Action<CardControl>? Hovered;

    public CardControl()
    {
        MouseFilter = MouseFilterEnum.Stop;
        MouseEntered += () => Hovered?.Invoke(this);
    }

    public static CardControl Create(CardView? card, Vector2 cardSize, bool square = false)
    {
        var c = new CardControl { Card = card, CardSize = cardSize };
        float side = Math.Max(cardSize.X, cardSize.Y);
        c.CustomMinimumSize = square ? new Vector2(side, side) : cardSize;
        return c;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            Clicked?.Invoke(this);
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        var size = Size;
        var font = GetThemeDefaultFont();
        bool hasCard = Card != null || FaceDown;

        if (!hasCard)
        {
            if (EmptyLabel != null)
            {
                var r = CenteredRect(size, CardSize, 1f);
                DrawRect(r, new Color(1, 1, 1, 0.04f));
                DrawRect(r, new Color(1, 1, 1, 0.18f), false, 1.5f);
                DrawString(font, new Vector2(r.Position.X, r.GetCenter().Y + 6), EmptyLabel,
                    HorizontalAlignment.Center, r.Size.X, 13, UiTheme.TextDim);
            }
        }
        else
        {
            var orientation = Card?.Orientation ?? CardOrientation.Standing;
            float angle = orientation switch
            {
                CardOrientation.Rested => Mathf.Pi / 2,
                CardOrientation.Reversed => Mathf.Pi,
                _ => 0,
            };
            var w = CardSize.X;
            var h = CardSize.Y;
            bool sideways = orientation == CardOrientation.Rested;
            float scale = sideways ? Math.Min(1f, Math.Min(size.X / h, size.Y / w)) : Math.Min(1f, Math.Min(size.X / w, size.Y / h));
            DrawSetTransform(size / 2, angle, new Vector2(scale, scale));
            var rect = new Rect2(-w / 2, -h / 2, w, h);
            if (FaceDown || Card!.IsHidden) DrawBack(rect, font);
            else DrawFace(rect, font);
            if (orientation == CardOrientation.Reversed)
                DrawRect(rect, new Color(0, 0, 0, 0.45f));
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        }

        if (ShowStats && Card is { Def: { } def } cv && def.IsCharacter)
        {
            var bar = new Rect2(0, size.Y - 18, size.X, 18);
            DrawRect(bar, new Color(0, 0, 0, 0.72f));
            var powerColor = cv.Power > def.Power ? UiTheme.Target : cv.Power < def.Power ? UiTheme.Opponent : UiTheme.Text;
            DrawString(font, new Vector2(4, size.Y - 4), cv.Power.ToString(), HorizontalAlignment.Left, size.X - 8, 13, powerColor);
            DrawString(font, new Vector2(4, size.Y - 4), $"魂{cv.Soul}", HorizontalAlignment.Right, size.X - 8, 13, UiTheme.Accent);
        }

        if (Badge != null)
        {
            var bw = font.GetStringSize(Badge, HorizontalAlignment.Left, -1, 14).X + 10;
            var br = new Rect2(size.X - bw - 2, 2, bw, 20);
            DrawRect(br, new Color(0, 0, 0, 0.8f));
            DrawString(font, new Vector2(br.Position.X + 5, 17), Badge, HorizontalAlignment.Left, -1, 14, UiTheme.Text);
        }

        if (Highlight != CardHighlight.None)
        {
            var color = Highlight switch
            {
                CardHighlight.Selected => UiTheme.Selected,
                CardHighlight.Target => UiTheme.Target,
                _ => UiTheme.Selectable,
            };
            DrawRect(new Rect2(Vector2.One, size - Vector2.One * 2), color, false, 3f);
        }
    }

    private static Rect2 CenteredRect(Vector2 size, Vector2 card, float s) =>
        new((size - card * s) / 2, card * s);

    private void DrawBack(Rect2 r, Font font)
    {
        DrawRect(r, new Color("#2b3f73"));
        DrawRect(r.Grow(-4), new Color("#1d2c55"));
        DrawRect(r.Grow(-4), new Color("#8aa0d8"), false, 1.5f);
        if (r.Size.X >= 44)
            DrawString(font, new Vector2(r.Position.X, r.GetCenter().Y + 7), "WS", HorizontalAlignment.Center, r.Size.X, 20, new Color("#8aa0d8"));
    }

    private void DrawFace(Rect2 r, Font font)
    {
        var def = Card!.Def!;
        var tex = Library?.GetTexture(def);
        if (tex != null)
        {
            DrawTextureRect(tex, r, false);
            DrawRect(r, new Color(0, 0, 0, 0.6f), false, 1f);
            return;
        }

        // 没有卡图：画文字卡面。
        var tint = Texts.CardTint(def.Color);
        DrawRect(r, tint.Darkened(0.55f));
        DrawRect(new Rect2(r.Position, new Vector2(r.Size.X, 18)), tint);
        DrawRect(r, tint, false, 2f);
        float x = r.Position.X + 4, y = r.Position.Y;
        string head = def.IsClimax ? "名场面" : def.IsEvent ? $"事件 L{def.Level} C{def.Cost}" : $"L{def.Level} C{def.Cost}";
        DrawString(font, new Vector2(x, y + 14), head, HorizontalAlignment.Left, r.Size.X - 8, 12, Colors.Black);
        DrawMultilineString(font, new Vector2(x, y + 34), def.Name, HorizontalAlignment.Left, r.Size.X - 8, 12, 4, UiTheme.Text);
        if (def.IsCharacter)
            DrawString(font, new Vector2(x, r.End.Y - 6), def.Power.ToString(), HorizontalAlignment.Left, r.Size.X - 8, 12, UiTheme.Text);
        if (def.RawTriggers.Count > 0)
            DrawString(font, new Vector2(x, r.End.Y - 6), string.Join("", def.RawTriggers.Select(Texts.Trigger)),
                HorizontalAlignment.Right, r.Size.X - 8, 11, UiTheme.Accent);
    }
}

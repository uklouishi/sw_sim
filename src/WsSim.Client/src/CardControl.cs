using Godot;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

public enum CardHighlight { None, Selectable, Selected, Target }

/// <summary>
/// 一张卡（或一个空的舞台格 / 一叠卡）。有卡图时画卡图，没有时画文字卡面；
/// 横置转 90°，倒置转 180°。点击和悬停通过事件交给 <see cref="GameScreen"/>。
/// 动画用：<see cref="Angle"/> 覆盖方向，<see cref="Flip"/> 做翻面（1 → 0 → 1 时换面）。
/// </summary>
public partial class CardControl : Control
{
    public static CardLibrary? Library { get; set; }

    /// <summary>竖放时卡的大小（宽:高 = 5:7）。</summary>
    public Vector2 CardSize { get; set; } = new(80, 112);
    public CardView? Card { get; set; }
    /// <summary>画卡背（牌库、能量区、对手手牌）。</summary>
    public bool FaceDown { get; set; }
    /// <summary>没有卡时显示的文字。为 null 时空着不画（场地底图已画了格子）。</summary>
    public string? EmptyLabel { get; set; }
    /// <summary>右上角的数字（牌库张数等）。</summary>
    public string? Badge { get; set; }
    /// <summary>在卡下方显示当前战力 / 魂（舞台上的角色）。</summary>
    public bool ShowStats { get; set; }
    public CardHighlight Highlight { get; set; }
    /// <summary>画成一叠（牌库、休息室）：数值是下面露出几层。</summary>
    public int StackDepth { get; set; }

    /// <summary>覆盖卡的方向（弧度）；NaN 时按 <see cref="CardView.Orientation"/>。能量区、等级区的卡固定横放。</summary>
    public float Angle { get; set; } = float.NaN;
    /// <summary>额外旋转（弧度，动画用）。</summary>
    public float AngleOffset { get; set; }
    /// <summary>水平缩放（翻面动画用）。</summary>
    public float Flip { get; set; } = 1f;
    /// <summary>发光强度（动画落点 / 攻击中）。</summary>
    public float Glow { get; set; }

    // 由 GameScreen 填写，用来把点击对应到选项。
    public int OwnerIndex { get; set; }
    public ZoneType Zone { get; set; }
    public StageSlot? Slot { get; set; }
    /// <summary>在区域列表里的序号（动画定位用）；一叠卡的整体为 -1。</summary>
    public int ZoneIndex { get; set; } = -1;

    public event Action<CardControl>? Clicked;
    public event Action<CardControl>? Hovered;
    public event Action<CardControl>? Unhovered;

    public CardControl()
    {
        MouseFilter = MouseFilterEnum.Stop;
        MouseEntered += () => Hovered?.Invoke(this);
        MouseExited += () => Unhovered?.Invoke(this);
    }

    public static CardControl Create(CardView? card, Vector2 cardSize, bool square = false)
    {
        var c = new CardControl { Card = card, CardSize = cardSize };
        float side = Math.Max(cardSize.X, cardSize.Y);
        c.CustomMinimumSize = square ? new Vector2(side, side) : cardSize;
        return c;
    }

    public static float OrientationAngle(CardOrientation o) => o switch
    {
        CardOrientation.Rested => Mathf.Pi / 2,
        CardOrientation.Reversed => Mathf.Pi,
        _ => 0,
    };

    /// <summary>当前显示用的方向（不含动画偏移）。</summary>
    public float BaseAngle => float.IsNaN(Angle) ? OrientationAngle(Card?.Orientation ?? CardOrientation.Standing) : Angle;

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
            float angle = BaseAngle + AngleOffset;
            var w = CardSize.X;
            var h = CardSize.Y;
            bool sideways = Mathf.Abs(Mathf.Sin(angle)) > 0.7f;
            float scale = sideways ? Math.Min(1f, Math.Min(size.X / h, size.Y / w)) : Math.Min(1f, Math.Min(size.X / w, size.Y / h));
            var rect = new Rect2(-w / 2, -h / 2, w, h);

            // 一叠卡：下面露出几层。
            for (int i = Math.Min(StackDepth, 4); i >= 1; i--)
            {
                DrawSetTransform(size / 2 + new Vector2(i * 1.5f, i * 2f), angle, new Vector2(scale, scale));
                DrawRect(rect, new Color(0, 0, 0, 0.55f));
                DrawRect(rect, new Color(1, 1, 1, 0.12f), false, 1f);
            }

            // 阴影
            DrawSetTransform(size / 2 + new Vector2(3, 4), angle, new Vector2(scale * Mathf.Abs(Flip), scale));
            DrawRect(rect, new Color(0, 0, 0, 0.35f));

            DrawSetTransform(size / 2, angle, new Vector2(scale * Mathf.Max(0.001f, Mathf.Abs(Flip)), scale));
            if (Glow > 0.01f)
                for (int g = 3; g >= 1; g--)
                    DrawRect(rect.Grow(g * 3), new Color(UiTheme.Accent, 0.12f * Glow), true);
            if (FaceDown || Card!.IsHidden) DrawBack(rect, font);
            else if (Library?.GetTexture(Card.Def!) is { } tex && tex.GetWidth() > tex.GetHeight())
            {
                // 名场面的卡图是横的：竖着的卡上横印着，横放（顺时针 90°）时正好看正。
                DrawSetTransform(size / 2, angle - Mathf.Pi / 2, new Vector2(scale, scale * Mathf.Max(0.001f, Mathf.Abs(Flip))));
                var land = new Rect2(-h / 2, -w / 2, h, w);
                DrawTextureRect(tex, land, false);
                DrawRect(land, new Color(0, 0, 0, 0.6f), false, 1f);
            }
            else DrawFace(rect, font);
            if (Card?.Orientation == CardOrientation.Reversed && float.IsNaN(Angle))
                DrawRect(rect, new Color(0, 0, 0, 0.45f));
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        }

        if (ShowStats && Card is { Def: { } def } cv && def.IsCharacter)
        {
            var bar = new Rect2(4, size.Y - 18, size.X - 8, 17);
            DrawRect(bar, new Color(0, 0, 0, 0.75f));
            var powerColor = cv.Power > def.Power ? UiTheme.Target : cv.Power < def.Power ? UiTheme.Opponent : UiTheme.Text;
            DrawString(font, new Vector2(8, size.Y - 5), cv.Power.ToString(), HorizontalAlignment.Left, size.X - 16, 13, powerColor);
            DrawString(font, new Vector2(8, size.Y - 5), $"魂{cv.Soul}", HorizontalAlignment.Right, size.X - 16, 13, UiTheme.Accent);
        }

        if (Badge != null)
        {
            var bw = font.GetStringSize(Badge, HorizontalAlignment.Left, -1, 13).X + 10;
            var br = new Rect2(size.X - bw - 2, 2, bw, 18);
            DrawStyleBox(UiTheme.Box(new Color(0, 0, 0, 0.8f), 9), br);
            DrawString(font, new Vector2(br.Position.X + 5, 16), Badge, HorizontalAlignment.Left, -1, 13, UiTheme.Text);
        }

        if (Highlight != CardHighlight.None)
        {
            var color = Highlight switch
            {
                CardHighlight.Selected => UiTheme.Selected,
                CardHighlight.Target => UiTheme.Target,
                _ => UiTheme.Selectable,
            };
            var hr = HighlightRect(size);
            DrawRect(hr.Grow(2), new Color(color, 0.25f), false, 6f);
            DrawRect(hr, color, false, 2.5f);
        }
    }

    /// <summary>高亮框贴着卡（横置时贴横放的卡），空格时贴整个格子。</summary>
    private Rect2 HighlightRect(Vector2 size)
    {
        if (Card == null && !FaceDown) return new Rect2(Vector2.One * 2, size - Vector2.One * 4);
        bool sideways = Mathf.Abs(Mathf.Sin(BaseAngle)) > 0.7f;
        var cs = sideways ? new Vector2(CardSize.Y, CardSize.X) : CardSize;
        float s = Math.Min(1f, Math.Min(size.X / cs.X, size.Y / cs.Y));
        return CenteredRect(size, cs, s).Grow(1);
    }

    private static Rect2 CenteredRect(Vector2 size, Vector2 card, float s) =>
        new((size - card * s) / 2, card * s);

    private void DrawBack(Rect2 r, Font font)
    {
        var tex = Assets.Get(Assets.CardBack);
        if (tex != null)
        {
            DrawTextureRect(tex, r, false);
            DrawRect(r, new Color(0, 0, 0, 0.5f), false, 1f);
            return;
        }
        // 没有卡背素材：黑白银的简易卡背。
        DrawRect(r, new Color("#1a1a1f"));
        DrawRect(r.Grow(-3), new Color("#2d2d35"));
        DrawRect(r.Grow(-3), new Color("#c9ccd6"), false, 1.5f);
        DrawRect(r.Grow(-7), new Color("#c9ccd6", 0.4f), false, 1f);
        if (r.Size.X >= 44)
            DrawString(font, new Vector2(r.Position.X, r.GetCenter().Y + 7), "WS", HorizontalAlignment.Center, r.Size.X, 20, new Color("#e6e8ef"));
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
        DrawRect(r, tint.Darkened(0.6f));
        DrawRect(new Rect2(r.Position, new Vector2(r.Size.X, 18)), tint);
        DrawRect(r, tint, false, 2f);
        float x = r.Position.X + 4, y = r.Position.Y;
        int fs = r.Size.X < 60 ? 9 : 12;
        string head = def.IsClimax ? "名场面" : def.IsEvent ? $"事件 L{def.Level} C{def.Cost}" : $"L{def.Level} C{def.Cost}";
        DrawString(font, new Vector2(x, y + 14), head, HorizontalAlignment.Left, r.Size.X - 8, fs, Colors.Black);
        DrawMultilineString(font, new Vector2(x, y + 34), def.Name, HorizontalAlignment.Left, r.Size.X - 8, fs, 4, UiTheme.Text);
        if (def.IsCharacter)
            DrawString(font, new Vector2(x, r.End.Y - 6), def.Power.ToString(), HorizontalAlignment.Left, r.Size.X - 8, fs, UiTheme.Text);
        if (def.RawTriggers.Count > 0)
            DrawString(font, new Vector2(x, r.End.Y - 6), string.Join("", def.RawTriggers.Select(Texts.Trigger)),
                HorizontalAlignment.Right, r.Size.X - 8, fs - 1, UiTheme.Accent);
    }
}

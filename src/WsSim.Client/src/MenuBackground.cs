using Godot;

namespace WsSim.Client;

/// <summary>菜单背景：素材 menu-bg.png（铺满），没有时画深色渐变和两张斜放的卡背。</summary>
public partial class MenuBackground : Control
{
    public MenuBackground()
    {
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        var size = Size;
        var bg = Assets.Get(Assets.MenuBackground);
        if (bg != null)
        {
            var ts = bg.GetSize();
            float s = Math.Max(size.X / ts.X, size.Y / ts.Y);
            DrawTextureRectRegion(bg, new Rect2(Vector2.Zero, size), new Rect2((ts - size / s) / 2, size / s));
        }
        else
        {
            int bands = 40;
            for (int i = 0; i < bands; i++)
            {
                float t = i / (float)(bands - 1);
                var c = new Color("#1c2030").Lerp(new Color("#07080c"), t);
                DrawRect(new Rect2(0, size.Y * i / bands, size.X, size.Y / bands + 1), c);
            }
            var back = Assets.Get(Assets.CardBack);
            if (back != null)
            {
                foreach (var (pos, angle, scale, alpha) in new[] { (new Vector2(0.33f, 0.78f), -0.32f, 1.9f, 0.16f), (new Vector2(0.86f, 0.30f), 0.2f, 2.4f, 0.10f) })
                {
                    var cs = new Vector2(247, 342) * scale * (size.Y / 900f);
                    DrawSetTransform(size * pos, angle, Vector2.One);
                    DrawTextureRect(back, new Rect2(-cs / 2, cs), false, new Color(1, 1, 1, alpha));
                }
                DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            }
        }
        // 左侧压暗，让按钮清楚
        for (int i = 0; i < 20; i++)
            DrawRect(new Rect2(i * size.X * 0.03f, 0, size.X * 0.03f, size.Y), new Color(0, 0, 0, 0.35f * (1 - i / 20f)));
    }
}

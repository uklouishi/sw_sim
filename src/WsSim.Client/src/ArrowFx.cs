using Godot;

namespace WsSim.Client;

/// <summary>攻击箭头。</summary>
public partial class ArrowFx : Control
{
    public Vector2 From { get; set; }
    public Vector2 To { get; set; }
    public Color Color { get; set; }
    public float Progress { get; set; }
    public string Label { get; set; } = "";

    public ArrowFx()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var end = From.Lerp(To, Progress);
        var dir = (To - From).Normalized();
        var normal = new Vector2(-dir.Y, dir.X);
        DrawLine(From, end, new Color(Color, 0.25f), 16f, true);
        DrawLine(From, end, Color, 6f, true);
        var head = new[] { end + dir * 14, end - dir * 16 + normal * 16, end - dir * 16 - normal * 16 };
        DrawColoredPolygon(head, Color);
        DrawCircle(From, 9, Color);
        if (Progress > 0.6f)
        {
            var font = GetThemeDefaultFont();
            var mid = From.Lerp(To, 0.5f) + normal * 26;
            DrawString(font, mid - new Vector2(50, -6), Label, HorizontalAlignment.Center, 100, 18, Colors.White);
        }
    }
}

using Godot;
using WsSim.Core.Game;

namespace WsSim.Client;

/// <summary>
/// 场地底图：素材 playmat.png（没有时画渐变），上面画双方的区域框和区域名。
/// 区域名对双方都正着写，方便阅读。
/// </summary>
public partial class PlaymatControl : Control
{
    public PlaymatControl()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    private static readonly Color Line = new(0.85f, 0.88f, 0.95f, 0.22f);
    private static readonly Color Fill = new(1, 1, 1, 0.035f);
    private static readonly Color LabelColor = new(0.85f, 0.88f, 0.95f, 0.38f);

    public override void _Draw()
    {
        var size = BoardLayout.Size;
        var full = new Rect2(Vector2.Zero, size);
        var mat = Assets.Get(Assets.Playmat);
        if (mat != null)
        {
            // 铺满（裁掉多余部分，保持比例）。
            var ts = mat.GetSize();
            float s = Math.Max(size.X / ts.X, size.Y / ts.Y);
            var src = new Rect2((ts - size / s) / 2, size / s);
            DrawTextureRectRegion(mat, full, src);
        }
        else
        {
            DrawProceduralMat(size);
        }

        // 中线
        float cy = BoardLayout.CenterY;
        DrawLine(new Vector2(40, cy), new Vector2(size.X - 40, cy), new Color(0.8f, 0.85f, 1f, 0.18f), 2f);
        DrawLine(new Vector2(size.X * 0.3f, cy), new Vector2(size.X * 0.7f, cy), new Color(0.85f, 0.9f, 1f, 0.35f), 1f);

        foreach (bool bottom in new[] { true, false })
        {
            foreach (var s in StageSlots.All)
                Zone(BoardLayout.Stage(s, bottom), s.IsFront() ? "前列" : "后列", bottom, small: true);
            Zone(BoardLayout.Climax(bottom), "名场面", bottom);
            Zone(BoardLayout.StockArea(bottom), "能量", bottom);
            Zone(BoardLayout.LevelArea(bottom), "等级", bottom);
            var clock = BoardLayout.ClockArea(bottom);
            Zone(clock.Grow(4), "伤害", bottom);
            for (int i = 1; i < 7; i++)
            {
                var a = BoardLayout.ClockCard(i, bottom);
                float x = bottom ? a.Position.X - (BoardLayout.ClockStep - BoardLayout.SmallCard.X) / 2 : a.End.X + (BoardLayout.ClockStep - BoardLayout.SmallCard.X) / 2;
                DrawLine(new Vector2(x, clock.Position.Y + 6), new Vector2(x, clock.End.Y - 6), new Color(Line, 0.12f), 1f);
            }
            Zone(BoardLayout.Deck(bottom), "牌库", bottom);
            Zone(BoardLayout.WaitingRoom(bottom), "休息室", bottom);
            Zone(BoardLayout.Memory(bottom), "思出", bottom);
            Zone(BoardLayout.Resolution(bottom), "判定", bottom, dashed: true);
        }
    }

    private void Zone(Rect2 r, string label, bool bottom, bool small = false, bool dashed = false)
    {
        DrawStyleBox(Box(dashed), r);
        var font = GetThemeDefaultFont();
        int fs = small ? 11 : 12;
        // 区域名写在框里（有卡时被卡盖住，张数看卡上的角标）。
        float y = r.GetCenter().Y + 5;
        DrawString(font, new Vector2(r.Position.X, y), label, HorizontalAlignment.Center, r.Size.X, fs, LabelColor);
    }

    private static StyleBoxFlat? _box, _dashed;

    private static StyleBoxFlat Box(bool dashed)
    {
        if (dashed)
        {
            _dashed ??= UiTheme.Box(new Color(1, 1, 1, 0.015f), 6, 1, new Color(Line, 0.12f), 0, 0);
            return _dashed;
        }
        _box ??= UiTheme.Box(Fill, 6, 1, Line, 0, 0);
        return _box;
    }

    /// <summary>没有 playmat.png 时：深色渐变 + 暗角 + 双方半场微弱的色调。</summary>
    private void DrawProceduralMat(Vector2 size)
    {
        int bands = 30;
        for (int i = 0; i < bands; i++)
        {
            float t = i / (float)(bands - 1);
            float d = Mathf.Abs(t - 0.5f) * 2; // 0 在中线，1 在边缘
            var c = new Color("#20232c").Lerp(new Color("#0e1015"), d * d);
            DrawRect(new Rect2(0, size.Y * i / bands, size.X, size.Y / bands + 1), c);
        }
        // 两侧暗角
        for (int i = 0; i < 12; i++)
        {
            float a = 0.05f * (12 - i) / 12f;
            DrawRect(new Rect2(i * 8, 0, 8, size.Y), new Color(0, 0, 0, a * 3));
            DrawRect(new Rect2(size.X - (i + 1) * 8, 0, 8, size.Y), new Color(0, 0, 0, a * 3));
        }
        // 半场色调：我方偏蓝，对手偏红
        DrawRect(new Rect2(0, size.Y / 2, size.X, size.Y / 2), new Color(0.3f, 0.5f, 1f, 0.025f));
        DrawRect(new Rect2(0, 0, size.X, size.Y / 2), new Color(1f, 0.35f, 0.3f, 0.025f));
    }
}

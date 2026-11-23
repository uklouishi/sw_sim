using Godot;
using WsSim.Core.Game;
using WsSim.Core.Hosting;

namespace WsSim.Client;

/// <summary>
/// 对局界面：动画。引擎每一步变化都是一个 <see cref="BoardChanged"/> 事件，按顺序播放：
/// 卡从原来的位置飞到新位置（看不到正面的一端显示卡背，途中翻面），横置 / 倒置转动，
/// 攻击画箭头，回合开始显示横幅。播放期间后面的事件排队等待，所以对手的每一步都看得清。
/// 连续的同类移动（洗牌重整、升级时放回休息室等）合成一批，错开一点同时播放。
/// </summary>
public partial class GameScreen
{
    private readonly Queue<HostEvent> _queue = new();
    private double _busy;

    private float Factor => _main.SelfTestSteps > 0 && !_main.AnimFromArgs ? 0f : _main.Settings.AnimFactor;
    private bool Animate => Factor > 0;

    private const float MoveTime = 0.34f;
    private const float Stagger = 0.045f;

    private readonly record struct Spot(Rect2 Rect, Vector2 CardSize, float Angle);

    private static Spot SpotOf(CardControl c) => new(new Rect2(c.Position, c.Size), c.CardSize, c.BaseAngle);

    private void PlayStep(BoardChanged first)
    {
        // 收集同一批：同一玩家、同一来源和去向的连续移动。
        var batch = new List<BoardChanged> { first };
        if (first.Event is CardMovedEvent m0)
        {
            while (batch.Count < 60 && _queue.TryPeek(out var next)
                   && next is BoardChanged { Event: CardMovedEvent m1 }
                   && m1.Owner == m0.Owner && m1.From == m0.From && m1.To == m0.To
                   && m0.From != ZoneType.Stage && m0.To != ZoneType.Stage)
                batch.Add((BoardChanged)_queue.Dequeue());
        }

        // 记下现在每张卡的位置，再换成新快照。
        var oldKey = _byKey.ToDictionary(kv => kv.Key, kv => SpotOf(kv.Value));
        var oldId = _byId.ToDictionary(kv => kv.Key, kv => SpotOf(kv.Value));
        var oldOrientation = _byKey.Where(kv => kv.Key.Zone == ZoneType.Stage && kv.Value.Card != null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.BaseAngle);
        foreach (var b in batch) AcceptView(b.View);
        Rebuild();

        float f = Factor;
        double busy = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            var b = batch[i];
            switch (b.Event)
            {
                case CardMovedEvent m:
                    float delay = i * Stagger * f;
                    AnimateMove(b, m, oldKey, oldId, delay, MoveTime * f);
                    busy = Math.Max(busy, delay + MoveTime * f);
                    break;
                case CardOrientationEvent o:
                    AnimateOrientation(o, 0.26f * f);
                    busy = 0.26f * f;
                    break;
                case AttackDeclaredEvent a:
                    AnimateAttack(a, 0.7f * f);
                    busy = 0.6f * f;
                    break;
                case ShuffleEvent s:
                    AnimateShuffle(s, 0.4f * f);
                    busy = 0.4f * f;
                    break;
                case PhaseEvent { Phase: Phase.Stand } pe:
                    ShowBanner(TurnBannerText(pe), pe.ActivePlayer == Me ? UiTheme.Selected : UiTheme.Opponent, 1.1f * f);
                    busy = 0.9f * f;
                    break;
                case PhaseEvent pe when pe.Phase is Phase.Attack or Phase.Climax or Phase.Encore:
                    FlashPhase(0.4f * f);
                    busy = 0.12f * f;
                    break;
            }
        }
        _busy = busy;
    }

    private string TurnBannerText(PhaseEvent pe) =>
        $"第 {pe.TurnNumber} 回合　" + (_config.Spectate ? $"P{pe.ActivePlayer} 的回合" : pe.ActivePlayer == Me ? "我的回合" : "对手的回合");

    private static int KeyIndex(ZoneType zone, int index, StageSlot? slot) => zone switch
    {
        ZoneType.Stage => slot is { } s ? (int)s : index,
        ZoneType.Deck or ZoneType.WaitingRoom or ZoneType.Memory or ZoneType.ClimaxArea => -1,
        _ => index,
    };

    private Spot? FindOld(Dictionary<(int, ZoneType, int), Spot> oldKey, int owner, ZoneType zone, int index)
    {
        if (oldKey.TryGetValue((owner, zone, index), out var s)) return s;
        if (oldKey.TryGetValue((owner, zone, -1), out s)) return s;
        return null;
    }

    private CardControl? FindNew(int owner, ZoneType zone, int index)
    {
        if (_byKey.TryGetValue((owner, zone, index), out var c)) return c;
        if (_byKey.TryGetValue((owner, zone, -1), out c)) return c;
        return null;
    }

    private void AnimateMove(BoardChanged b, CardMovedEvent m, Dictionary<(int, ZoneType, int), Spot> oldKey,
        Dictionary<int, Spot> oldId, float delay, float dur)
    {
        var dst = FindNew(m.Owner, m.To, KeyIndex(m.To, m.ToIndex, m.ToSlot));
        if (dst == null) return;
        Spot? srcOpt = m.InstanceId >= 0 && oldId.TryGetValue(m.InstanceId, out var byId) ? byId : null;
        srcOpt ??= FindOld(oldKey, m.Owner, m.From, KeyIndex(m.From, m.FromIndex, m.FromSlot));
        if (srcOpt is not { } src) return;
        var to = SpotOf(dst);

        // 落点是单张卡时，先藏起来等飞到再显示；一叠卡（休息室等）直接叠上去。
        bool hideDst = dst.ZoneIndex >= 0 || dst.Zone == ZoneType.Stage || dst.Zone == ZoneType.ClimaxArea;
        if (hideDst) dst.Modulate = new Color(1, 1, 1, 0);

        var card = b.Card ?? CardView.Hidden;
        bool flip = !card.IsHidden && b.FromVisible != b.ToVisible;
        var ghost = new CardControl
        {
            Card = card,
            FaceDown = card.IsHidden || !b.FromVisible,
            CardSize = src.CardSize,
            Angle = src.Angle,
            Position = src.Rect.Position,
            Size = src.Rect.Size,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _fxLayer.AddChild(ghost);

        void Step(float t)
        {
            if (!IsInstanceValid(ghost)) return;
            float lift = 1f + 0.12f * Mathf.Sin(Mathf.Pi * t);
            var size = src.Rect.Size.Lerp(to.Rect.Size, t);
            var center = src.Rect.GetCenter().Lerp(to.Rect.GetCenter(), t) + new Vector2(0, -18 * Mathf.Sin(Mathf.Pi * t));
            ghost.Size = size * lift;
            ghost.Position = center - ghost.Size / 2;
            ghost.CardSize = src.CardSize.Lerp(to.CardSize, t) * lift;
            ghost.Angle = Mathf.Lerp(src.Angle, to.Angle, t);
            ghost.Glow = Mathf.Sin(Mathf.Pi * t) * 0.8f;
            if (flip)
            {
                ghost.Flip = Mathf.Abs(Mathf.Cos(Mathf.Pi * t));
                ghost.FaceDown = t < 0.5f ? !b.FromVisible : !b.ToVisible;
            }
            ghost.QueueRedraw();
        }
        Step(0);

        var tween = ghost.CreateTween();
        if (delay > 0) tween.TweenInterval(delay);
        tween.TweenMethod(Callable.From<float>(Step), 0f, 1f, dur).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tween.TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(dst))
            {
                dst.Modulate = Colors.White;
                Pulse(dst, 0.35f * Factor);
            }
            ghost.QueueFree();
        }));
    }

    /// <summary>落点闪一下光。</summary>
    private static void Pulse(CardControl c, float dur)
    {
        if (dur <= 0) return;
        var t = c.CreateTween();
        t.TweenMethod(Callable.From<float>(v =>
        {
            if (!IsInstanceValid(c)) return;
            c.Glow = v;
            c.QueueRedraw();
        }), 0.9f, 0f, dur);
    }

    private void AnimateOrientation(CardOrientationEvent o, float dur)
    {
        if (!_byKey.TryGetValue((o.Owner, ZoneType.Stage, (int)o.Slot), out var c)) return;
        float from = CardControl.OrientationAngle(o.From), to = CardControl.OrientationAngle(o.To);
        c.AngleOffset = from - to;
        c.QueueRedraw();
        var t = c.CreateTween();
        t.TweenMethod(Callable.From<float>(v =>
        {
            if (!IsInstanceValid(c)) return;
            c.AngleOffset = v;
            c.QueueRedraw();
        }), from - to, 0f, dur).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private void AnimateShuffle(ShuffleEvent s, float dur)
    {
        if (!_byKey.TryGetValue((s.Owner, ZoneType.Deck, -1), out var c)) return;
        var t = c.CreateTween();
        t.TweenMethod(Callable.From<float>(v =>
        {
            if (!IsInstanceValid(c)) return;
            c.AngleOffset = 0.12f * Mathf.Sin(v * Mathf.Pi * 6) * (1 - v);
            c.QueueRedraw();
        }), 0f, 1f, dur);
    }

    private void AnimateAttack(AttackDeclaredEvent a, float dur)
    {
        bool bottom = a.Attacker == Me;
        var from = BoardLayout.Stage(a.AttackerSlot, bottom).GetCenter();
        var to = a.DefenderSlot is { } ds
            ? BoardLayout.Stage(ds, !bottom).GetCenter()
            : BoardLayout.ClockArea(!bottom).GetCenter();
        var color = a.Type switch
        {
            AttackType.Front => new Color("#ff6b5e"),
            AttackType.Side => new Color("#f2c14e"),
            _ => new Color("#c77dff"),
        };
        var arrow = new ArrowFx { From = from, To = to, Color = color, Size = BoardLayout.Size, Label = a.Type switch
        {
            AttackType.Front => "正面攻击",
            AttackType.Side => "侧面攻击",
            _ => "直接攻击",
        } };
        _fxLayer.AddChild(arrow);
        var t = arrow.CreateTween();
        t.TweenMethod(Callable.From<float>(v => { arrow.Progress = v; arrow.QueueRedraw(); }), 0f, 1f, dur * 0.45f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenInterval(dur * 0.3f);
        t.TweenProperty(arrow, "modulate:a", 0f, dur * 0.25f);
        t.TweenCallback(Callable.From(arrow.QueueFree));
        if (_byKey.TryGetValue((a.Attacker, ZoneType.Stage, (int)a.AttackerSlot), out var c)) Pulse(c, dur);
    }

    private void ShowBanner(string text, Color accent, float dur)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0, 0, 0, 0.72f), 0, 0, default, 40, 12));
        var label = UiTheme.Label(text, 30, accent);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        panel.AddChild(label);
        panel.Size = new Vector2(BoardLayout.Size.X, 64);
        panel.Position = new Vector2(0, BoardLayout.CenterY - 32);
        panel.Modulate = new Color(1, 1, 1, 0);
        _fxLayer.AddChild(panel);
        var t = panel.CreateTween();
        t.TweenProperty(panel, "modulate:a", 1f, dur * 0.2f);
        t.TweenInterval(dur * 0.55f);
        t.TweenProperty(panel, "modulate:a", 0f, dur * 0.25f);
        t.TweenCallback(Callable.From(panel.QueueFree));
    }

    private void FlashPhase(float dur)
    {
        _phasePanel.PivotOffset = _phasePanel.Size / 2;
        var t = _phasePanel.CreateTween();
        t.TweenProperty(_phasePanel, "scale", new Vector2(1.15f, 1.15f), dur * 0.4f);
        t.TweenProperty(_phasePanel, "scale", Vector2.One, dur * 0.6f);
    }
}

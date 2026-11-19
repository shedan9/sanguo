using Godot;

namespace Sanguo.Client;

/// <summary>鼠标穿透的金色指向动画，定向牌公开后绘制并淡出。</summary>
public partial class BattleTargetLine : Control
{
    public bool HasLine { get; private set; }
    public bool ReduceMotion { get; set; }
    public double ChoiceRevealDelay => ReduceMotion || _shotRemaining <= 0 ? 0 : Math.Max(0, _delay + .26 - _elapsed);
    private Vector2 _from, _to;
    private Rect2 _targetBounds;
    private double _elapsed, _shotRemaining, _delay;
    private float _opacity;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public void Play(Vector2 from, Rect2 targetBounds, double delay = 0)
    {
        _from = from; _to = targetBounds.GetCenter(); _targetBounds = targetBounds; _shotRemaining = .8; _delay = delay; _elapsed = 0; _opacity = 1; HasLine = delay <= 0;
        QueueRedraw();
    }

    public void ClearLine() { _shotRemaining = 0; HasLine = false; QueueRedraw(); }

    public override void _Process(double delta)
    {
        var wasVisible = HasLine;
        if (_shotRemaining <= 0) return;
        if (_delay > 0)
        {
            _delay -= delta;
            if (_delay > 0) return;
            delta = -_delay;
        }
        if (!ReduceMotion) _elapsed += delta;
        _shotRemaining = Math.Max(0, _shotRemaining - delta);
        _opacity = (float)Math.Min(1, _shotRemaining / .35);
        HasLine = _shotRemaining > 0;
        if (HasLine || wasVisible) QueueRedraw();
    }

    public override void _Draw()
    {
        if (!HasLine || _from.DistanceSquaredTo(_to) < 4) return;
        var from = GetGlobalTransform().AffineInverse() * _from;
        var to = GetGlobalTransform().AffineInverse() * _to;
        if (!ReduceMotion) to = from.Lerp(to, Mathf.Clamp((float)_elapsed / .12f, 0, 1));
        var direction = (to - from).Normalized();
        var normal = direction.Orthogonal();
        DrawLine(from, to, new Color(1f, .61f, .15f, .07f * _opacity), 14, true);
        DrawLine(from, to, new Color(1f, .72f, .23f, .18f * _opacity), 7, true);
        DrawLine(from, to, new Color(1f, .82f, .33f, .75f * _opacity), 2.5f, true);
        DrawLine(from, to, new Color(1f, .97f, .76f, _opacity), 1, true);
        for (var i = 0; i < 3; i++)
        {
            var t = (float)((_elapsed * .85 + i / 3.0) % 1.0);
            var point = from.Lerp(to, t);
            DrawLine(point - direction * Math.Min(18, from.DistanceTo(point)), point, new Color(1, .92f, .52f, .75f * _opacity), 3, true);
            DrawCircle(point, 4, new Color(1, .78f, .24f, .18f * _opacity));
            DrawCircle(point, 1.8f, new Color(1, 1, .84f, _opacity));
        }
        DrawPolyline([to - direction * 12 + normal * 5, to, to - direction * 12 - normal * 5],
            new Color(1, .91f, .58f, _opacity), 2, true);
        if (_shotRemaining > 0)
        {
            var localBounds = GetGlobalTransform().AffineInverse() * _targetBounds;
            var spread = ReduceMotion ? 1f : 1f + 4f * (1f - (float)_shotRemaining / .8f);
            DrawRect(localBounds.Grow(spread), new Color(1, .22f, .09f, .8f * _opacity), false, 2f, true);
        }
        DrawCircle(from, 3, new Color(1, .9f, .45f, .7f * _opacity));
        DrawArc(to, 9, 0, Mathf.Tau, 32, new Color(1, .85f, .35f, .55f * _opacity), 1.5f, true);
    }
}

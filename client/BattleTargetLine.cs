using Godot;

namespace Sanguo.Client;

/// <summary>鼠标穿透的金色指向动画；预选持续显示，出牌反馈短暂保留后淡出。</summary>
public partial class BattleTargetLine : Control
{
    public Func<(Vector2 From, Vector2 To)?>? Preview { get; set; }
    public bool HasLine { get; private set; }
    public bool ReduceMotion { get; set; }
    private Vector2 _from, _to;
    private double _elapsed, _shotRemaining;
    private float _opacity;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public void Play(Vector2 from, Vector2 to)
    {
        _from = from; _to = to; _shotRemaining = .8; _opacity = 1; HasLine = true;
        QueueRedraw();
    }

    public void ClearLine() { _shotRemaining = 0; HasLine = false; QueueRedraw(); }

    public override void _Process(double delta)
    {
        var wasVisible = HasLine;
        if (!ReduceMotion) _elapsed += delta;
        var preview = Preview?.Invoke();
        if (preview is { } points)
        {
            _shotRemaining = 0;
            _from = points.From; _to = points.To; _opacity = .95f; HasLine = true;
        }
        else
        {
            _shotRemaining = Math.Max(0, _shotRemaining - delta);
            _opacity = (float)Math.Min(1, _shotRemaining / .35);
            HasLine = _shotRemaining > 0;
        }
        if (HasLine || wasVisible) QueueRedraw();
    }

    public override void _Draw()
    {
        if (!HasLine || _from.DistanceSquaredTo(_to) < 4) return;
        var from = GetGlobalTransform().AffineInverse() * _from;
        var to = GetGlobalTransform().AffineInverse() * _to;
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
            DrawLine(point - direction * 18, point, new Color(1, .92f, .52f, .75f * _opacity), 3, true);
            DrawCircle(point, 4, new Color(1, .78f, .24f, .18f * _opacity));
            DrawCircle(point, 1.8f, new Color(1, 1, .84f, _opacity));
        }
        DrawPolyline([to - direction * 12 + normal * 5, to, to - direction * 12 - normal * 5],
            new Color(1, .91f, .58f, _opacity), 2, true);
        DrawCircle(from, 3, new Color(1, .9f, .45f, .7f * _opacity));
        DrawArc(to, 9, 0, Mathf.Tau, 32, new Color(1, .85f, .35f, .55f * _opacity), 1.5f, true);
    }
}

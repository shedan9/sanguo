using Godot;

namespace Sanguo.Client;

/// <summary>公开结算的短时粒子、斩痕与扩散光环；不参与输入或规则。</summary>
public partial class BattleImpactEffect : Control
{
    public Color Tint { get; set; } = Colors.White;
    public bool Slash { get; set; }
    private float _age;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= .6f) { QueueFree(); return; }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var t = _age / .6f;
        var color = new Color(Tint, 1 - t);
        DrawArc(Vector2.Zero, 12 + t * 60, 0, Mathf.Tau, 48, new Color(Tint, (1 - t) * .65f), 2, true);
        DrawCircle(Vector2.Zero, (1 - t) * 26, new Color(Tint, (1 - t) * .12f));
        for (var i = 0; i < 12; i++)
        {
            var direction = Vector2.FromAngle(i * Mathf.Tau / 12 + .19f);
            var start = direction * (16 + t * (45 + i % 3 * 12));
            DrawLine(start, start + direction * (1 - t) * 14, color, 2, true);
        }
        if (Slash)
        {
            var extent = 44 * Mathf.Sin(Mathf.Pi * Math.Min(1, t * 1.4f));
            DrawLine(new Vector2(-extent, extent * .7f), new Vector2(extent, -extent * .7f), new Color(Tint, (1 - t) * .3f), 13, true);
            DrawLine(new Vector2(-extent, extent * .7f), new Vector2(extent, -extent * .7f), color, 3, true);
        }
    }
}

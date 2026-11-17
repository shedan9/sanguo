using Godot;

namespace Sanguo.Client;

/// <summary>只读来源目标指向；不参与命令、规则或输入。</summary>
internal partial class BattleTargetLink : Control
{
    public Vector2 Source { get; init; }
    public Vector2 Target { get; init; }

    public override void _Ready() => Resized += QueueRedraw;

    public override void _Draw()
    {
        var from = Source * Size;
        var to = Target * Size;
        var direction = (to - from).Normalized();
        if (from.DistanceTo(to) < 100) return;
        from += direction * 52; to -= direction * 52;
        var color = new Color("C3A66A");
        DrawLine(from, to, color, 2, true);
        DrawCircle(from, 4, color);
        var side = direction.Orthogonal() * 6;
        DrawColoredPolygon([to, to - direction * 14 + side, to - direction * 14 - side], color);
    }
}

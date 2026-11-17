using Godot;

namespace Sanguo.Client;

/// <summary>项目自绘的铜色桌面与刻纹，不使用参照截图的背景图片。</summary>
public partial class BattleTableBackdrop : Control
{
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        var center = Size * new Vector2(.48f, .46f);
        var radius = Size.Y * .40f;
        var dark = new Color("3D2E1C", .18f);
        var light = new Color("F6D5A0", .09f);
        foreach (var r in new[] { .77f, .80f, .96f, 1f, 1.025f })
        {
            DrawArc(center + Vector2.Down * 2, radius * r, 0, Mathf.Tau, 180, light, 2, true);
            DrawArc(center, radius * r, 0, Mathf.Tau, 180, dark, 2, true);
        }
        for (var i = 0; i < 6; i++)
        {
            var angle = i * Mathf.Tau / 6;
            var points = new Vector2[100];
            for (var j = 0; j < points.Length; j++)
            {
                var t = j / 99f;
                var a = angle + t * Mathf.Tau * 1.15f;
                points[j] = center + Vector2.FromAngle(angle) * radius * .43f
                    + Vector2.FromAngle(a) * radius * .28f * (1 - t * .92f);
            }
            DrawPolyline(points.Select(p => p + Vector2.Down * 2).ToArray(), light, 3, true);
            DrawPolyline(points, dark, 3, true);
        }
    }
}

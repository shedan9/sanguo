using System.Collections.Immutable;
using Sanguo.BattleCore.Basic;

namespace Sanguo.Application;

public sealed record BattleSeatPosition(string EntityId, int SeatId, double X, double Y);

/// <summary>开发桌面的归一化周边位置；包含阵亡座次，视角只旋转显示，不修改规则座次。</summary>
public static class BattleSeatLayout
{
    public static ImmutableArray<BattleSeatPosition> Arrange(BattleView view, string focusId)
    {
        var units = view.Combatants.OrderBy(u => u.SeatId).ToArray();
        var focus = Array.FindIndex(units, u => u.EntityId == focusId);
        if (focus < 0) throw new ArgumentException("视角单位不在本场战斗中。", nameof(focusId));
        return Enumerable.Range(0, units.Length).Select(offset =>
        {
            var unit = units[(focus + offset) % units.Length];
            var slot = offset * 8 / units.Length;
            (double x, double y) = slot switch
            {
                0 => (.5, .82), 1 => (.1, .82), 2 => (.1, .5), 3 => (.1, .18),
                4 => (.5, .18), 5 => (.9, .18), 6 => (.9, .5), _ => (.9, .82)
            };
            return new BattleSeatPosition(unit.EntityId, unit.SeatId, x, y);
        }).ToImmutableArray();
    }
}

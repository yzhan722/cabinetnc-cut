using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class ClimbCutTests
{
    static readonly (double X, double Y)[] UnitRect =
        [(0, 0), (10, 0), (10, 4), (0, 4), (0, 0)];

    [Fact]
    public void Outer_climb_is_clockwise()
    {
        var oriented = ClimbCut.OrientClosed(UnitRect, inner: false);
        Assert.True(ClimbCut.SignedArea(oriented) < 0);
    }

    [Fact]
    public void Inner_climb_is_counterclockwise()
    {
        var oriented = ClimbCut.OrientClosed(UnitRect, inner: true);
        Assert.True(ClimbCut.SignedArea(oriented) > 0);
    }

    [Fact]
    public void Starts_on_longest_edge()
    {
        var oriented = ClimbCut.OrientClosed(UnitRect, inner: false);
        var a = oriented[0];
        var b = oriented[1];
        var len0 = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        Assert.True(len0 >= 9.9, $"first edge {len0}");
    }
}

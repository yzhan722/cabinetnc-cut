using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;

namespace CabinetNC.Domain.Tests;

public class RemnantOutlineTests
{
    [Fact]
    public void Rectangle_has_no_blocked_region()
    {
        var pts = new[]
        {
            new Point2(10, 20),
            new Point2(610, 20),
            new Point2(610, 420),
            new Point2(10, 420),
        };

        Assert.True(RemnantOutline.TryBuild(pts, out var shape, out var err), err);
        Assert.NotNull(shape);
        Assert.Equal(600, shape!.WidthMm, 3);
        Assert.Equal(400, shape.LengthMm, 3);
        Assert.True(shape.IsRectangle);
        Assert.Empty(shape.Blocked);
        Assert.Equal(new Point2(0, 0), shape.Outline[0]);
    }

    [Fact]
    public void L_shape_marks_the_missing_corner()
    {
        // 800×800 L, notch is the top-right 400×400.
        var pts = new[]
        {
            new Point2(0, 0),
            new Point2(800, 0),
            new Point2(800, 400),
            new Point2(400, 400),
            new Point2(400, 800),
            new Point2(0, 800),
        };

        Assert.True(RemnantOutline.TryBuild(pts, out var shape, out var err), err);
        Assert.NotNull(shape);
        Assert.False(shape!.IsRectangle);
        var hole = Assert.Single(shape.Blocked);
        Assert.Equal(400, hole.MinX, 3);
        Assert.Equal(400, hole.MinY, 3);
        Assert.Equal(800, hole.MaxX, 3);
        Assert.Equal(800, hole.MaxY, 3);
    }

    [Fact]
    public void Slanted_edge_is_rejected()
    {
        var pts = new[]
        {
            new Point2(0, 0),
            new Point2(400, 0),
            new Point2(400, 200),
            new Point2(0, 300),
        };

        Assert.False(RemnantOutline.TryBuild(pts, out var shape, out var err));
        Assert.Null(shape);
        Assert.Contains("直角", err);
    }

    [Fact]
    public void Self_crossing_is_rejected()
    {
        // Two colinear edges on y=50 overlap — not a simple ring.
        var pts = new[]
        {
            new Point2(0, 0),
            new Point2(100, 0),
            new Point2(100, 50),
            new Point2(30, 50),
            new Point2(30, 20),
            new Point2(70, 20),
            new Point2(70, 50),
            new Point2(0, 50),
        };

        Assert.False(RemnantOutline.TryBuild(pts, out _, out var err));
        Assert.Contains("自交", err);
    }
}

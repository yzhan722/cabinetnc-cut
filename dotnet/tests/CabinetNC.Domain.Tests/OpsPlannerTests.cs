using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class OpsPlannerTests
{
    [Fact]
    public void Contour_and_drill_from_panel()
    {
        var panel = new Panel
        {
            PanelId = "P1",
            Outline = new Outline
            {
                Points = [new(0, 0), new(100, 0), new(100, 50), new(0, 50)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "H1",
                    Kind = "holeVertical",
                    X = 20,
                    Y = 20,
                    DiameterMm = 3,
                    DepthMm = 12,
                },
            ],
        };
        var ops = OpsPlanner.FeaturesToOps([panel]);
        Assert.Equal(2, ops.Count);
        Assert.Contains(ops, o => o.Op == "contour");
        Assert.Contains(ops, o => o.Op == "drill");

        var placed = OpsPlanner.AttachToNest(ops, [
            new NestPlacement { PanelId = "P1", SheetIndex = 0, OffsetX = 10, OffsetY = 5, RotationDeg = 0 },
        ]);
        var drill = placed.First(o => o.Op == "drill");
        Assert.True(drill.Placed);
        Assert.Equal(30, drill.SheetX);
        Assert.Equal(25, drill.SheetY);
    }

    [Fact]
    public void AttachToNest_keeps_four_decimal_sheet_coords()
    {
        var ops = new[]
        {
            new CutOp
            {
                Op = "drill",
                PanelId = "P1",
                FeatureId = "H1",
                Placed = false,
                X = 10.12346,
                Y = 20.56789,
            },
        };
        var placed = OpsPlanner.AttachToNest(ops, [
            new NestPlacement { PanelId = "P1", SheetIndex = 0, OffsetX = 0.11111, OffsetY = 0.22222 },
        ]);
        var drill = Assert.Single(placed);
        Assert.Equal(10.2346, drill.SheetX);
        Assert.Equal(20.7901, drill.SheetY);
    }

    [Fact]
    public void Through_finger_hole_is_one_contour_not_pocket_spiral()
    {
        var panel = new Panel
        {
            PanelId = "LID",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(447, 0), new(447, 277), new(0, 277)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "FINGER",
                    Kind = "holeVertical",
                    X = 223.5,
                    Y = 138.5,
                    DiameterMm = 40,
                    DepthMm = 18,
                    Through = true,
                },
            ],
        };
        var ops = OpsPlanner.FeaturesToOps([panel]);
        var hole = Assert.Single(ops, o => o.FeatureId == "FINGER");
        Assert.Equal("contour", hole.Op);
        Assert.True(hole.Through);
        Assert.True(hole.Path is { Count: >= 3 });
        Assert.Null(hole.PathSegments);
        Assert.Null(hole.FinishLoop);
    }

    [Fact]
    public void Rebate_pocket_is_two_closed_walls_without_outer_retrace()
    {
        var panel = new Panel
        {
            PanelId = "LID",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(447, 0), new(447, 277), new(0, 277)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "REBATE",
                    Kind = "pocket",
                    DepthMm = 9,
                    Path = [new(0, 0), new(447, 0), new(447, 277), new(0, 277)],
                    Holes =
                    [
                        [new(9, 9), new(438, 9), new(438, 268), new(9, 268)],
                    ],
                },
            ],
        };
        var pocket = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.Op == "pocket");
        Assert.Null(pocket.FinishLoop);
        Assert.Equal(2, pocket.PathSegments!.Count);
        Assert.All(pocket.PathSegments, loop =>
        {
            Assert.True(loop.Count >= 4);
            Assert.Equal(loop[0].X, loop[^1].X, 6);
            Assert.Equal(loop[0].Y, loop[^1].Y, 6);
        });
    }

    [Fact]
    public void Wide_pocket_with_island_is_area_clearance_not_two_walls()
    {
        var panel = new Panel
        {
            PanelId = "P",
            ThicknessMm = 16,
            Outline = new Outline
            {
                Points = [new(0, 0), new(200, 0), new(200, 350), new(0, 350)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "PK",
                    Kind = "pocket",
                    DepthMm = 6,
                    Path = [new(0, 0), new(104, 0), new(104, 300), new(0, 300)],
                    Holes =
                    [
                        [new(38, 140), new(66, 140), new(66, 272), new(38, 272)],
                    ],
                },
            ],
        };
        var pocket = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.Op == "pocket");
        Assert.True(pocket.PathSegments!.Count > 2, $"segments={pocket.PathSegments.Count}");
        Assert.True(pocket.Path!.Count > 40, $"pathPts={pocket.Path.Count}");
    }

    [Fact]
    public void Blind_pocket_clears_through_a_through_slot()
    {
        var slot = new Point2[]
        {
            new(46, 140), new(58, 140), new(58, 272), new(46, 272),
        };
        var panel = new Panel
        {
            PanelId = "P",
            ThicknessMm = 16,
            Outline = new Outline
            {
                Points = [new(0, 0), new(200, 0), new(200, 350), new(0, 350)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "PK",
                    Kind = "pocket",
                    DepthMm = 6,
                    Path = [new(0, 0), new(104, 0), new(104, 300), new(0, 300)],
                    Holes = [slot],
                },
                new PanelFeature
                {
                    FeatureId = "SLOT",
                    Kind = "cutout",
                    Through = true,
                    DepthMm = 16,
                    Path = slot,
                },
            ],
        };
        var pocket = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.Op == "pocket");
        Assert.DoesNotContain(pocket.PathSegments!, loop =>
            loop.Max(p => p.X) - loop.Min(p => p.X) < 40
            && loop.Min(p => p.Y) > 120);
        Assert.True(
            CrossesBox(pocket.Path!, 46, 160, 58, 250),
            "clearance must run through a through-slot narrower than 20 mm");
    }

    static bool CrossesBox(
        IReadOnlyList<(double X, double Y)> path,
        double minX, double minY, double maxX, double maxY)
    {
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            var segMinX = Math.Min(a.X, b.X);
            var segMaxX = Math.Max(a.X, b.X);
            var segMinY = Math.Min(a.Y, b.Y);
            var segMaxY = Math.Max(a.Y, b.Y);
            if (segMaxX >= minX && segMinX <= maxX && segMaxY >= minY && segMinY <= maxY)
                return true;
        }
        return false;
    }

    static bool PathEnters(
        IReadOnlyList<(double X, double Y)> path,
        double minX, double minY, double maxX, double maxY) =>
        path.Any(p => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY);

    [Fact]
    public void Blind_pocket_keeps_a_pad_when_a_thin_slot_sits_on_it()
    {
        var stadium = new Point2[]
        {
            new(20, 40), new(84, 40), new(84, 280), new(20, 280),
        };
        var slot = new Point2[]
        {
            new(46, 50), new(58, 50), new(58, 270), new(46, 270),
        };
        var panel = new Panel
        {
            PanelId = "P",
            ThicknessMm = 16,
            Outline = new Outline
            {
                Points = [new(0, 0), new(200, 0), new(200, 320), new(0, 320)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "PK",
                    Kind = "pocket",
                    DepthMm = 6,
                    Path = [new(0, 0), new(104, 0), new(104, 300), new(0, 300)],
                    Holes = [stadium],
                },
                new PanelFeature
                {
                    FeatureId = "SLOT",
                    Kind = "cutout",
                    Through = true,
                    DepthMm = 16,
                    Path = slot,
                },
            ],
        };
        var pocket = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.Op == "pocket");
        Assert.False(
            PathEnters(pocket.Path!, 30, 55, 74, 265),
            "full-thickness pad stays; thin through-slot is ignored");
    }

    [Fact]
    public void Stepped_window_is_ring_clear_not_floor_fill()
    {
        var window = new Point2[]
        {
            new(19, 19), new(201, 19), new(201, 141), new(19, 141),
        };
        var panel = new Panel
        {
            PanelId = "P",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(220, 0), new(220, 160), new(0, 160)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "STEP",
                    Kind = "pocket",
                    DepthMm = 9,
                    Path = [new(10, 10), new(210, 10), new(210, 150), new(10, 150)],
                    Holes = [window],
                },
                new PanelFeature
                {
                    FeatureId = "WIN",
                    Kind = "cutout",
                    Through = true,
                    DepthMm = 18,
                    Path = window,
                },
            ],
        };
        var pocket = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.Op == "pocket");
        Assert.Equal(2, pocket.PathSegments!.Count);
        Assert.False(
            PathEnters(pocket.Path!, 70, 55, 150, 105),
            "wide through window is avoided; only the step is cleared");
    }

    [Fact]
    public void Rebate_island_stays_when_a_through_hole_sits_on_the_pad()
    {
        var panel = new Panel
        {
            PanelId = "LID",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(447, 0), new(447, 277), new(0, 277)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "REBATE",
                    Kind = "pocket",
                    DepthMm = 9,
                    Path = [new(0, 0), new(447, 0), new(447, 277), new(0, 277)],
                    Holes =
                    [
                        [new(9, 9), new(438, 9), new(438, 268), new(9, 268)],
                    ],
                },
                new PanelFeature
                {
                    FeatureId = "FINGER",
                    Kind = "holeVertical",
                    X = 223.5,
                    Y = 138.5,
                    DiameterMm = 40,
                    DepthMm = 18,
                    Through = true,
                },
            ],
        };
        var pocket = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.Op == "pocket");
        Assert.Equal(2, pocket.PathSegments!.Count);
    }

    [Fact]
    public void Blind_pocket_clears_through_a_through_groove()
    {
        var slot = new Point2[]
        {
            new(46, 40), new(58, 40), new(58, 260), new(46, 260),
        };
        var panel = new Panel
        {
            PanelId = "P",
            ThicknessMm = 16,
            Outline = new Outline
            {
                Points = [new(0, 0), new(200, 0), new(200, 320), new(0, 320)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "PK",
                    Kind = "pocket",
                    DepthMm = 6,
                    Path = [new(0, 0), new(104, 0), new(104, 300), new(0, 300)],
                    Holes = [slot],
                },
                new PanelFeature
                {
                    FeatureId = "G1",
                    Kind = "grooveVertical",
                    Through = true,
                    DepthMm = 16,
                    WidthMm = 12,
                    Path = [new(52, 40), new(52, 260)],
                    Profile = slot,
                },
            ],
        };
        var pocket = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.Op == "pocket");
        Assert.True(
            CrossesBox(pocket.Path!, 46, 80, 58, 220),
            "clearance must run through the through-groove");
    }
}

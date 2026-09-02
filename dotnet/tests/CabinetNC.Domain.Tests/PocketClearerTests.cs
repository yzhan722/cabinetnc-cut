using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class PocketClearerTests
{
    static IReadOnlyList<(double X, double Y)> Rect(double w, double h) =>
        [(0, 0), (w, 0), (w, h), (0, h)];

    [Fact]
    public void Clear_uses_spiral_not_horizontal_raster()
    {
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = Rect(120, 80),
            ToolDiameterMm = 6.35,
            StepoverMm = 4,
        });
        Assert.True(result.Segments.Count >= 1);
        var fill = result.Segments[0];
        var turn = 0;
        for (var i = 2; i < fill.Count; i++)
        {
            var ax = fill[i - 1].X - fill[i - 2].X;
            var ay = fill[i - 1].Y - fill[i - 2].Y;
            var bx = fill[i].X - fill[i - 1].X;
            var by = fill[i].Y - fill[i - 1].Y;
            if (ax * by - ay * bx is > 0.05 or < -0.05)
                turn++;
        }
        Assert.True(turn >= 8, $"spiral turns={turn} pts={fill.Count}");
    }

    [Fact]
    public void Clear_path_has_many_more_points_than_boundary()
    {
        var boundary = Rect(120, 80);
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = boundary,
            ToolDiameterMm = 6.35,
            StepoverMm = 3,
        });
        Assert.True(result.PassCount >= 3, $"passCount={result.PassCount}");
        Assert.True(result.Path.Count >= boundary.Count * 3,
            $"pathPts={result.Path.Count} boundary={boundary.Count}");
    }

    [Fact]
    public void Smaller_stepover_makes_longer_path()
    {
        var outline = Rect(100, 60);
        var coarse = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = outline, ToolDiameterMm = 6, StepoverMm = 5,
        });
        var fine = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = outline, ToolDiameterMm = 6, StepoverMm = 2,
        });
        Assert.True(fine.Path.Count > coarse.Path.Count);
        Assert.True(fine.PassCount > coarse.PassCount);
    }

    [Fact]
    public void FeaturesToOps_pocket_is_not_boundary_only()
    {
        var panel = new Panel
        {
            PanelId = "P",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(200, 0), new(200, 150), new(0, 150)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "PK1",
                    Kind = "pocket",
                    DepthMm = 6,
                    Path = [new(20, 20), new(120, 20), new(120, 90), new(20, 90)],
                },
            ],
        };
        var ops = OpsPlanner.FeaturesToOps([panel]);
        var pocket = Assert.Single(ops, o => o.Op == "pocket");
        Assert.True(pocket.Path!.Count > 8, $"pocket path pts={pocket.Path.Count}");
    }

    [Fact]
    public void Thirty_five_mm_hinge_spiral_cuts_directly_to_size_without_finish_loop()
    {
        var panel = new Panel
        {
            PanelId = "P",
            ThicknessMm = 16,
            Outline = new Outline
            {
                Points = [new(0, 0), new(200, 0), new(200, 150), new(0, 150)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "HINGE",
                    Kind = "holeVertical",
                    X = 80,
                    Y = 60,
                    DiameterMm = 35,
                    DepthMm = 12,
                },
            ],
        };

        var op = Assert.Single(OpsPlanner.FeaturesToOps([panel]), o => o.FeatureId == "HINGE");
        Assert.Equal("T2", op.ToolId);
        Assert.Null(op.FinishLoop);
        var spiral = Assert.Single(op.PathSegments!);
        var toolCenterDiameter = Math.Max(
            spiral.Max(p => p.X) - spiral.Min(p => p.X),
            spiral.Max(p => p.Y) - spiral.Min(p => p.Y));
        Assert.InRange(toolCenterDiameter + 10, 34.95, 35.05);
        var final = spiral[^1];
        Assert.True(
            spiral.Count(p => Math.Abs(p.X - final.X) < 1e-6
                && Math.Abs(p.Y - final.Y) < 1e-6) >= 2,
            "outer clearance ring must close without a separate finish pass");
    }

    [Fact]
    public void Small_panel_does_not_warn_in_preflight()
    {
        var panel = new Panel
        {
            PanelId = "S",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(50, 0), new(50, 40), new(0, 40)],
            },
        };
        var ops = OpsPlanner.FeaturesToOps([panel])
            .Select(o => o with { Placed = true, Path = [(1, 1), (2, 1), (2, 2)] })
            .ToList();
        var report = NcPreflight.Check(
            ops,
            Machines.MachineCatalog.Get("nesting_router_6"),
            1220, 2440,
            new Dictionary<string, Panel> { ["S"] = panel });
        Assert.DoesNotContain(report.Issues, i => i.Code == "small_panel");
    }

    [Fact]
    public void Clear_ring_pocket_follows_both_walls()
    {
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = Rect(90, 70),
            Holes = [[(9, 9), (81, 9), (81, 61), (9, 61)]],
            ToolDiameterMm = 6.35,
        });
        Assert.False(result.TooSmallForTool);
        Assert.True(result.Segments.Count >= 2, $"segments={result.Segments.Count}");
        Assert.True(result.Path.Count > 8);
        Assert.Null(result.FinishLoop);
        Assert.All(result.Segments, loop =>
        {
            Assert.True(loop.Count >= 4);
            Assert.Equal(loop[0].X, loop[^1].X, 6);
            Assert.Equal(loop[0].Y, loop[^1].Y, 6);
        });
        var outerEnd = result.Segments[0][^1];
        var innerStart = result.Segments[1][0];
        var dx = innerStart.X - outerEnd.X;
        var dy = innerStart.Y - outerEnd.Y;
        var link = Math.Sqrt(dx * dx + dy * dy);
        Assert.True(link < 15, $"stay-down link {link:0.###} mm should stay in the rebate band");
    }

    [Fact]
    public void Wide_pocket_with_island_area_clears_the_floor()
    {
        // Tall pocket, island only in the upper half — same family as the
        // lock/handle rebate that was emitting two walls + a diagonal link.
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = Rect(104, 300),
            Holes = [[(38, 140), (66, 140), (66, 272), (38, 272)]],
            ToolDiameterMm = 10,
        });
        Assert.False(result.TooSmallForTool);
        Assert.True(result.Segments.Count > 2, $"segments={result.Segments.Count} (need fill + walls)");
        Assert.True(result.Path.Count > 40, $"pathPts={result.Path.Count}");

        var walls = result.Segments[^2];
        var island = result.Segments[^1];
        Assert.True(walls.Max(p => p.X) - walls.Min(p => p.X) > 80);
        Assert.True(island.Max(p => p.X) - island.Min(p => p.X) < 40);

        var floor = result.Segments.Take(result.Segments.Count - 2).SelectMany(s => s).ToList();
        Assert.Contains(floor, p => p.Y < 80);
        Assert.Contains(floor, p => p.X is > 20 and < 84 && p.Y is > 20 and < 120);
    }

    [Fact]
    public void Thin_tee_slot_is_one_wall_not_sliver_plus_retrace()
    {
        // Fridge B3 T-slot at tool centre: ~19 mm bar / stems. Onion used
        // to keep a 1 mm leftover and then FinishLoop retraced the T.
        (double X, double Y)[] tee =
        [
            (64.5, 563.5), (105.2, 563.5), (105.2, 442.5), (125.2, 442.5), (125.2, 563.5),
            (566.2, 563.5), (566.2, 442.5), (586.2, 442.5), (586.2, 563.5),
            (641.5, 563.5), (641.5, 578), (64.5, 578),
        ];
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = tee,
            ToolDiameterMm = 10,
            OnionSkinMm = 0.5,
        });
        Assert.False(result.TooSmallForTool);
        Assert.Null(result.FinishLoop);
        var loop = Assert.Single(result.Segments);
        Assert.True(loop.Count >= 8, $"pts={loop.Count}");
        Assert.Equal(loop[0].X, loop[^1].X, 5);
        Assert.Equal(loop[0].Y, loop[^1].Y, 5);
        var downRight = 0;
        for (var i = 1; i < loop.Count; i++)
        {
            if (loop[i - 1].X is > 560 and < 590
                && loop[i].X is > 560 and < 590
                && loop[i].Y < loop[i - 1].Y - 50)
                downRight++;
        }
        Assert.Equal(1, downRight);
    }

    [Fact]
    public void Slot_opening_to_panel_edges_runs_off_the_board()
    {
        var outline = Rect(400, 16);
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = outline,
            ToolDiameterMm = 10,
            OnionSkinMm = 0.5,
            PanelBounds = new LocalBounds(0, 0, 400, 200),
        });
        Assert.False(result.TooSmallForTool);
        var xs = result.Path.Select(p => p.X).ToList();
        Assert.True(xs.Min() <= -1, $"left {xs.Min():0.##} should run off the board");
        Assert.True(xs.Max() >= 401, $"right {xs.Max():0.##} should run off the board");
    }
}

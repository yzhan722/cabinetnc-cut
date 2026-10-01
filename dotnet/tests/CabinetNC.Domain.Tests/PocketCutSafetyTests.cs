using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;

namespace CabinetNC.Domain.Tests;

public class PocketCutSafetyTests
{
    static IReadOnlyList<(double X, double Y)> Rect(double x0, double y0, double x1, double y1) =>
        [(x0, y0), (x1, y0), (x1, y1), (x0, y1)];

    [Fact]
    public void Near_edge_closed_pocket_does_not_run_off_the_board()
    {
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = Rect(1, 20, 50, 80),
            ToolDiameterMm = 6.35,
            PanelBounds = new LocalBounds(0, 0, 200, 150),
        });
        Assert.False(result.TooSmallForTool);
        Assert.True(result.Path.Min(p => p.X) > -0.1, $"minX={result.Path.Min(p => p.X):0.###}");
        Assert.InRange(result.Path.Min(p => p.X), 3.0, 5.0);
    }

    [Fact]
    public void Mid_height_slot_on_panel_ends_still_runs_off()
    {
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = Rect(0.25, 20, 399.75, 36),
            ToolDiameterMm = 10,
            PanelBounds = new LocalBounds(0, 0, 400, 200),
        });
        Assert.False(result.TooSmallForTool);
        Assert.True(result.Path.Min(p => p.X) <= -1, $"left {result.Path.Min(p => p.X):0.##}");
        Assert.True(result.Path.Max(p => p.X) >= 401, $"right {result.Path.Max(p => p.X):0.##}");
    }

    [Fact]
    public void Island_staydown_links_do_not_enter_the_pad()
    {
        var island = Rect(38, 140, 66, 272);
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = Rect(0, 0, 104, 300),
            Holes = [island],
            ToolDiameterMm = 10,
        });
        Assert.False(result.TooSmallForTool);

        var hits = 0;
        for (var i = 1; i < result.Path.Count; i++)
        {
            if (SegmentEntersInset(result.Path[i - 1], result.Path[i], island, inset: 3))
                hits++;
        }
        Assert.Equal(0, hits);
    }

    [Fact]
    public void U_pocket_clears_both_arms()
    {
        (double X, double Y)[] u =
        [
            (0, 0), (100, 0), (100, 80), (80, 80), (80, 20), (20, 20), (20, 80), (0, 80),
        ];
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = u,
            ToolDiameterMm = 6.35,
            StepoverMm = 4,
        });
        Assert.False(result.TooSmallForTool);
        Assert.True(MinDistToPath((10, 50), result.Path) <= 3.6);
        Assert.True(MinDistToPath((90, 50), result.Path) <= 3.6);
        Assert.True(MinDistToPath((50, 10), result.Path) <= 3.6);
    }

    [Fact]
    public void Pinched_pocket_clears_both_lobes()
    {
        // Two 80×80 rooms joined by a 20 mm neck. After a few insets the
        // neck pinches off; Largest used to keep one room and leave a floor
        // strip in the other.
        (double X, double Y)[] peanut =
        [
            (0, 0), (80, 0), (80, 30), (100, 30), (100, 0), (180, 0),
            (180, 80), (100, 80), (100, 50), (80, 50), (80, 80), (0, 80),
        ];
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = peanut,
            ToolDiameterMm = 6.35,
            StepoverMm = 4,
        });
        Assert.False(result.TooSmallForTool);
        Assert.True(MinDistToPath((40, 40), result.Path) <= 3.6, "left lobe leftover");
        Assert.True(MinDistToPath((140, 40), result.Path) <= 3.6, "right lobe leftover");
    }

    [Fact]
    public void Default_stepover_covers_the_floor()
    {
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = Rect(0, 0, 120, 80),
            ToolDiameterMm = 6.35,
        });
        Assert.False(result.TooSmallForTool);
        foreach (var s in new (double X, double Y)[]
        {
            (60, 40), (20, 20), (100, 60), (10, 40), (110, 40), (60, 10), (60, 70),
        })
        {
            var d = MinDistToPath(s, result.Path);
            Assert.True(d <= 3.55, $"uncut sample ({s.X},{s.Y}) dist={d:0.###}");
        }
    }

    static bool SegmentEntersInset(
        (double X, double Y) a,
        (double X, double Y) b,
        IReadOnlyList<(double X, double Y)> rect,
        double inset)
    {
        var minX = rect.Min(p => p.X) + inset;
        var maxX = rect.Max(p => p.X) - inset;
        var minY = rect.Min(p => p.Y) + inset;
        var maxY = rect.Max(p => p.Y) - inset;
        if (maxX <= minX || maxY <= minY) return false;
        const int n = 8;
        for (var i = 1; i < n; i++)
        {
            var t = i / (double)n;
            var x = a.X + (b.X - a.X) * t;
            var y = a.Y + (b.Y - a.Y) * t;
            if (x > minX && x < maxX && y > minY && y < maxY)
                return true;
        }
        return false;
    }

    static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double MinDistToPath((double X, double Y) p, IReadOnlyList<(double X, double Y)> path)
    {
        var best = double.PositiveInfinity;
        for (var i = 1; i < path.Count; i++)
        {
            var d = PointSeg(p, path[i - 1], path[i]);
            if (d < best) best = d;
        }
        return best;
    }

    static double PointSeg((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Dist(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Dist(p, (a.X + t * dx, a.Y + t * dy));
    }
}

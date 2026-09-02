using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class PocketNcSegmentAuditTests
{
    [Fact]
    public void PocketClearer_spiral_fill_then_finish_loop()
    {
        var outline = new (double X, double Y)[]
        {
            (0, 0), (200, 0), (200, 100), (0, 100),
        };
        var result = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = outline,
            ToolDiameterMm = 6.35,
            StepoverMm = 8,
            OnionSkinMm = 0.5,
        });
        Assert.True(result.Segments.Count >= 1, $"segments={result.Segments.Count}");
        var fill = result.Segments[0];
        Assert.True(fill.Count >= 8, $"spiral pts={fill.Count}");
        var horiz = 0;
        var edges = 0;
        for (var i = 1; i < fill.Count; i++)
        {
            var dx = Math.Abs(fill[i].X - fill[i - 1].X);
            var dy = Math.Abs(fill[i].Y - fill[i - 1].Y);
            if (dx + dy < 0.05) continue;
            edges++;
            if (dy < 0.25 && dx > 1) horiz++;
        }
        Assert.True(edges > 0);
        Assert.True(horiz < edges * 0.85, $"raster-like horiz={horiz}/{edges}");
        Assert.NotNull(result.FinishLoop);
        Assert.True(result.FinishLoop!.Count >= 3);
    }

    [Fact]
    public void NcEmitter_pocket_stays_down_between_segments_and_does_not_close_zigzag()
    {
        var segments = new IReadOnlyList<(double X, double Y)>[]
        {
            new (double X, double Y)[] { (10, 10), (50, 10) },
            new (double X, double Y)[] { (50, 20), (10, 20) },
        };
        var finish = new (double X, double Y)[] { (12, 12), (48, 12), (48, 28), (12, 28), (12, 12) };
        var op = new CutOp
        {
            Op = "pocket",
            PanelId = "P1",
            FeatureId = "PK1",
            ToolId = "T1",
            Placed = true,
            DepthMm = 6,
            PathSegments = segments,
            FinishLoop = finish,
            ClosePath = false,
            Path = segments.SelectMany(s => s).Concat(finish).ToList(),
        };
        var nc = NcEmitter.OpsToNc([op], MachineCatalog.Get("nesting_router_6"));
        var normalized = nc.Replace("\r\n", "\n");
        // Same pocket: feed at cut Z between walls (Carveco). Do not SafeZ.
        Assert.Contains("G1 X50 Y20", normalized);
        Assert.DoesNotContain("G0 X50 Y20", normalized);
        Assert.Contains("(pocket", normalized);
        var beforeFinish = normalized.Contains("(finish")
            ? normalized.Split("(finish")[0]
            : normalized;
        Assert.DoesNotContain("G1 X10 Y10", beforeFinish);
    }
}

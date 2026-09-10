using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;
using Xunit.Abstractions;

namespace CabinetNC.Domain.Tests;

/// <summary>
/// Property test for the recut path: whatever the Troy post emits for a set of panels,
/// <see cref="NcReverse"/> must recover the same panels. Sizes, windows and holes are
/// randomised (fixed seed) so pass ordering, entry corners and ramp-range side lengths
/// are all exercised instead of one hand-picked rectangle.
/// </summary>
public class NcReverseRoundTripTests(ITestOutputHelper output)
{
    const int Cases = 40;
    const int Seed = 20260902;
    const double ToolRadiusMm = 5;
    const double SizeTolMm = 2.0;

    static MachineProfile Machine() => MachineCatalog.Get("nesting_router_6");

    sealed record Window(double X, double Y, double W, double H);

    sealed record Part(string Id, double X, double Y, double W, double H, Window? Window, (double X, double Y)? Hole);

    [Fact]
    public void Emitted_programs_reverse_into_the_same_panels()
    {
        var rng = new Random(Seed);
        var failures = new List<string>();
        for (var i = 0; i < Cases; i++)
        {
            var parts = RandomJob(rng);
            var nc = Emit(parts);
            var result = NcReverse.FromText(nc);
            var problems = Compare(parts, result).ToList();
            if (problems.Count > 0)
                failures.Add($"case {i} [{string.Join("; ", parts.Select(Describe))}]:\n    " + string.Join("\n    ", problems));
        }

        output.WriteLine($"{Cases} random jobs round-tripped, {failures.Count} failing");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Recut_package_keeps_every_recovered_panel_once()
    {
        var rng = new Random(Seed + 1);
        for (var i = 0; i < 10; i++)
        {
            var parts = RandomJob(rng);
            var result = NcReverse.FromText(Emit(parts));
            var pkg = NcReverse.ToPackage(result, "recut-" + i);
            Assert.Equal(parts.Count, pkg.Panels.Count);
            Assert.All(pkg.Panels, p => Assert.Equal(1, p.Quantity));
            Assert.Equal(pkg.Panels.Count, pkg.Panels.Select(p => p.PanelId).Distinct().Count());
        }
    }

    static IEnumerable<string> Compare(List<Part> parts, NcReverseResult result)
    {
        if (result.Panels.Count != parts.Count)
        {
            yield return $"expected {parts.Count} panels, reverse found {result.Panels.Count} (ops: {string.Join(",", result.Ops.Select(o => o.Op))})";
            yield break;
        }

        var expected = parts.OrderBy(p => p.W).ThenBy(p => p.H).ToList();
        var actual = result.Panels
            .Select(p => (Panel: p, W: Width(p), H: Height(p)))
            .OrderBy(p => p.W).ThenBy(p => p.H)
            .ToList();

        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            if (Math.Abs(a.W - e.W) > SizeTolMm || Math.Abs(a.H - e.H) > SizeTolMm)
                yield return $"{e.Id}: size {e.W}×{e.H} recovered as {a.W:0.#}×{a.H:0.#}";

            var windows = a.Panel.Features.Where(f => f.Kind == "cutout").ToList();
            var holes = a.Panel.Features.Where(f => f.Kind.Contains("hole", StringComparison.OrdinalIgnoreCase)).ToList();
            if (windows.Count != (e.Window is null ? 0 : 1))
                yield return $"{e.Id}: expected {(e.Window is null ? 0 : 1)} window(s), recovered {windows.Count}";
            if (holes.Count != (e.Hole is null ? 0 : 1))
                yield return $"{e.Id}: expected {(e.Hole is null ? 0 : 1)} hole(s), recovered {holes.Count}";

            if (e.Window is { } win && windows.Count == 1 && windows[0].Path is { Count: >= 3 } wp)
            {
                var ww = wp.Max(p => p.X) - wp.Min(p => p.X);
                var wh = wp.Max(p => p.Y) - wp.Min(p => p.Y);
                if (Math.Abs(ww - win.W) > SizeTolMm || Math.Abs(wh - win.H) > SizeTolMm)
                    yield return $"{e.Id}: window {win.W}×{win.H} recovered as {ww:0.#}×{wh:0.#} (cutter-centre loop not expanded?)";
                var wx = wp.Min(p => p.X);
                var wy = wp.Min(p => p.Y);
                if (Math.Abs(wx - win.X) > SizeTolMm || Math.Abs(wy - win.Y) > SizeTolMm)
                    yield return $"{e.Id}: window origin ({win.X},{win.Y}) recovered at ({wx:0.#},{wy:0.#})";
            }

            if (e.Hole is { } hole && holes.Count == 1)
            {
                if (Math.Abs(holes[0].X - hole.X) > SizeTolMm || Math.Abs(holes[0].Y - hole.Y) > SizeTolMm)
                    yield return $"{e.Id}: hole ({hole.X},{hole.Y}) recovered at ({holes[0].X:0.#},{holes[0].Y:0.#})";
            }
        }
    }

    static List<Part> RandomJob(Random rng)
    {
        var count = rng.Next(1, 5);
        var parts = new List<Part>();
        var usedWidths = new HashSet<int>();
        double x = 20, y = 20, rowH = 0;
        for (var i = 0; i < count; i++)
        {
            int w;
            do w = rng.Next(60, 601); while (!usedWidths.Add(w));
            var h = rng.Next(50, 401);
            if (x + w > 2400) { x = 20; y += rowH + 30; rowH = 0; }

            Window? window = null;
            if (w >= 120 && h >= 100 && rng.NextDouble() < 0.6)
            {
                // Sides deliberately inside the 8–80 mm "ramp" range the old StripRamp peeled.
                var ww = rng.Next(16, Math.Min(81, w - 60));
                var wh = rng.Next(16, Math.Min(81, h - 50));
                window = new Window(w - ww - 20, h - wh - 20, ww, wh);
            }

            (double X, double Y)? hole = null;
            if (w >= 80 && h >= 70 && rng.NextDouble() < 0.6)
                hole = (20 + rng.Next(0, 10), 20 + rng.Next(0, 10));

            parts.Add(new Part($"P{i + 1}", x, y, w, h, window, hole));
            x += w + 30;
            rowH = Math.Max(rowH, h);
        }
        return parts;
    }

    static string Emit(List<Part> parts)
    {
        var ops = new List<CutOp>();
        foreach (var p in parts)
        {
            if (p.Hole is { } hole)
            {
                ops.Add(new CutOp
                {
                    Op = "drill", PanelId = p.Id, FeatureId = p.Id + "-H1", ToolId = "T3", Placed = true,
                    SheetX = p.X + hole.X, SheetY = p.Y + hole.Y,
                    DiameterMm = 3, DepthMm = 18, ThicknessMm = 18, Through = true,
                });
            }
            if (p.Window is { } w)
            {
                ops.Add(new CutOp
                {
                    Op = "contour", PanelId = p.Id, FeatureId = p.Id + "-CUT", ToolId = "T2", Placed = true,
                    ClosePath = true, Through = true, ThicknessMm = 18, DepthMm = 18.5,
                    Path = [(p.X + w.X, p.Y + w.Y), (p.X + w.X + w.W, p.Y + w.Y), (p.X + w.X + w.W, p.Y + w.Y + w.H), (p.X + w.X, p.Y + w.Y + w.H)],
                });
            }
            ops.Add(new CutOp
            {
                Op = "contour", PanelId = p.Id, ToolId = "T2", Placed = true,
                ClosePath = true, Through = true, ThicknessMm = 18, DepthMm = 18.5,
                Path = [(p.X, p.Y), (p.X + p.W, p.Y), (p.X + p.W, p.Y + p.H), (p.X, p.Y + p.H)],
            });
        }
        var offset = ContourToolOffset.Apply(ops, ToolRadiusMm);
        return NcEmitter.OpsToNc(offset, Machine(), recipe: PostRecipe.TroyDefault());
    }

    static double Width(Panel p) => p.Outline.Points.Max(q => q.X) - p.Outline.Points.Min(q => q.X);
    static double Height(Panel p) => p.Outline.Points.Max(q => q.Y) - p.Outline.Points.Min(q => q.Y);

    static string Describe(Part p) =>
        $"{p.Id} {p.W}×{p.H}@({p.X},{p.Y})" + (p.Window is { } w ? $" win {w.W}×{w.H}" : "") + (p.Hole is not null ? " hole" : "");
}

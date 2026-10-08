using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class LockSlotGeometryTests
{
    static readonly Point2[] SharpLockSized =
    [
        new(0, 0),
        new(55, 0),
        new(55, 15.5),
        new(0, 15.5),
    ];

    [Fact]
    public void EnsureStadium_keeps_sharp_quad_without_lock_intent()
    {
        var kept = LockSlotGeometry.EnsureStadium(SharpLockSized);

        Assert.Equal(4, kept.Count);
    }

    [Fact]
    public void EnsureStadium_upgrades_sharp_quad_when_lock_tagged()
    {
        var stadium = LockSlotGeometry.EnsureStadium(SharpLockSized, purpose: "lock_cutout");

        Assert.True(stadium.Count > 8);
        Assert.Equal(0, stadium.Min(p => p.X), 3);
        Assert.Equal(55, stadium.Max(p => p.X), 3);
        Assert.Equal(0, stadium.Min(p => p.Y), 3);
        Assert.Equal(15.5, stadium.Max(p => p.Y), 3);
    }

    [Fact]
    public void EnsureStadium_upgrades_sharp_quad_when_hasArc_without_tag()
    {
        var stadium = LockSlotGeometry.EnsureStadium(SharpLockSized, hasArc: true);

        Assert.True(stadium.Count > 8);
    }

    [Fact]
    public void EnsureStadium_keeps_already_tessellated_stadium()
    {
        var points = LockSlotGeometry.CapsuleFromAabb(0, 55, 0, 15.5);
        Assert.True(points.Count > 8);

        var kept = LockSlotGeometry.EnsureStadium(points, purpose: "lock_cutout");

        Assert.Same(points, kept);
    }

    [Fact]
    public void Fifty_five_by_sixteen_stadium_offset_keeps_cut_size()
    {
        var stadium = LockSlotGeometry.CapsuleFromAabb(0, 55, 0, 16);
        Assert.Equal(55, stadium.Max(p => p.X) - stadium.Min(p => p.X), 3);
        Assert.Equal(16, stadium.Max(p => p.Y) - stadium.Min(p => p.Y), 3);

        var op = new CutOp
        {
            Op = "contour",
            PanelId = "P",
            FeatureId = "LOCK",
            Path = stadium.Select(p => (p.X, p.Y)).ToList(),
            ClosePath = true,
            Through = true,
            Placed = true,
        };
        var offset = ContourToolOffset.Apply([op], 5)[0].Path!;
        var cx = offset.Max(p => p.X) - offset.Min(p => p.X);
        var cy = offset.Max(p => p.Y) - offset.Min(p => p.Y);
        var cutW = cy + 10;
        var cutL = cx + 10;

        Assert.InRange(cutW, 15.8, 16.2);
        Assert.InRange(cutL, 54.5, 55.5);
        Assert.True(
            PolylineArcFit.Fit(offset, closed: true).Count(s => s.Arc) >= 2,
            "lock ends must stay arcs, not be flattened to chords");
        var withCad = new CutOp
        {
            Op = "contour",
            PanelId = "P",
            FeatureId = "LOCK",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            Path = stadium.Select(p => (p.X, p.Y)).ToList(),
            CadPath = LockSlotGeometry.CapsuleSegments(0, 55, 0, 16),
        };
        var nc = NcEmitter.OpsToNc(
            ContourToolOffset.Apply([withCad], 5),
            MachineCatalog.Get("nesting_router_6"),
            recipe: PostRecipe.TroyDefault());
        Assert.Contains("R3.0000", nc);
        Assert.DoesNotContain("R13.", nc);
    }

    /// <summary>
    /// Exact lock ring from Club Lounge / 22 Ensuite snapshot (Component927).
    /// Fusion and the snapshot are 55×16; S2 ANC cut ~53.8.
    /// </summary>
    static readonly (double X, double Y)[] SnapshotLock927 =
    [
        (35.25, 110.5), (35.642, 112.972), (36.778, 115.202), (38.548, 116.972),
        (40.778, 118.108), (43.25, 118.5), (45.722, 118.108), (47.952, 116.972),
        (49.722, 115.202), (50.858, 112.972), (51.25, 110.5), (51.25, 71.5),
        (50.858, 69.028), (49.722, 66.798), (47.952, 65.028), (45.722, 63.892),
        (43.25, 63.5), (40.778, 63.892), (38.548, 65.028), (36.778, 66.798),
        (35.642, 69.028), (35.25, 71.5),
    ];

    [Fact]
    public void Snapshot_lock_927_is_55_by_16()
    {
        var w = SnapshotLock927.Max(p => p.X) - SnapshotLock927.Min(p => p.X);
        var h = SnapshotLock927.Max(p => p.Y) - SnapshotLock927.Min(p => p.Y);
        Assert.Equal(16, w, 3);
        Assert.Equal(55, h, 3);
    }

    [Fact]
    public void Snapshot_lock_offset_is_exact_stadium_not_sloped_seam()
    {
        var op = new CutOp
        {
            Op = "contour",
            PanelId = "P",
            FeatureId = "LOCK",
            Path = SnapshotLock927.ToList(),
            ClosePath = true,
            Through = true,
            Placed = true,
        };
        var offset = ContourToolOffset.Apply([op], 5)[0].Path!;
        var cut = CutSize(offset);

        Assert.InRange(cut.L, 54.99, 55.01);
        Assert.InRange(cut.W, 15.99, 16.01);
        var fitted = PolylineArcFit.Fit(offset, closed: true);
        Assert.True(fitted.Count(s => s.Arc) == 2,
            $"expected 2 arcs: " + string.Join(" | ",
                fitted.Select(s => $"{(s.Arc ? "A" : "L")} ({s.X:0.###},{s.Y:0.###}) R{s.R:0.###}")));
        Assert.All(fitted.Where(s => s.Arc), s => Assert.InRange(s.R, 2.99, 3.01));
        var current = offset[0];
        var lineCount = 0;
        foreach (var seg in fitted)
        {
            if (!seg.Arc)
            {
                lineCount++;
                Assert.True(
                    Math.Abs(seg.X - current.X) < 1e-6
                    || Math.Abs(seg.Y - current.Y) < 1e-6,
                    $"stadium straight is sloped: ({current.X},{current.Y})→({seg.X},{seg.Y})");
            }
            current = (seg.X, seg.Y);
        }
        Assert.True(lineCount == 2,
            $"expected 2 straights, got {lineCount}: "
            + string.Join(" | ", fitted.Select(s => $"{(s.Arc ? "A" : "L")} ({s.X:0.###},{s.Y:0.###}) R{s.R:0.###}")));
    }

    [Fact]
    public void Rotated_snapshot_lock_offset_remains_exact_stadium()
    {
        const double rotationDeg = 37;
        var angle = rotationDeg * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        (double X, double Y) Rotate((double X, double Y) p, bool inverse) =>
            inverse
                ? (p.X * cos + p.Y * sin, -p.X * sin + p.Y * cos)
                : (p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);

        var op = new CutOp
        {
            Op = "contour",
            PanelId = "P",
            FeatureId = "LOCK",
            Path = SnapshotLock927.Select(p => Rotate(p, inverse: false)).ToList(),
            RotationDeg = rotationDeg,
            ClosePath = true,
            Through = true,
            Placed = true,
        };

        var offset = ContourToolOffset.Apply([op], 5)[0].Path!;
        var localOffset = offset.Select(p => Rotate(p, inverse: true)).ToList();
        var cut = CutSize(localOffset);
        var fitted = PolylineArcFit.Fit(offset, closed: true);

        Assert.InRange(cut.L, 54.99, 55.01);
        Assert.InRange(cut.W, 15.99, 16.01);
        Assert.Equal(2, fitted.Count(s => s.Arc));
        Assert.All(fitted.Where(s => s.Arc), s => Assert.InRange(s.R, 2.99, 3.01));
    }

    [Fact]
    public void Snapshot_lock_offset_reports_cut_size()
    {
        var ring = SnapshotLock927.ToList();
        var miter = Inset(ring, 5, Clipper2Lib.JoinType.Miter);
        var round = Inset(ring, 5, Clipper2Lib.JoinType.Round);
        var miterCut = CutSize(miter);
        var roundCut = CutSize(round);
        Assert.True(
            miterCut.L > 54.2 && roundCut.L > 54.2,
            $"snapshot 55×16 after offset: Miter cut {miterCut.L:0.###}×{miterCut.W:0.###}, Round cut {roundCut.L:0.###}×{roundCut.W:0.###}");
    }

    static (double L, double W) CutSize(IReadOnlyList<(double X, double Y)> toolCenter)
    {
        var l = toolCenter.Max(p => p.X) - toolCenter.Min(p => p.X);
        var w = toolCenter.Max(p => p.Y) - toolCenter.Min(p => p.Y);
        var longSide = Math.Max(l, w) + 10;
        var shortSide = Math.Min(l, w) + 10;
        return (longSide, shortSide);
    }

    [Fact]
    public void S2_g3_bulge_recovers_lock_length()
    {
        // Morning S2 lock (T2 Ø10). Endpoint AABB was 43.75 → "cut 53.8".
        // G3 must be included — the tip is on the arc, not at a vertex.
        var xs = new List<double>();
        var ys = new List<double>();
        void Pt(double x, double y) { xs.Add(x); ys.Add(y); }
        void G3(double x0, double y0, double x1, double y1, double r)
        {
            foreach (var (x, y) in SampleG3(x0, y0, x1, y1, r))
            {
                xs.Add(x);
                ys.Add(y);
            }
        }

        Pt(340.1060, 953.2500);
        Pt(301.8940, 953.2500);
        G3(301.8940, 953.2500, 299.1237, 948.5232, 3.0149);
        G3(299.1237, 948.5232, 301.8940, 947.2500, 3.1750);
        Pt(340.1060, 947.2500);
        G3(340.1060, 947.2500, 342.8762, 951.9767, 3.0148);
        G3(342.8762, 951.9767, 340.1060, 953.2500, 3.1750);

        var cutL = (xs.Max() - xs.Min()) + 10;
        var cutW = (ys.Max() - ys.Min()) + 10;
        Assert.InRange(cutW, 15.8, 16.2);
        Assert.InRange(cutL, 54.6, 55.4);
    }

    static IEnumerable<(double X, double Y)> SampleG3(
        double x0, double y0, double x1, double y1, double r)
    {
        var (cx, cy) = G3Center(x0, y0, x1, y1, r);
        var a0 = Math.Atan2(y0 - cy, x0 - cx);
        var a1 = Math.Atan2(y1 - cy, x1 - cx);
        var sweep = a1 - a0;
        while (sweep <= 0) sweep += 2 * Math.PI;
        for (var i = 0; i <= 32; i++)
        {
            var a = a0 + sweep * i / 32;
            yield return (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
    }

    static (double Cx, double Cy) G3Center(double x0, double y0, double x1, double y1, double r)
    {
        var mx = (x0 + x1) * 0.5;
        var my = (y0 + y1) * 0.5;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        var h = Math.Sqrt(Math.Max(0, r * r - (chord * 0.5) * (chord * 0.5)));
        var px = -dy / chord;
        var py = dx / chord;
        // Two centers; G3 = CCW. Pick the one where start→end is CCW.
        var c1 = (mx + px * h, my + py * h);
        var c2 = (mx - px * h, my - py * h);
        foreach (var c in new[] { c1, c2 })
        {
            var sx = x0 - c.Item1;
            var sy = y0 - c.Item2;
            var ex = x1 - c.Item1;
            var ey = y1 - c.Item2;
            if (sx * ey - sy * ex > 0)
                return c;
        }
        return c1;
    }

    [Fact]
    public void Round_inset_of_55x16_stadium_does_not_shrink_to_53_8()
    {
        var stadium = LockSlotGeometry.CapsuleFromAabb(0, 55, 0, 16)
            .Select(p => (p.X, p.Y)).ToList();
        var round = Inset(stadium, 5, Clipper2Lib.JoinType.Round);
        var cutL = (round.Max(p => p.X) - round.Min(p => p.X)) + 10;
        var cutW = (round.Max(p => p.Y) - round.Min(p => p.Y)) + 10;
        Assert.InRange(cutW, 15.7, 16.3);
        Assert.True(cutL > 54.2, $"Round inset cut length {cutL:0.###} — if this is ~53.8, offset ate the 55");
    }

    static List<(double X, double Y)> Inset(
        IReadOnlyList<(double X, double Y)> ring, double mm, Clipper2Lib.JoinType join)
    {
        const double scale = 10000;
        var path = new Clipper2Lib.Path64();
        foreach (var p in ring)
            path.Add(new Clipper2Lib.Point64(
                (long)Math.Round(p.X * scale), (long)Math.Round(p.Y * scale)));
        var outp = Clipper2Lib.Clipper.InflatePaths(
            new Clipper2Lib.Paths64 { path }, -mm * scale, join, Clipper2Lib.EndType.Polygon, 2);
        var best = outp.OrderByDescending(p => Math.Abs(Clipper2Lib.Clipper.Area(p))).First();
        return best.Select(p => (p.X / scale, p.Y / scale)).ToList();
    }
}

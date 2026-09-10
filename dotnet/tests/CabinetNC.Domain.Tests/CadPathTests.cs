using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class CadPathTests
{
    static IReadOnlyList<CadSegment> Rect(double w, double h) =>
    [
        CadSegment.MakeLine(new(0, 0), new(w, 0)),
        CadSegment.MakeLine(new(w, 0), new(w, h)),
        CadSegment.MakeLine(new(w, h), new(0, h)),
        CadSegment.MakeLine(new(0, h), new(0, 0)),
    ];

    static IReadOnlyList<CadSegment> NotchedStrip() =>
    [
        CadSegment.MakeLine(new(0, 0), new(400, 0)),
        CadSegment.MakeLine(new(400, 0), new(400, 200)),
        CadSegment.MakeLine(new(400, 200), new(250, 200)),
        CadSegment.MakeLine(new(250, 200), new(250, 180)),
        CadSegment.MakeLine(new(250, 180), new(170, 180)),
        CadSegment.MakeLine(new(170, 180), new(170, 200)),
        CadSegment.MakeLine(new(170, 200), new(0, 200)),
        CadSegment.MakeLine(new(0, 200), new(0, 0)),
    ];

    [Fact]
    public void Outer_square_offset_adds_tool_radius_arcs_only()
    {
        Assert.True(CadPath.TryOffset(Rect(100, 80), 5, roundConvex: true, out var off));
        var arcs = off.Where(s => s.IsArc).ToList();
        Assert.Equal(4, arcs.Count);
        Assert.All(arcs, a => Assert.InRange(a.RadiusMm, 4.99, 5.01));
    }

    [Fact]
    public void Notch_offset_has_no_r20_invented_arc()
    {
        Assert.True(CadPath.TryOffset(NotchedStrip(), 5, roundConvex: true, out var off));
        Assert.DoesNotContain(off, s => s.IsArc && s.RadiusMm > 8);
        var notchLines = off.Count(s => s.IsLine);
        Assert.True(notchLines >= 8);
    }

    [Fact]
    public void Cad_notch_emits_r5_corners_not_r20()
    {
        var source = new CutOp
        {
            Op = "contour",
            PanelId = "FRAME",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            ThicknessMm = 18,
            DepthMm = 18.5,
            Path = CadPath.ToPolyline(NotchedStrip()),
            CadPath = NotchedStrip(),
        };
        var offset = ContourToolOffset.Apply([source], 5);
        Assert.NotNull(offset[0].CadPath);
        Assert.DoesNotContain(offset[0].CadPath!, s => s.IsArc && s.RadiusMm > 8 && s.RadiusMm < 40);
        var nc = NcEmitter.OpsToNc(offset, MachineCatalog.Get("nesting_router_6"),
            recipe: PostRecipe.TroyDefault());
        Assert.DoesNotContain("R20.", nc);
        Assert.Contains("R5.0000", nc);
        Assert.Contains("G1 ", nc);
    }

    [Fact]
    public void Inner_lock_cadpath_insets_caps_to_R3_not_R13()
    {
        var cad = LockSlotGeometry.CapsuleSegments(0, 55, 0, 16);
        Assert.True(CadPath.TryOffset(cad, -5, roundConvex: false, out var off));
        var arcs = off.Where(s => s.IsArc).ToList();
        Assert.Equal(2, arcs.Count);
        Assert.All(arcs, a => Assert.InRange(a.RadiusMm, 2.99, 3.01));

        var op = new CutOp
        {
            Op = "contour",
            PanelId = "P",
            FeatureId = "LOCK",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            ThicknessMm = 18,
            DepthMm = 18.5,
            Path = CadPath.ToPolyline(cad),
            CadPath = cad,
        };
        var offset = ContourToolOffset.Apply([op], 5)[0];
        Assert.NotNull(offset.CadPath);
        Assert.All(offset.CadPath!.Where(s => s.IsArc), a => Assert.InRange(a.RadiusMm, 2.99, 3.01));
        var nc = NcEmitter.OpsToNc([offset], MachineCatalog.Get("nesting_router_6"),
            recipe: PostRecipe.TroyDefault());
        Assert.Contains("R3.0000", nc);
        Assert.DoesNotContain("R13.", nc);
    }

    [Fact]
    public void Inner_ccw_designed_arc_shrinks_by_tool_radius()
    {
        const double r = 30;
        var cad = new CadSegment[]
        {
            CadSegment.MakeLine(new(r, 0), new(170, 0)),
            CadSegment.MakeArc(new(170, 0), new(200, r), new(170, r), r, cw: false),
            CadSegment.MakeLine(new(200, r), new(200, 90)),
            CadSegment.MakeArc(new(200, 90), new(170, 120), new(170, 90), r, cw: false),
            CadSegment.MakeLine(new(170, 120), new(r, 120)),
            CadSegment.MakeArc(new(r, 120), new(0, 90), new(r, 90), r, cw: false),
            CadSegment.MakeLine(new(0, 90), new(0, r)),
            CadSegment.MakeArc(new(0, r), new(r, 0), new(r, r), r, cw: false),
        };
        Assert.True(CadPath.TryOffset(cad, -5, roundConvex: false, out var off));
        Assert.All(off.Where(s => s.IsArc), a => Assert.InRange(a.RadiusMm, 24.99, 25.01));
    }

    /// <summary>Kitchen DS / SH tongue: 7.5 mm step, Ø10 tool. Must stay square.</summary>
    static IReadOnlyList<(double X, double Y)> DrawerShelfTongue() =>
    [
        (0, 15), (201.3333, 15), (201.3333, 0), (402.6667, 0), (402.6667, 15),
        (584, 15), (584, 424.9), (402.6667, 424.9), (402.6667, 432.4),
        (201.3333, 432.4), (201.3333, 424.9), (0, 424.9),
    ];

    static bool HasDiagonalG1(string nc)
    {
        double? x = null, y = null;
        foreach (var raw in nc.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] is not ('G' or 'N')) continue;
            var g1 = line.Contains("G1 ", StringComparison.Ordinal)
                     || line.Contains("G01", StringComparison.Ordinal);
            var nx = x;
            var ny = y;
            if (TryToken(line, 'X', out var xv)) nx = xv;
            if (TryToken(line, 'Y', out var yv)) ny = yv;
            if (g1 && x is { } px && y is { } py && nx is { } qx && ny is { } qy)
            {
                var dx = Math.Abs(qx - px);
                var dy = Math.Abs(qy - py);
                // Collapsed short-step gouge is a few mm. Ignore long linking moves.
                if (dx > 0.2 && dy > 0.2 && Math.Sqrt(dx * dx + dy * dy) < 20)
                    return true;
            }
            x = nx;
            y = ny;
        }
        return false;
    }

    static bool TryToken(string line, char axis, out double v)
    {
        v = 0;
        var i = line.IndexOf(axis);
        if (i < 0) return false;
        var j = i + 1;
        while (j < line.Length && (char.IsDigit(line[j]) || line[j] is '.' or '-' or '+'))
            j++;
        return double.TryParse(line[(i + 1)..j], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    [Fact]
    public void Short_tongue_offset_stays_axis_aligned()
    {
        var cad = CadPath.FromPolyline(DrawerShelfTongue());
        Assert.True(CadPath.IsAxisAligned(DrawerShelfTongue()));
        Assert.True(CadPath.TryOffset(cad, 5, roundConvex: true, out var off));
        Assert.All(off.Where(s => s.IsLine), s =>
        {
            var dx = Math.Abs(s.End.X - s.Start.X);
            var dy = Math.Abs(s.End.Y - s.Start.Y);
            Assert.True(dx < 0.05 || dy < 0.05, $"slanted leftover {s.Start}→{s.End}");
        });
        Assert.Contains(off, s => s.IsArc && Math.Abs(s.RadiusMm - 5) < 0.02);
        var leftover = off.Where(s => s.IsLine
            && Math.Abs(Math.Abs(s.End.Y - s.Start.Y) - 2.5) < 0.05
            && Math.Abs(s.End.X - s.Start.X) < 0.05).ToList();
        Assert.True(leftover.Count >= 2, "7.5 mm step should keep a 2.5 mm vertical after R5");
    }

    [Fact]
    public void Fusion_polyline_only_contour_does_not_emit_diagonal_g1()
    {
        var source = new CutOp
        {
            Op = "contour",
            PanelId = "DS",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            ThicknessMm = 15,
            DepthMm = 15.5,
            Path = DrawerShelfTongue(),
        };
        var offset = ContourToolOffset.Apply([source], 5)[0];
        Assert.NotNull(offset.CadPath);
        Assert.All(offset.CadPath!.Where(s => s.IsLine), s =>
        {
            var dx = Math.Abs(s.End.X - s.Start.X);
            var dy = Math.Abs(s.End.Y - s.Start.Y);
            Assert.True(dx < 0.05 || dy < 0.05);
        });
        var nc = NcEmitter.OpsToNc([offset], MachineCatalog.Get("nesting_router_6"),
            recipe: PostRecipe.TroyDefault());
        Assert.False(HasDiagonalG1(nc), nc);
        Assert.Contains("R5.0000", nc);
    }

    [Fact]
    public void Outer_cw_designed_arc_grows_by_tool_radius()
    {
        const double r = 50;
        var cad = new CadSegment[]
        {
            CadSegment.MakeLine(new(r, 0), new(0, 0)),
            CadSegment.MakeLine(new(0, 0), new(0, 200)),
            CadSegment.MakeLine(new(0, 200), new(400, 200)),
            CadSegment.MakeLine(new(400, 200), new(400, r)),
            CadSegment.MakeArc(new(400, r), new(400 - r, 0), new(400 - r, r), r, cw: true),
        };
        Assert.True(CadPath.TryOffset(cad, 5, roundConvex: true, out var off));
        var designed = off.Where(s => s.IsArc && s.RadiusMm > 10).ToList();
        Assert.Contains(designed, a => a.RadiusMm is >= 54.9 and <= 55.1);
    }
}

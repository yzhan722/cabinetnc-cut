using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class PolylineArcFitTests
{
    static List<(double X, double Y)> Quarter(bool cw, int steps = 12)
    {
        var pts = new List<(double X, double Y)>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps * Math.PI / 2;
            pts.Add(cw
                ? (5 * Math.Cos(t), -5 * Math.Sin(t))
                : (5 * Math.Cos(t), 5 * Math.Sin(t)));
        }
        return pts;
    }

    [Fact]
    public void Dirty_clipper_corner_snaps_g1_to_true_tangents()
    {
        // Shop NC: G1 to (13.83, 10.14) then G2 R5 to (10, 15) — CAD has no step.
        var path = new List<(double X, double Y)> { (847.0000, 10.0000) };
        const double cx = 15, cy = 15, r = 5;
        var a0 = Math.Atan2(10.1382 - cy, 13.8328 - cx);
        var a1 = Math.Atan2(15.0000 - cy, 10.0000 - cx);
        var sweep = a1 - a0;
        while (sweep > 0) sweep -= 2 * Math.PI;
        for (var i = 0; i <= 12; i++)
        {
            var a = a0 + sweep * (i / 12d);
            path.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        path.Add((10.1382, 495.1672));
        var segs = PolylineArcFit.Fit(path, closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.Equal(5, arc.R, 3);
        var incoming = segs[segs.ToList().IndexOf(arc) - 1];
        Assert.False(incoming.Arc);
        Assert.InRange(incoming.X, 14.98, 15.02);
        Assert.InRange(incoming.Y, 9.98, 10.02);
        Assert.InRange(arc.X, 9.98, 10.02);
        Assert.InRange(arc.Y, 14.98, 15.02);
    }

    [Fact]
    public void Tessellated_quarter_becomes_one_arc_R5()
    {
        var segs = PolylineArcFit.Fit(Quarter(cw: false), closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.False(arc.Cw);
        Assert.Equal(5, arc.R, 3);
        Assert.Equal(0, arc.X, 3);
        Assert.Equal(5, arc.Y, 3);
    }

    [Fact]
    public void Clockwise_quarter_is_G2()
    {
        var segs = PolylineArcFit.Fit(Quarter(cw: true), closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.True(arc.Cw);
        Assert.Equal(5, arc.R, 3);
    }

    [Fact]
    public void Clipper_style_dense_fan_becomes_one_R5()
    {
        // Real S5 corner (Bunk Bed S5 N211–N224): 0.63 mm chords on R5.
        (double X, double Y)[] fan =
        [
            (624.000, 1317.000),
            (624.631, 1317.040),
            (625.252, 1317.159),
            (625.852, 1317.356),
            (626.424, 1317.627),
            (626.956, 1317.967),
            (627.441, 1318.373),
            (627.871, 1318.836),
            (628.240, 1319.350),
            (628.540, 1319.906),
            (628.768, 1320.495),
            (628.920, 1321.109),
            (628.993, 1321.737),
            (629.000, 1322.000),
        ];
        var segs = PolylineArcFit.Fit(fan, closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.Equal(5, arc.R, 3);
        Assert.Equal(629, arc.X, 2);
        Assert.Equal(1322, arc.Y, 2);
    }

    [Fact]
    public void Sharp_square_stays_G1()
    {
        var segs = PolylineArcFit.Fit([(0, 0), (200, 0), (200, 100), (0, 100)], closed: true);
        Assert.DoesNotContain(segs, s => s.Arc);
        Assert.True(segs.Count >= 4);
    }

    static List<(double X, double Y)> RoundedRect(
        double x, double y, double w, double h, double r, int steps = 12)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        var pts = new List<(double X, double Y)>();
        void Fan(double cx, double cy, double a0)
        {
            for (var i = 0; i <= steps; i++)
            {
                var a = a0 + i / (double)steps * Math.PI / 2;
                pts.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
        }
        Fan(x + w - r, y + r, -Math.PI / 2);
        Fan(x + w - r, y + h - r, 0);
        Fan(x + r, y + h - r, Math.PI / 2);
        Fan(x + r, y + r, Math.PI);
        return pts;
    }

    static List<(double X, double Y)> QuarterR(double r, bool cw, int steps = 16)
    {
        var pts = new List<(double X, double Y)>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps * Math.PI / 2;
            pts.Add(cw
                ? (r * Math.Cos(t), -r * Math.Sin(t))
                : (r * Math.Cos(t), r * Math.Sin(t)));
        }
        return pts;
    }

    [Fact]
    public void Lounge_top_inner_rebate_wall_fits_arcs_not_g1_stair()
    {
        // _16.anc N26–N48: T1 inner shoulder after Clipper expand (was all G1).
        (double X, double Y)[] wall =
        [
            (429.6750, 1622.0000),
            (428.0829, 1610.2066),
            (420.2910, 1594.6179),
            (416.4151, 1590.2460),
            (407.1254, 1583.1970),
            (401.8714, 1580.6411),
            (396.3288, 1578.7926),
            (390.5925, 1577.6839),
            (198.9322, 1577.7466),
            (173.2460, 1590.5859),
            (163.6411, 1605.1286),
            (161.7926, 1610.6712),
            (160.6839, 1616.4075),
            (160.7466, 1978.0688),
            (161.9171, 1983.7934),
            (173.5859, 2003.7540),
            (199.4085, 2016.3161),
            (391.0688, 2016.2534),
            (396.7934, 2015.0829),
            (416.7550, 2003.4141),
            (426.3589, 1988.8714),
            (429.6750, 1972.0000),
        ];
        var segs = PolylineArcFit.Fit(wall, closed: false);
        var arcs = segs.Where(s => s.Arc).ToList();
        Assert.True(arcs.Count >= 3, $"arcs={arcs.Count} segs={segs.Count}");
        Assert.All(arcs, a => Assert.InRange(a.R, 40, 55));
    }

    [Fact]
    public void Rebate_inner_wall_has_four_arcs_and_no_short_g1_stubs()
    {
        var cleared = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = RoundedRect(0, 0, 447, 277, 48.5),
            Holes = [RoundedRect(9, 9, 429, 259, 39.5)],
            ToolDiameterMm = 6.35,
        });
        Assert.False(cleared.TooSmallForTool);
        Assert.True(cleared.Segments.Count >= 2, $"segs={cleared.Segments.Count}");
        var inner = cleared.Segments[1];
        var segs = PolylineArcFit.Fit(inner, closed: true);
        var arcs = segs.Where(s => s.Arc).ToList();
        Assert.True(arcs.Count >= 4, $"arcs={arcs.Count} n={inner.Count}");
        Assert.All(arcs, a => Assert.InRange(a.R, 40, 50));

        var pos = inner[0];
        foreach (var s in segs)
        {
            var len = Math.Sqrt((s.X - pos.X) * (s.X - pos.X) + (s.Y - pos.Y) * (s.Y - pos.Y));
            if (!s.Arc)
                Assert.True(len > 20, $"G1 stub {len:0.0}mm to ({s.X:0.##},{s.Y:0.##})");
            pos = (s.X, s.Y);
        }
    }

    [Fact]
    public void Lounge_lid_quarter_R48_5_becomes_one_arc()
    {
        var segs = PolylineArcFit.Fit(QuarterR(48.5, cw: false), closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.False(arc.Cw);
        Assert.Equal(48.5, arc.R, 2);
        Assert.Equal(0, arc.X, 2);
        Assert.Equal(48.5, arc.Y, 2);
    }

    [Fact]
    public void Bulvd1_two_chord_R165_corner_is_one_arc()
    {
        // _16-class BULVD1 N280–N306: Fusion sent the designed corner as two
        // long G1s. Circle through the three vertices is R165 / 92°.
        (double X, double Y)[] corner =
        [
            (154.6551, 1193.4933),
            (165.3807, 1068.3827),
            (268.2250, 984.5500),
        ];
        var segs = PolylineArcFit.Fit(corner, closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.InRange(arc.R, 160, 170);
        Assert.InRange(arc.X, 268.2, 268.3);
        Assert.InRange(arc.Y, 984.5, 984.6);
    }

    [Fact]
    public void Bulvd1_corner_between_straights_stays_one_arc()
    {
        const double cx = 312.6266, cy = 1144.0210;
        (double X, double Y) p1 = (154.6551, 1193.4933);
        (double X, double Y) p2 = (165.3807, 1068.3827);
        (double X, double Y) p3 = (268.2250, 984.5500);
        (double X, double Y) Tangent((double X, double Y) p)
        {
            var rx = p.X - cx;
            var ry = p.Y - cy;
            var len = Math.Sqrt(rx * rx + ry * ry);
            return (-ry / len, rx / len);
        }
        var t1 = Tangent(p1);
        var t3 = Tangent(p3);
        (double X, double Y)[] path =
        [
            (p1.X - 80 * t1.X, p1.Y - 80 * t1.Y),
            p1, p2, p3,
            (p3.X + 80 * t3.X, p3.Y + 80 * t3.Y),
        ];
        var segs = PolylineArcFit.Fit(path, closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.InRange(arc.R, 160, 170);
    }

    [Fact]
    public void Bulvd1_three_chord_R1714_bow_is_one_arc()
    {
        // d16_1_BULVD1 N133–N135: Fusion tessellated the designed hypot as
        // three G1s. Circle is R1714 / 40° / sagitta ~104 — must stay an arc
        // or the toolpath chords off the CAD edge.
        (double X, double Y)[] bow =
        [
            (10.2102, 346.4345),
            (357.0520, 1128.1061),
            (590.9163, 1370.2509),
        ];
        var segs = PolylineArcFit.Fit(bow, closed: false);
        var arc = Assert.Single(segs, s => s.Arc);
        Assert.InRange(arc.R, 1650, 1780);
        Assert.InRange(arc.X, 590.9, 591.0);
        Assert.InRange(arc.Y, 1370.2, 1370.3);
    }

    [Fact]
    public void Leftover_R70_forty_degree_bow_stays_G1()
    {
        // _02.anc N51: leftover run after a tab, fitted as G2 R69.8355.
        const double r = 69.8355;
        var sweep = 42 * Math.PI / 180;
        var pts = new List<(double X, double Y)>();
        for (var i = 0; i <= 8; i++)
        {
            var a = -sweep / 2 + sweep * (i / 8d);
            pts.Add((r * Math.Sin(a), r * (1 - Math.Cos(a))));
        }
        var segs = PolylineArcFit.Fit(pts, closed: false);
        Assert.DoesNotContain(segs, s => s.Arc);
    }

    [Fact]
    public void Shallow_bow_on_long_edge_becomes_one_G1()
    {
        // Circular R256 over ~90 mm — the false G2 OmniCam used to emit on square edges.
        const double r = 256;
        var sweep = 91.5 / r;
        var pts = new List<(double X, double Y)>();
        for (var i = 0; i <= 12; i++)
        {
            var a = -sweep / 2 + sweep * (i / 12d);
            pts.Add((257 + r * Math.Sin(a), 1285 - r * (1 - Math.Cos(a))));
        }
        var segs = PolylineArcFit.Fit(pts, closed: false);
        Assert.DoesNotContain(segs, s => s.Arc);
        var line = Assert.Single(segs);
        Assert.Equal(pts[^1].X, line.X, 3);
        Assert.Equal(pts[^1].Y, line.Y, 3);
    }
}

public class NcEmitterTroyArcTests
{
    static MachineProfile Machine() => MachineCatalog.Get("nesting_router_6");

    [Fact]
    public void Offset_outer_square_uses_R5_tool_centre_corners()
    {
        var source = new CutOp
        {
            Op = "contour",
            PanelId = "P1",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            ThicknessMm = 18,
            DepthMm = 18.5,
            Path = [(0, 0), (100, 0), (100, 80), (0, 80)],
        };
        var offset = ContourToolOffset.Apply([source], 5);
        var path = offset[0].Path!;
        var nc = NcEmitter.OpsToNc(offset, Machine(), recipe: PostRecipe.TroyDefault());
        Assert.Contains("G2 ", nc);
        Assert.Contains("R5.0000", nc);
        Assert.True(ClimbCut.SignedArea(path) < 0, "outer climb is CW");
        Assert.True(path.Count > 8, $"round square needs corner fans, got {path.Count}");
        var segs = PolylineArcFit.Fit(path, closed: true);
        var pos = path[0];
        foreach (var s in segs)
        {
            if (!s.Arc)
            {
                var len = Math.Sqrt((s.X - pos.X) * (s.X - pos.X) + (s.Y - pos.Y) * (s.Y - pos.Y));
                Assert.True(len > 20, $"G1 stub {len:0.###}mm ({pos.X:0.##},{pos.Y:0.##})→({s.X:0.##},{s.Y:0.##})");
            }
            else
            {
                Assert.True(
                    AlmostAxis(pos, (s.X, s.Y), 5) || AlmostQuarter(pos, (s.X, s.Y), 5),
                    $"arc not on tool-centre tangents ({pos.X:0.##},{pos.Y:0.##})→({s.X:0.##},{s.Y:0.##})");
            }
            pos = (s.X, s.Y);
        }
    }

    static bool AlmostAxis((double X, double Y) a, (double X, double Y) b, double tol) =>
        Math.Abs(a.X - b.X) < 0.02 && Math.Abs(a.Y - b.Y) > tol
        || Math.Abs(a.Y - b.Y) < 0.02 && Math.Abs(a.X - b.X) > tol;

    static bool AlmostQuarter((double X, double Y) a, (double X, double Y) b, double r) =>
        Math.Abs(Math.Abs(a.X - b.X) - r) < 0.05 && Math.Abs(Math.Abs(a.Y - b.Y) - r) < 0.05;

    [Fact]
    public void Inner_window_climb_is_ccw_G3()
    {
        var source = new CutOp
        {
            Op = "contour",
            PanelId = "P1",
            FeatureId = "W1",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            ThicknessMm = 18,
            DepthMm = 18.5,
            Path = [(20, 20), (80, 20), (80, 70), (20, 70)],
        };
        var offset = ContourToolOffset.Apply([source], 5);
        var path = offset[0].Path!;
        var nc = NcEmitter.OpsToNc(offset, Machine(), recipe: PostRecipe.TroyDefault());
        // Sharp inner rectangle: Clipper inset stays G1 (no round join). Climb is CCW.
        Assert.True(ClimbCut.SignedArea(path) > 0, "inner climb is CCW");
        Assert.DoesNotContain("G2 ", nc);
    }

    [Fact]
    public void Rectangular_notch_on_strip_stays_square_not_false_r22()
    {
        // Sheet-12 BLMFC strip: 20×20 mid-edge notch. Offset + Fit used to
        // emit G2 R21.96 across the exit instead of G1 + R5.
        var source = new CutOp
        {
            Op = "contour",
            PanelId = "STRIP",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            ThicknessMm = 18,
            DepthMm = 18.5,
            Path =
            [
                (729, 100), (0, 100), (0, 0), (1478, 0), (1478, 100),
                (749, 100), (749, 80), (729, 80),
            ],
        };
        var offset = ContourToolOffset.Apply([source], 5);
        var segs = PolylineArcFit.Fit(offset[0].Path!, closed: true);
        Assert.DoesNotContain(segs, s => s.Arc && s.R > 8 && s.R < 40);
        var nc = NcEmitter.OpsToNc(offset, Machine(), recipe: PostRecipe.TroyDefault());
        Assert.DoesNotContain("R21.", nc);
        Assert.DoesNotContain("R20.", nc);
        Assert.Contains("R5.0000", nc);
    }

    [Fact]
    public void Wide_shallow_notch_exit_is_not_r20_fillet()
    {
        // _04.anc N18–N21 / _18.anc N173–N176: 80×20 notch, G2 R20.7752
        // between two R5 corners (below the old 21.58 shop-fillet cap).
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
            Path =
            [
                (0, 0), (400, 0), (400, 200),
                (250, 200), (250, 180), (170, 180), (170, 200),
                (0, 200),
            ],
        };
        var offset = ContourToolOffset.Apply([source], 5);
        var segs = PolylineArcFit.Fit(offset[0].Path!, closed: true);
        Assert.DoesNotContain(segs, s => s.Arc && s.R > 8 && s.R < 40);
        var nc = NcEmitter.OpsToNc(offset, Machine(), recipe: PostRecipe.TroyDefault());
        Assert.DoesNotContain("R20.", nc);
        Assert.Contains("R5.0000", nc);
    }

    [Fact]
    public void Through_finger_hole_offset_emits_one_G3_not_onion_rings()
    {
        var source = new CutOp
        {
            Op = "contour",
            PanelId = "LID",
            FeatureId = "FINGER",
            ToolId = "T2",
            Placed = true,
            ClosePath = true,
            Through = true,
            ThicknessMm = 18,
            DepthMm = 18.55,
            DiameterMm = 40,
            Path = ClearanceToolPick.CupOutline(new PanelFeature
            {
                FeatureId = "FINGER",
                Kind = "holeVertical",
                X = 223.5,
                Y = 138.5,
                DiameterMm = 40,
                Through = true,
            })!,
        };
        var offset = ContourToolOffset.Apply([source], 5);
        var nc = NcEmitter.OpsToNc(offset, Machine(), recipe: PostRecipe.TroyDefault());
        Assert.Contains("G3 ", nc);
        Assert.DoesNotContain("R2.4625", nc);
        Assert.DoesNotContain("R6.4710", nc);
        Assert.DoesNotContain("R10.4797", nc);
        var radii = System.Text.RegularExpressions.Regex.Matches(nc, @"G3 [^N\n]*R(\d+\.\d+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();
        var r = Assert.Single(radii);
        Assert.InRange(r, 14.9, 15.1);
    }
}

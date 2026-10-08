using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Xunit.Abstractions;
using static CabinetNC.Verify.Tests.VerifyFixture;

namespace CabinetNC.Verify.Tests;

public class ExportVerifierTests(ITestOutputHelper output)
{
    // A realistic shop panel: drill, window, blind pocket, wide groove, hinge cup.
    static Panel ShopPanel(string id = "A") => Rect(id, 400, 300, 18,
        Hole("H1", 40, 40, 3),
        Hole("H2", 360, 40, 3),
        Cutout("W1", 150, 90, 250, 150),
        Pocket("P1", 280, 200, 360, 260, 8),
        Groove("G1", new Point2(30, 180), new Point2(130, 180), 10, 6),
        Hole("CUP", 60, 250, 35, depth: 12, through: false));

    static (List<Panel> Panels, List<NestPlacement> Places) Job(params (Panel P, double X, double Y, double Rot)[] items)
    {
        var panels = items.Select(i => i.P).ToList();
        var places = items.Select(i => Place(i.P.PanelId, i.X, i.Y, i.Rot)).ToList();
        return (panels, places);
    }

    void Dump(VerifyReport r) => output.WriteLine(Describe(r));

    // ------------------------------------------------------------------ positive paths

    [Fact]
    public void Shipping_troy_program_passes()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var nc = EmitTroy(PlanOps(panels, places));
        var r = Run(Input(panels, places, nc));
        Dump(r);
        Assert.True(r.Ok, Describe(r));
        Assert.DoesNotContain(r.Issues, i => i.Code == VerifyCodes.FeatureUnverifiable);
    }

    [Fact]
    public void Rotated_and_multiple_panels_pass()
    {
        var (panels, places) = Job(
            (ShopPanel("A"), 20, 20, 0),
            (ShopPanel("B"), 450, 20, 90),
            (Rect("C", 200, 120, 18, Hole("h", 30, 30, 3)), 20, 350, 0));
        var nc = EmitTroy(PlanOps(panels, places));
        var r = Run(Input(panels, places, nc));
        Dump(r);
        Assert.True(r.Ok, Describe(r));
        Assert.Equal(3, r.PanelCount);
    }

    [Fact]
    public void Shipping_generic_program_passes_with_board_top_datum()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var nc = EmitGeneric(PlanOps(panels, places));
        var r = Run(Input(panels, places, nc, ZDatum.BoardTop));
        Dump(r);
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Wrong_z_datum_is_caught_not_silently_passed()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var nc = EmitTroy(PlanOps(panels, places));
        var r = Run(Input(panels, places, nc, ZDatum.BoardTop));
        Dump(r);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Declared_bridges_are_exempt_undeclared_gaps_are_not()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var ops = PlanOps(panels, places);
        var outer = ops.First(o => o.Op == "contour" && o.FeatureId is null);
        var mid = outer.Path![0];
        var next = outer.Path![1];
        var bridge = new ProfileBridge
        {
            Id = "b1",
            PanelId = "A",
            SheetIndex = 0,
            X = (mid.X + next.X) / 2,
            Y = (mid.Y + next.Y) / 2,
            WidthMm = 5,
            ArcLengthMm = Math.Sqrt(Math.Pow(next.X - mid.X, 2) + Math.Pow(next.Y - mid.Y, 2)) / 2,
        };
        var nc = EmitTroy(ops, PostRecipe.TroyDefault().WithBridges([bridge]));

        var declared = Run(Input(panels, places, nc, bridges: [bridge]));
        Dump(declared);
        Assert.True(declared.Ok, Describe(declared));

        var undeclared = Run(Input(panels, places, nc));
        Dump(undeclared);
        Assert.Contains(undeclared.Issues, i => i.Code == VerifyCodes.ThroughUndercut && i.FeatureId is null);
    }

    // ------------------------------------------------------------------ mutations: each code must fire

    [Fact]
    public void Flags_outline_not_cut_through()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var nc = EmitTroy(PlanOps(panels, places)).Replace("Z-0.5500", "Z0.5000", StringComparison.Ordinal);
        var r = Run(Input(panels, places, nc));
        Dump(r);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.ThroughUndercut && i.FeatureId is null);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.FeatureNotCut && i.FeatureId == "W1");
    }

    [Fact]
    public void Flags_through_overcut_into_panel_body()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var nc = Inject(EmitTroy(PlanOps(panels, places)),
            "G0 X60.0000 Y60.0000 Z30.0000", "G1 Z-0.5500 F1000.0", "G1 X60.0000 Y140.0000 F5000.0", "G0 Z30.0000");
        var r = Run(Input(panels, places, nc));
        Dump(r);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.ThroughOvercut && i.PanelId == "A");
    }

    [Fact]
    public void Flags_blind_overcut_where_no_feature_exists()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        // Z13 on an 18 board = 5 mm deep across bare panel.
        var nc = Inject(EmitTroy(PlanOps(panels, places)),
            "G0 X200.0000 Y40.0000 Z30.0000", "G1 Z13.0000 F1000.0", "G1 X380.0000 Y40.0000 F5000.0", "G0 Z30.0000");
        var r = Run(Input(panels, places, nc));
        Dump(r);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.BlindOvercut && i.PanelId == "A");
        Assert.DoesNotContain(r.Issues, i => i.Code == VerifyCodes.ThroughOvercut);
    }

    [Fact]
    public void Flags_pocket_cut_deeper_than_cad()
    {
        var cad = ShopPanel();
        var deeper = Rect("A", 400, 300, 18, cad.Features.Select(f =>
            f.FeatureId == "P1" ? Pocket("P1", 280, 200, 360, 260, 12) : f).ToArray());
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var nc = EmitTroy(PlanOps([deeper], places));
        var r = Run(Input([cad], places, nc));
        Dump(r);
        var hit = Assert.Single(r.Issues, i => i.Code == VerifyCodes.DepthMismatch && i.FeatureId == "P1");
        Assert.Equal(8, hit.ExpectedMm);
        Assert.InRange(hit.MeasuredMm ?? 0, 11.8, 12.2);
    }

    [Fact]
    public void Flags_pocket_floor_left_uncut()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        // Keep only the first quarter of the clear path: walls/finish gone, floor half done.
        var ops = PlanOps(panels, places).Select(o =>
            o.Op == "pocket" && o.FeatureId == "P1" && o.PathSegments is { Count: >= 1 } segs
                ? o with
                {
                    PathSegments = [segs[0].Take(Math.Max(2, segs[0].Count / 4)).ToList()],
                    FinishLoop = null,
                }
                : o).ToList();
        var r = Run(Input(panels, places, EmitTroy(ops)));
        Dump(r);
        Assert.Contains(r.Issues, i => i.FeatureId == "P1"
            && (i.Code == VerifyCodes.PocketFloorUncut || i.Code == VerifyCodes.FeatureNotCut));
    }

    [Fact]
    public void Flags_groove_narrower_than_cad()
    {
        var cad = ShopPanel();
        var narrow = Rect("A", 400, 300, 18, cad.Features.Select(f =>
            f.FeatureId == "G1" ? Groove("G1", new Point2(30, 180), new Point2(130, 180), 6.35, 6) : f).ToArray());
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var nc = EmitTroy(PlanOps([narrow], places));
        var r = Run(Input([cad], places, nc));
        Dump(r);
        var hit = Assert.Single(r.Issues, i => i.Code == VerifyCodes.GrooveWidthMismatch && i.FeatureId == "G1");
        Assert.Equal(10, hit.ExpectedMm);
        Assert.InRange(hit.MeasuredMm ?? 0, 6.0, 6.7);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.GrooveFloorUncut && i.FeatureId == "G1");
    }

    [Fact]
    public void Flags_groove_wider_than_cad_as_overcut_and_width()
    {
        var cad = ShopPanel();
        var wide = Rect("A", 400, 300, 18, cad.Features.Select(f =>
            f.FeatureId == "G1" ? Groove("G1", new Point2(30, 180), new Point2(130, 180), 14, 6) : f).ToArray());
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var nc = EmitTroy(PlanOps([wide], places));
        var r = Run(Input([cad], places, nc));
        Dump(r);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.GrooveWidthMismatch && i.FeatureId == "G1");
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.BlindOvercut && i.FeatureId == "G1");
    }

    [Fact]
    public void Groove_crossed_by_sibling_at_junction_passes()
    {
        var panel = Rect("A", 400, 300, 18,
            Groove("MAIN", new Point2(30, 100), new Point2(370, 100), 14.5, 6),
            Groove("BRANCH", new Point2(200, 100), new Point2(200, 250), 14.5, 6));
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var nc = EmitTroy(PlanOps([panel], places));
        var r = Run(Input([panel], places, nc));
        Dump(r);
        Assert.True(r.Ok, Describe(r));
        Assert.DoesNotContain(r.Issues, i => i.Code == VerifyCodes.GrooveWidthMismatch);
    }

    [Fact]
    public void Flags_drill_drift_and_missing_hole()
    {
        var cad = ShopPanel();
        var shifted = Rect("A", 400, 300, 18, cad.Features
            .Where(f => f.FeatureId != "H2")
            .Select(f => f.FeatureId == "H1" ? Hole("H1", 41.5, 40, 3) : f).ToArray());
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var nc = EmitTroy(PlanOps([shifted], places));
        var r = Run(Input([cad], places, nc));
        Dump(r);
        var drift = Assert.Single(r.Issues, i => i.Code == VerifyCodes.DrillCentreDrift && i.FeatureId == "H1");
        Assert.InRange(drift.MeasuredMm ?? 0, 1.4, 1.6);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.FeatureNotCut && i.FeatureId == "H2");
    }

    [Fact]
    public void Flags_hole_drilled_with_wrong_diameter()
    {
        var cad = Rect("A", 200, 120, 18, Hole("H", 50, 50, 4.5));
        var made = Rect("A", 200, 120, 18, Hole("H", 50, 50, 3));
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var nc = EmitTroy(PlanOps([made], places));
        var r = Run(Input([cad], places, nc));
        Dump(r);
        var hit = Assert.Single(r.Issues, i => i.Code == VerifyCodes.HoleDiameterMismatch);
        Assert.Equal(4.5, hit.ExpectedMm);
        Assert.Equal(3, hit.MeasuredMm);
    }

    [Fact]
    public void Flags_window_missing_from_program()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var ops = PlanOps(panels, places).Where(o => o.FeatureId != "W1").ToList();
        var r = Run(Input(panels, places, EmitTroy(ops)));
        Dump(r);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.FeatureNotCut && i.FeatureId == "W1");
    }

    [Fact]
    public void Flags_unknown_tool_number()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var nc = Inject(EmitTroy(PlanOps(panels, places)),
            "M6 T9", "G0 X60.0000 Y60.0000 Z30.0000", "G1 Z13.0000 F1000.0", "G1 X80.0000 F5000.0", "G0 Z30.0000", "M6 T2");
        var r = Run(Input(panels, places, nc));
        Dump(r);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.ToolUnknown && i.IsError);
    }

    [Fact]
    public void Warns_on_stray_cut_far_from_any_panel()
    {
        var (panels, places) = Job((ShopPanel(), 20, 20, 0));
        var nc = Inject(EmitTroy(PlanOps(panels, places)),
            "G0 X900.0000 Y900.0000 Z30.0000", "G1 Z-0.5500 F1000.0", "G1 X1100.0000 Y900.0000 F5000.0", "G0 Z30.0000");
        var r = Run(Input(panels, places, nc));
        Dump(r);
        var hit = Assert.Single(r.Issues, i => i.Code == VerifyCodes.StrayCut);
        Assert.False(hit.IsError);
        Assert.True(r.Ok);
    }

    [Fact]
    public void Unverifiable_features_warn_but_do_not_block()
    {
        var p = Rect("A", 200, 120, 18,
            new PanelFeature { FeatureId = "X", Kind = "mystery", X = 50, Y = 50 },
            Groove("G", new Point2(20, 60), new Point2(180, 60), 0, 6));
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var r = Run(Input([p], places, EmitTroy(PlanOps([p], places))));
        Dump(r);
        Assert.True(r.Ok, Describe(r));
        Assert.Equal(2, r.Issues.Count(i => i.Code == VerifyCodes.FeatureUnverifiable));
    }

    [Fact]
    public void Empty_sheet_reports_no_panels_warning()
    {
        var r = Run(Input([], [], "N1 G90\nN2 M30\n"));
        Assert.True(r.Ok);
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.NoPanels);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Insert raw blocks after the Troy header (before the first XY motion).</summary>
    static string Inject(string nc, params string[] blocks)
    {
        var lines = nc.Split("\r\n").ToList();
        var at = lines.FindIndex(l => l.Contains("G17", StringComparison.Ordinal)) + 1;
        lines.InsertRange(at, blocks.Select((b, i) => $"N9{i:000} {b}"));
        return string.Join("\r\n", lines);
    }
}

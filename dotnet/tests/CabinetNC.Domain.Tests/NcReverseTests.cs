using CabinetNC.Domain;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NcReverseTests
{
    static MachineProfile Machine() => MachineCatalog.Get("nesting_router_6");

    static CutOp Drill() => new()
    {
        Op = "drill",
        PanelId = "P1",
        FeatureId = "H1",
        ToolId = "T3",
        Placed = true,
        SheetX = 30,
        SheetY = 40,
        DiameterMm = 3,
        DepthMm = 18,
        ThicknessMm = 18,
        Through = true,
    };

    static CutOp Tongue() => new()
    {
        Op = "groove",
        PanelId = "P1",
        FeatureId = "TG1",
        ToolId = "T1",
        Placed = true,
        IsTongue = true,
        DepthMm = 9,
        ThicknessMm = 18,
        Path = [(10, 10), (190, 10)],
        ClosePath = false,
    };

    static CutOp Outer() => new()
    {
        Op = "contour",
        PanelId = "P1",
        ToolId = "T2",
        Placed = true,
        ClosePath = true,
        Through = true,
        ThicknessMm = 18,
        DepthMm = 18.5,
        Path = [(0, 0), (200, 0), (200, 100), (0, 100)],
    };

    static CutOp InnerCutout() => new()
    {
        Op = "contour",
        PanelId = "P1",
        FeatureId = "CUT-1",
        ToolId = "T2",
        Placed = true,
        ClosePath = true,
        Through = true,
        ThicknessMm = 18,
        DepthMm = 18.5,
        Path = [(40, 20), (80, 20), (80, 70), (40, 70)],
    };

    static string EmitOffset(params CutOp[] ops)
    {
        var offset = ContourToolOffset.Apply(ops, 5);
        return NcEmitter.OpsToNc(offset, Machine(), recipe: PostRecipe.TroyDefault());
    }

    [Fact]
    public void Lexer_reads_header_and_skips_pro2()
    {
        var nc = """
            ;SELECT PROCESS
            (GTO,PRO1,!PROC(0)=1)
            (GTO,PRO2,!PROC(0)=2)
            "PRO2"
            N1 LS11='x'
            N2 M701
            "PRO1"
            N1 G90
            N2 G40
            N3 (UAO,1)
            N4 M6 T2
            N5 G0 X10.0000 Y20.0000 Z30.0000
            N6 G1 Z0.5000 F1000.0
            N7 M30
            """;
        var all = OsaiTroyLexer.Lex(nc);
        Assert.Contains(all, l => l.Label == "PRO1");
        var cut = OsaiTroyLexer.CutProgram(all);
        Assert.DoesNotContain(cut, l => l.Paren is not null && l.Paren.Contains("LS11", StringComparison.Ordinal));
        Assert.Contains(cut, l => l.Words.Any(w => w.Letter == 'G' && w.Number == 90));
        Assert.Contains(cut, l => l.Words.Any(w => w.Letter == 'M' && w.Number == 6));
    }

    /// <summary>The export viewer highlights the executing block; strokes must point back at the source line.</summary>
    [Fact]
    public void Strokes_carry_the_source_line_they_came_from()
    {
        var nc = "N1 G90\r\n\r\nN2 M6 T2\r\nN3 M3 S14500\r\nN4 G0 X10.0000 Y20.0000 Z30.0000\r\n\r\n\r\nN5 G1 Z0.5000 F1000.0\r\nN6 G1 X50.0000\r\nN7 M30\r\n";
        var lines = OsaiTroyLexer.Lex(nc);
        Assert.Equal(0, lines.First(l => l.N == 1).SourceLine);
        Assert.Equal(2, lines.First(l => l.N == 2).SourceLine);
        Assert.Equal(7, lines.First(l => l.N == 5).SourceLine);

        var replay = OsaiTroyParser.Replay(nc);
        Assert.Equal([4, 7, 8], replay.Strokes.Select(s => s.LineIndex).ToArray());
    }

    [Fact]
    public void Parser_replays_self_emitted_header()
    {
        var nc = EmitOffset(Outer());
        var replay = OsaiTroyParser.Replay(nc);
        Assert.True(replay.Strokes.Count > 4);
        Assert.Contains(replay.Lines, l => l.Paren == "UAO,1");
        Assert.Contains(replay.Strokes, s => s.ToolNum == 2 && !s.Rapid);
        Assert.True(replay.SafeZMm >= 20);
    }

    [Fact]
    public void Infer_merges_two_pass_contour_and_keeps_drill_tongue()
    {
        var nc = EmitOffset(Outer(), Drill(), Tongue());
        var result = NcReverse.FromText(nc);
        Assert.Contains(result.Ops, o => o.Op == "drill");
        Assert.Contains(result.Ops, o => o.Op == "groove" && o.IsTongue);
        Assert.Equal(1, result.Ops.Count(o => o.Op == "contour"));
    }

    [Fact]
    public void Reverse_recovers_panel_bounds_and_hole()
    {
        var nc = EmitOffset(Outer(), Drill());
        var result = NcReverse.FromText(nc);
        Assert.DoesNotContain("no_contour", result.Warnings);
        Assert.DoesNotContain("no_panel", result.Warnings);
        Assert.Single(result.Panels);
        var panel = result.Panels[0];
        var w = panel.Outline.Points.Max(p => p.X) - panel.Outline.Points.Min(p => p.X);
        var h = panel.Outline.Points.Max(p => p.Y) - panel.Outline.Points.Min(p => p.Y);
        Assert.InRange(w, 196, 204);
        Assert.InRange(h, 96, 104);
        Assert.Contains(panel.Features, f => f.Kind.Contains("hole", StringComparison.OrdinalIgnoreCase));
        var hole = panel.Features.First(f => f.Kind.Contains("hole", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(hole.X, 25, 35);
        Assert.InRange(hole.Y, 35, 45);
    }

    [Fact]
    public void Reverse_two_outers_become_two_panels()
    {
        var a = Outer();
        var b = a with
        {
            PanelId = "P2",
            Path = [(300, 0), (400, 0), (400, 80), (300, 80)],
        };
        var nc = EmitOffset(a, b);
        var result = NcReverse.FromText(nc);
        Assert.Equal(2, result.Panels.Count);
    }

    /// <summary>A 40×50 window has sides inside the ramp-strip range; it must stay a closed contour.</summary>
    [Fact]
    public void Infer_keeps_small_closed_window_as_contour()
    {
        var nc = EmitOffset(Outer(), InnerCutout());
        var result = NcReverse.FromText(nc);
        Assert.Equal(2, result.Ops.Count(o => o.Op == "contour"));
    }

    [Fact]
    public void Reverse_recovers_inner_cutout_not_cutter_center()
    {
        var nc = EmitOffset(Outer(), InnerCutout());
        var result = NcReverse.FromText(nc);
        Assert.Single(result.Panels);
        var cut = result.Panels[0].Features.Single(f => f.Kind == "cutout");
        Assert.NotNull(cut.Path);
        var w = cut.Path!.Max(p => p.X) - cut.Path.Min(p => p.X);
        var h = cut.Path.Max(p => p.Y) - cut.Path.Min(p => p.Y);
        Assert.InRange(w, 36, 44);
        Assert.InRange(h, 46, 54);
        Assert.InRange(cut.Path.Min(p => p.X), 36, 44);
        Assert.InRange(cut.Path.Min(p => p.Y), 16, 24);
    }

    /// <summary>
    /// Travel-optimised posts enter the through pass at a different corner than the leave
    /// pass and may run it the other way round; both still describe one panel.
    /// </summary>
    [Fact]
    public void Reverse_merges_passes_with_different_entry_vertex_and_direction()
    {
        var nc = """
            N1 G90
            N2 G40
            N3 (UAO,1)
            N4 M6 T2
            N5 M3 S14500
            N6 G0 X-5.0000 Y-5.0000 Z30.0000
            N7 G1 Z0.5000 F1000.0
            N8 G1 Y105.0000 F12000.0
            N9 G1 X205.0000
            N10 G1 Y-5.0000
            N11 G1 X-5.0000
            N12 G0 Z30.0000
            N13 G0 X205.0000 Y105.0000
            N14 G1 Z-0.5500 F1000.0
            N15 G1 X-5.0000 F20000.0
            N16 G1 Y-5.0000
            N17 G1 X205.0000
            N18 G1 Y105.0000
            N19 G0 Z30.0000
            N20 M5
            N21 M30
            """;
        var result = NcReverse.FromText(nc);
        Assert.Equal(1, result.Ops.Count(o => o.Op == "contour"));
        var panel = Assert.Single(result.Panels);
        var w = panel.Outline.Points.Max(p => p.X) - panel.Outline.Points.Min(p => p.X);
        var h = panel.Outline.Points.Max(p => p.Y) - panel.Outline.Points.Min(p => p.Y);
        Assert.InRange(w, 196, 204);
        Assert.InRange(h, 96, 104);
    }

    /// <summary>Leave pass, through pass and a spring (repeat through) pass are one loop.</summary>
    [Fact]
    public void Reverse_merges_three_passes_into_one_panel()
    {
        var nc = """
            N1 G90
            N2 G40
            N3 (UAO,1)
            N4 M6 T2
            N5 M3 S14500
            N6 G0 X-5.0000 Y-5.0000 Z30.0000
            N7 G1 Z0.5000 F1000.0
            N8 G1 Y105.0000 F12000.0
            N9 G1 X205.0000
            N10 G1 Y-5.0000
            N11 G1 X-5.0000
            N12 G0 Z30.0000
            N13 G0 X205.0000 Y105.0000
            N14 G1 Z-0.5500 F1000.0
            N15 G1 X-5.0000 F20000.0
            N16 G1 Y-5.0000
            N17 G1 X205.0000
            N18 G1 Y105.0000
            N19 G0 Z30.0000
            N20 G0 X205.0000 Y-5.0000
            N21 G1 Z-0.5500 F1000.0
            N22 G1 Y105.0000 F20000.0
            N23 G1 X-5.0000
            N24 G1 Y-5.0000
            N25 G1 X205.0000
            N26 G0 Z30.0000
            N27 M5
            N28 M30
            """;
        var result = NcReverse.FromText(nc);
        var contour = Assert.Single(result.Ops, o => o.Op == "contour");
        Assert.True(contour.Through);
        Assert.Single(result.Panels);
    }

    [Fact]
    public void SameLoop_ignores_start_vertex_direction_and_tessellation()
    {
        (double, double)[] rect = [(0, 0), (200, 0), (200, 100), (0, 100), (0, 0)];
        (double, double)[] rotated = [(200, 100), (0, 100), (0, 0), (200, 0), (200, 100)];
        (double, double)[] reversed = [(0, 0), (0, 100), (200, 100), (200, 0), (0, 0)];
        (double, double)[] dense = [(0, 0), (100, 0), (200, 0), (200, 50), (200, 100), (100, 100), (0, 100), (0, 50), (0, 0)];
        (double, double)[] neighbour = [(300, 0), (400, 0), (400, 80), (300, 80), (300, 0)];
        (double, double)[] shifted = [(4, 0), (204, 0), (204, 100), (4, 100), (4, 0)];

        Assert.True(NcProcessInfer.SameLoop(rect, rotated));
        Assert.True(NcProcessInfer.SameLoop(rect, reversed));
        Assert.True(NcProcessInfer.SameLoop(rect, dense));
        Assert.False(NcProcessInfer.SameLoop(rect, neighbour));
        Assert.False(NcProcessInfer.SameLoop(rect, shifted));
    }

    [Fact]
    public void Recut_keeps_only_selected_panel_at_qty_one()
    {
        var a = Outer();
        var b = a with
        {
            PanelId = "P2",
            Path = [(300, 0), (400, 0), (400, 80), (300, 80)],
        };
        var nc = EmitOffset(a, b);
        var result = NcReverse.FromText(nc);
        Assert.Equal(2, result.Panels.Count);
        var keep = result.Panels[0].WithQuantity(1);
        var pkg = NcReverse.ToPackage(result, "recut").WithPanels([keep]);
        Assert.Single(pkg.Panels);
        Assert.Equal(1, pkg.Panels[0].Quantity);
    }

    [Fact]
    public void Package_from_reverse_is_cut_package()
    {
        var nc = EmitOffset(Outer());
        var result = NcReverse.FromText(nc);
        var pkg = NcReverse.ToPackage(result, "job-anc");
        Assert.Equal("job-anc", pkg.JobId);
        Assert.Equal(CutPackage.Schema, pkg.SchemaName);
        Assert.Single(pkg.Panels);
        Assert.Single(pkg.Sheets);
    }

}

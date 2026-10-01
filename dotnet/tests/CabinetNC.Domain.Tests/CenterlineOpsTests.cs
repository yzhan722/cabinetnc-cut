using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class CenterlineOpsTests
{
    static Panel Board() => new()
    {
        PanelId = "P1",
        ThicknessMm = 18,
        Outline = new Outline { Points = [new(0, 0), new(600, 0), new(600, 400), new(0, 400)] },
        Features =
        [
            // Horizontal groove across the board at y=100.
            new PanelFeature { FeatureId = "G1", Kind = "grooveVertical", X = 0, Y = 100, WidthMm = 6, DepthMm = 8, Path = [new(0, 100), new(600, 100)] },
            // Vertical grooves crossing it at x=200 and x=400.
            new PanelFeature { FeatureId = "G2", Kind = "grooveVertical", X = 200, Y = 0, WidthMm = 6, DepthMm = 8, Path = [new(200, 0), new(200, 400)] },
            new PanelFeature { FeatureId = "G3", Kind = "grooveVertical", X = 400, Y = 0, WidthMm = 6, DepthMm = 8, Path = [new(400, 0), new(400, 400)] },
            // Short stub that stops before G1.
            new PanelFeature { FeatureId = "G4", Kind = "grooveVertical", X = 300, Y = 300, WidthMm = 6, DepthMm = 8, Path = [new(300, 300), new(300, 200)] },
            new PanelFeature { FeatureId = "H1", Kind = "holeVertical", X = 500, Y = 300, DiameterMm = 35 },
        ],
    };

    [Fact]
    public void CutParams_two_crossings_sorted()
    {
        var cuts = CenterlineOps.CutParams([new(0, 100), new(600, 100)], [[new(200, 0), new(200, 400)], [new(400, 0), new(400, 400)]]);
        Assert.Equal(2, cuts.Count);
        Assert.Equal(200.0 / 600, cuts[0], 6);
        Assert.Equal(400.0 / 600, cuts[1], 6);
    }

    [Fact]
    public void CutParams_collinear_overlap_yields_interval_ends()
    {
        var cuts = CenterlineOps.CutParams([new(0, 0), new(100, 0)], [[new(30, 0), new(60, 0)]]);
        Assert.Equal(2, cuts.Count);
        Assert.Equal(0.3, cuts[0], 6);
        Assert.Equal(0.6, cuts[1], 6);
    }

    [Fact]
    public void Trim_middle_piece_leaves_two()
    {
        var kept = CenterlineOps.Trim([new(0, 100), new(600, 100)], [[new(200, 0), new(200, 400)], [new(400, 0), new(400, 400)]], new(300, 100));
        Assert.NotNull(kept);
        Assert.Equal(2, kept!.Count);
        Assert.Equal(new Point2(0, 100), kept[0][0]);
        Assert.Equal(200, kept[0][^1].X, 6);
        Assert.Equal(400, kept[1][0].X, 6);
        Assert.Equal(600, kept[1][^1].X, 6);
    }

    [Fact]
    public void Trim_end_piece_shortens()
    {
        var kept = CenterlineOps.Trim([new(0, 100), new(600, 100)], [[new(200, 0), new(200, 400)]], new(50, 100));
        Assert.Single(kept!);
        Assert.Equal(200, kept![0][0].X, 6);
        Assert.Equal(600, kept[0][^1].X, 6);
    }

    [Fact]
    public void Trim_without_intersection_is_null()
    {
        var kept = CenterlineOps.Trim([new(0, 100), new(600, 100)], [[new(200, 200), new(200, 400)]], new(50, 100));
        Assert.Null(kept);
    }

    [Fact]
    public void Extend_hits_nearest_boundary_ahead()
    {
        var path = CenterlineOps.Extend([new(300, 300), new(300, 200)], [[new(0, 100), new(600, 100)], [new(0, 0), new(600, 0)]], new(300, 210));
        Assert.NotNull(path);
        Assert.Equal(100, path![^1].Y, 6);
        Assert.Equal(300, path[0].Y, 6);
    }

    [Fact]
    public void Extend_other_end_when_picked_near_it()
    {
        var path = CenterlineOps.Extend([new(300, 300), new(300, 200)], [[new(0, 400), new(600, 400)]], new(300, 290));
        Assert.NotNull(path);
        Assert.Equal(400, path![0].Y, 6);
    }

    [Fact]
    public void Classify_overlap_kinds()
    {
        IReadOnlyList<Point2> g = [new(0, 100), new(600, 100)];
        Assert.Equal(CenterlineOverlap.Duplicate, CenterlineOps.Classify([new(600, 100.2), new(0, 100)], g));
        Assert.Equal(CenterlineOverlap.Collinear, CenterlineOps.Classify([new(100, 100), new(300, 100)], g));
        Assert.Equal(CenterlineOverlap.Crossing, CenterlineOps.Classify([new(300, 0), new(300, 400)], g));
        Assert.Equal(CenterlineOverlap.Crossing, CenterlineOps.Classify([new(300, 100), new(300, 400)], g)); // T
        Assert.Equal(CenterlineOverlap.None, CenterlineOps.Classify([new(0, 200), new(600, 200)], g));
    }

    [Fact]
    public void TrimGroove_keeps_id_on_first_piece_and_suffixes_rest()
    {
        var next = PanelEdit.TrimGroove(Board(), "G1", null, new(300, 100), out var err);
        Assert.Null(err);
        Assert.NotNull(next);
        var pieces = next!.Features.Where(f => f.FeatureId.StartsWith("G1", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal("G1", pieces[0].FeatureId);
        Assert.Equal("G1_t", pieces[1].FeatureId);
        Assert.Equal(6, pieces[1].WidthMm);
        Assert.Equal(8, pieces[1].DepthMm);
        Assert.Equal(400, pieces[1].X, 6);
        Assert.Null(pieces[1].Profile);
        // Order preserved: pieces sit where G1 was.
        Assert.Equal("G1", next.Features[0].FeatureId);
        Assert.Equal("G1_t", next.Features[1].FeatureId);
        Assert.Equal("G2", next.Features[2].FeatureId);
        Assert.Equal(EditImpact.FeaturesOnly, PanelEdit.ClassifyChange(Board(), next));
    }

    [Fact]
    public void TrimGroove_with_explicit_cutters_ignores_others()
    {
        var next = PanelEdit.TrimGroove(Board(), "G1", ["G2"], new(300, 100), out _);
        var g1 = next!.Features.Single(f => f.FeatureId == "G1");
        Assert.Equal(0, g1.Path![0].X, 6);
        Assert.Equal(200, g1.Path[^1].X, 6);
        Assert.DoesNotContain(next.Features, f => f.FeatureId == "G1_t");
    }

    [Fact]
    public void TrimGroove_outline_is_always_a_cutter()
    {
        // Groove running off the board: trim the outside piece against the outline only.
        var p = Board();
        var over = new PanelFeature { FeatureId = "G9", Kind = "grooveVertical", X = 100, Y = 350, WidthMm = 6, DepthMm = 8, Path = [new(100, 350), new(100, 450)] };
        p = new Panel
        {
            PanelId = p.PanelId, ThicknessMm = p.ThicknessMm, Outline = p.Outline,
            Features = p.Features.Append(over).ToList(),
        };
        var next = PanelEdit.TrimGroove(p, "G9", [], new(100, 440), out var err);
        Assert.Null(err);
        var g9 = next!.Features.Single(f => f.FeatureId == "G9");
        Assert.Equal(400, g9.Path![^1].Y, 6);
    }

    [Fact]
    public void ExtendGroove_to_crossing_groove()
    {
        var next = PanelEdit.ExtendGroove(Board(), "G4", null, new(300, 210), out var err);
        Assert.Null(err);
        var g4 = next!.Features.Single(f => f.FeatureId == "G4");
        Assert.Equal(100, g4.Path![^1].Y, 6);
    }

    [Fact]
    public void ExtendGroove_to_outline_when_nothing_else()
    {
        var next = PanelEdit.ExtendGroove(Board(), "G4", [], new(300, 290), out _);
        var g4 = next!.Features.Single(f => f.FeatureId == "G4");
        Assert.Equal(400, g4.Path![0].Y, 6);
    }

    [Fact]
    public void ExtendGroove_rejects_hole()
    {
        var next = PanelEdit.ExtendGroove(Board(), "H1", null, new(500, 300), out var err);
        Assert.Null(next);
        Assert.NotNull(err);
    }

    [Fact]
    public void ClassifyGrooveOverlap_reports_worst_and_culprit()
    {
        var k = PanelEdit.ClassifyGrooveOverlap(Board(), [new(0, 100), new(600, 100)], out var other);
        Assert.Equal(CenterlineOverlap.Duplicate, k);
        Assert.Equal("G1", other);
        k = PanelEdit.ClassifyGrooveOverlap(Board(), [new(0, 250), new(600, 250)], out other);
        Assert.Equal(CenterlineOverlap.Crossing, k);
        Assert.Contains(other, new[] { "G2", "G3", "G4" });
    }

    [Fact]
    public void AddVerticalGroove_through_flag()
    {
        var next = PanelEdit.AddVerticalGroove(Board(), [new(50, 50), new(50, 350)], 6, 18, through: true);
        Assert.True(next.Features[^1].Through);
        Assert.Equal(EditImpact.FeaturesOnly, PanelEdit.ClassifyChange(Board(), next));
    }

    static IReadOnlyList<Point2> RectRing() => [new(0, 0), new(600, 0), new(600, 400), new(0, 400)];

    [Fact]
    public void NotchRing_cuts_sw_corner_keeps_board()
    {
        var next = CenterlineOps.NotchRing(RectRing(), [new(50, 0), new(0, 50)]);
        Assert.NotNull(next);
        Assert.Equal(5, next!.Count);
        Assert.Contains(next, p => Math.Abs(p.X - 50) < 1e-6 && Math.Abs(p.Y) < 1e-6);
        Assert.Contains(next, p => Math.Abs(p.X) < 1e-6 && Math.Abs(p.Y - 50) < 1e-6);
        Assert.DoesNotContain(next, p => Math.Abs(p.X) < 1e-6 && Math.Abs(p.Y) < 1e-6);
        Assert.True(Math.Abs(CenterlineOps.SignedArea(next)) > 200_000);
    }

    [Fact]
    public void NotchRing_through_cut_keeps_a_half()
    {
        var next = CenterlineOps.NotchRing(RectRing(), [new(0, 200), new(600, 200)]);
        Assert.NotNull(next);
        Assert.Equal(4, next!.Count);
        Assert.Equal(120_000, Math.Abs(CenterlineOps.SignedArea(next)), 0);
    }

    [Fact]
    public void NotchRing_one_hit_is_null()
    {
        Assert.Null(CenterlineOps.NotchRing(RectRing(), [new(300, 200), new(300, 500)]));
    }

    [Fact]
    public void ReplaceOutline_flips_winding_to_match()
    {
        var p = Board();
        var next = PanelEdit.ReplaceOutline(p, [new(0, 0), new(0, 300), new(400, 300), new(400, 0)], out var err);
        Assert.Null(err);
        Assert.Equal(4, next!.Outline.Points.Count);
        Assert.Equal(Math.Sign(CenterlineOps.SignedArea(p.Outline.Points)), Math.Sign(CenterlineOps.SignedArea(next.Outline.Points)));
        Assert.Equal(EditImpact.Nest, PanelEdit.ClassifyChange(p, next));
    }

    [Fact]
    public void NotchOutline_corner()
    {
        var next = PanelEdit.NotchOutline(Board(), [new(50, 0), new(0, 50)], out var err);
        Assert.Null(err);
        Assert.Equal(5, next!.Outline.Points.Count);
        Assert.Equal(EditImpact.Nest, PanelEdit.ClassifyChange(Board(), next));
    }

    [Fact]
    public void NotchOutline_refuses_miss()
    {
        var next = PanelEdit.NotchOutline(Board(), [new(50, 50), new(80, 80)], out var err);
        Assert.Null(next);
        Assert.Contains("不相交", err);
    }

    [Fact]
    public void TrimOutline_drops_picked_corner_between_two_grooves()
    {
        // G2 at x=200 and left outline x=0: pick bottom-left edge, but those don't share a cutter.
        // Use G2 (x=200 through) and pick the left piece of the bottom edge — only one hit on that
        // edge from G2 at (200,0). Need two cutters that both hit the ring.
        var p = Board();
        var next = PanelEdit.TrimOutline(p, ["G2", "G3"], new(300, 0), out var err);
        Assert.Null(err);
        // Bottom edge between G2 (200,0) and G3 (400,0) dropped, bridged straight.
        Assert.DoesNotContain(next!.Outline.Points, q => Math.Abs(q.Y) < 1e-6 && q.X > 210 && q.X < 390);
        Assert.Contains(next.Outline.Points, q => Math.Abs(q.X - 200) < 1e-3 && Math.Abs(q.Y) < 1e-3);
        Assert.Contains(next.Outline.Points, q => Math.Abs(q.X - 400) < 1e-3 && Math.Abs(q.Y) < 1e-3);
    }
}

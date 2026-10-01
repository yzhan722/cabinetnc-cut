using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class PanelEditTests
{
    static Panel Rect() => new()
    {
        PanelId = "P1",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points = [new(0, 0), new(600, 0), new(600, 400), new(0, 400)],
        },
        Features =
        [
            new PanelFeature { FeatureId = "H1", Kind = "holeVertical", X = 80, Y = 80, DiameterMm = 35 },
            new PanelFeature
            {
                FeatureId = "G1",
                Kind = "grooveVertical",
                X = 0,
                Y = 50,
                WidthMm = 6,
                Path = [new(0, 50), new(600, 50)],
            },
        ],
    };

    [Fact]
    public void MoveHole_updates_xy()
    {
        var next = PanelEdit.MoveHole(Rect(), "H1", 100, 120);
        var h = next.Features.Single(f => f.FeatureId == "H1");
        Assert.Equal(100, h.X);
        Assert.Equal(120, h.Y);
    }

    [Fact]
    public void Resize_scales_features()
    {
        var next = PanelEdit.ResizeFromEdges(Rect(), 0, 0, 300, 200);
        var h = next.Features.Single(f => f.FeatureId == "H1");
        Assert.Equal(40, h.X, 3);
        Assert.Equal(40, h.Y, 3);
    }

    [Fact]
    public void HitTest_finds_hole()
    {
        var p = Rect();
        var view = GeomInteraction.BuildView(p, 800, 600);
        var (sx, sy) = GeomInteraction.ToScreen(view, 80, 80);
        var hit = GeomInteraction.HitTest(p, view, sx, sy);
        Assert.NotNull(hit);
        Assert.Equal("hole", hit!.Value.Type);
        Assert.Equal("H1", hit.Value.FeatureId);
    }

    [Fact]
    public void MirrorX_flips_coords_and_edge_banding()
    {
        var p = Rect();
        p = new Panel
        {
            PanelId = p.PanelId,
            ThicknessMm = p.ThicknessMm,
            Outline = p.Outline,
            Features = p.Features,
            Side = "A",
            EdgeBanding = new EdgeBanding { Left = "L", Right = "R", Front = "F", Back = "B" },
            Orientation = new WorkpieceOrientation { MillingFace = "A", AllowMirror = true },
        };
        var next = PanelEdit.Mirror(p, "X");
        var h = next.Features.Single(f => f.FeatureId == "H1");
        Assert.Equal(520, h.X, 3); // 2*300 - 80
        Assert.Equal(80, h.Y, 3);
        Assert.Equal("R", next.EdgeBanding!.Left);
        Assert.Equal("L", next.EdgeBanding.Right);
        Assert.Equal("B", next.Side);
        Assert.Equal("B", next.Orientation!.MillingFace);
        Assert.Equal("x", next.Orientation.FlipStrategy);
    }

    [Fact]
    public void Duplicate_assigns_new_ids()
    {
        var next = PanelEdit.Duplicate(Rect(), "P1_copy");
        Assert.Equal("P1_copy", next.PanelId);
        Assert.DoesNotContain(next.Features, f => f.FeatureId == "H1");
        Assert.Contains(next.Features, f => f.FeatureId.StartsWith("H1"));
        Assert.Equal(80, next.Features.First(f => f.FeatureId.StartsWith("H1")).X);
    }

    static Panel WithPocketAndCutout()
    {
        var p = Rect();
        var feats = p.Features.ToList();
        feats.Add(new PanelFeature
        {
            FeatureId = "P1",
            Kind = "pocket",
            X = 200,
            Y = 200,
            DepthMm = 5,
            Path = [new(200, 200), new(300, 200), new(300, 260), new(200, 260)],
            Profile = [new(200, 200), new(300, 200), new(300, 260), new(200, 260)],
            Holes = [[new(230, 220), new(260, 220), new(260, 240), new(230, 240)]],
        });
        feats.Add(new PanelFeature
        {
            FeatureId = "C1",
            Kind = "throughCutout",
            Through = true,
            X = 400,
            Y = 100,
            Path = [new(400, 100), new(450, 100), new(450, 150), new(400, 150)],
        });
        return new Panel { PanelId = p.PanelId, ThicknessMm = p.ThicknessMm, Outline = p.Outline, Features = feats };
    }

    [Fact]
    public void TranslateFeature_moves_only_target_and_keeps_id()
    {
        var p = WithPocketAndCutout();
        var next = PanelEdit.TranslateFeature(p, "G1", 200, 0);

        var g = next.Features.Single(f => f.FeatureId == "G1");
        Assert.Equal(200, g.Path![0].X, 6);
        Assert.Equal(800, g.Path![1].X, 6);
        Assert.Equal(200, g.X, 6); // anchor follows the path

        var h = next.Features.Single(f => f.FeatureId == "H1");
        Assert.Equal(80, h.X, 6);
        Assert.Equal(4, next.Features.Count);
    }

    [Fact]
    public void TranslateFeature_moves_pocket_islands_too()
    {
        var p = WithPocketAndCutout();
        var next = PanelEdit.TranslateFeature(p, "P1", 10, 20);
        var pocket = next.Features.Single(f => f.FeatureId == "P1");
        Assert.Equal(210, pocket.Path![0].X, 6);
        Assert.Equal(220, pocket.Path![0].Y, 6);
        Assert.Equal(240, pocket.Holes![0][0].X, 6);
        Assert.Equal(240, pocket.Holes![0][0].Y, 6);
        Assert.Equal(210, pocket.Profile![0].X, 6);
    }

    [Fact]
    public void CopyFeatures_adds_clone_with_new_id()
    {
        var p = Rect();
        var (next, ids) = PanelEdit.CopyFeatures(p, ["H1"], 100, 0);
        Assert.Equal(["H1_c"], ids);
        Assert.Equal(3, next.Features.Count);
        Assert.Equal(80, next.Features.Single(f => f.FeatureId == "H1").X, 6);
        Assert.Equal(180, next.Features.Single(f => f.FeatureId == "H1_c").X, 6);

        var (again, ids2) = PanelEdit.CopyFeatures(next, ["H1"], 200, 0);
        Assert.Equal(["H1_c2"], ids2);
        Assert.Equal(4, again.Features.Count);
    }

    [Fact]
    public void RemoveFeatures_drops_listed_ids()
    {
        var next = PanelEdit.RemoveFeatures(Rect(), ["H1", "nope"]);
        Assert.Single(next.Features);
        Assert.Equal("G1", next.Features[0].FeatureId);
    }

    [Fact]
    public void IsFeatureInsideOutline_accepts_edge_groove_rejects_offboard()
    {
        var p = Rect();
        var groove = p.Features.Single(f => f.FeatureId == "G1"); // 0..600 on the edge
        Assert.True(PanelEdit.IsFeatureInsideOutline(p, groove));

        var moved = PanelEdit.TranslateFeature(p, "H1", 600, 0);
        Assert.Equal(["H1"], PanelEdit.FeaturesOutsideOutline(moved, ["H1", "G1"]).ToList());
    }

    [Fact]
    public void FeatureOutsideMm_measures_worst_overhang()
    {
        var p = Rect();
        Assert.Equal(0, PanelEdit.FeatureOutsideMm(p, "G1"), 6);
        Assert.Equal(0, PanelEdit.FeatureOutsideMm(p, "H1"), 6);

        // Hole ⌀35 at x=80 pushed to x=590 → rim reaches 607.5, i.e. 7.5mm past the edge.
        var moved = PanelEdit.TranslateFeature(p, "H1", 510, 0);
        Assert.Equal(7.5, PanelEdit.FeatureOutsideMm(moved, "H1"), 6);

        // Groove overhanging 10mm on the left; sliding it down does not change the overhang.
        var over = PanelEdit.TranslateFeature(p, "G1", -10, 100);
        Assert.Equal(10, PanelEdit.FeatureOutsideMm(over, "G1"), 6);
        Assert.Equal(10, PanelEdit.FeatureOutsideMm(PanelEdit.TranslateFeature(over, "G1", 0, 50), "G1"), 6);
        Assert.Equal(0, PanelEdit.FeatureOutsideMm(p, "missing"), 6);
    }

    [Fact]
    public void IsFeatureInsideOutline_uses_polygon_not_bbox()
    {
        // L-shaped board: notch in the top-right quadrant.
        var l = new Panel
        {
            PanelId = "L",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(600, 0), new(600, 200), new(300, 200), new(300, 400), new(0, 400)],
            },
            Features =
            [
                new PanelFeature { FeatureId = "H1", Kind = "holeVertical", X = 450, Y = 300, DiameterMm = 8 },
            ],
        };
        Assert.False(PanelEdit.IsFeatureInsideOutline(l, l.Features[0]));
    }

    [Fact]
    public void ClassifyChange_feature_move_is_cam_only()
    {
        var p = WithPocketAndCutout();
        Assert.Equal(EditImpact.None, PanelEdit.ClassifyChange(p, p));
        Assert.Equal(EditImpact.FeaturesOnly, PanelEdit.ClassifyChange(p, PanelEdit.TranslateFeature(p, "G1", 200, 0)));
        Assert.Equal(EditImpact.FeaturesOnly, PanelEdit.ClassifyChange(p, PanelEdit.TranslateFeature(p, "P1", 5, 5)));
        Assert.Equal(EditImpact.FeaturesOnly, PanelEdit.ClassifyChange(p, PanelEdit.UpdateFeatureParams(p, "H1", depthMm: 12)));
    }

    [Fact]
    public void ClassifyChange_outline_or_cutout_change_needs_nest()
    {
        var p = WithPocketAndCutout();
        Assert.Equal(EditImpact.Nest, PanelEdit.ClassifyChange(p, PanelEdit.TranslateFeature(p, "C1", 10, 0)));
        Assert.Equal(EditImpact.Nest, PanelEdit.ClassifyChange(p, PanelEdit.RemoveFeatures(p, ["C1"])));
        Assert.Equal(EditImpact.Nest, PanelEdit.ClassifyChange(p, PanelEdit.ResizeFromEdges(p, 0, 0, 500, 400)));
    }

    [Fact]
    public void ReplaceGeometry_keeps_target_identity()
    {
        var src = PanelEdit.TranslateFeature(Rect(), "H1", 50, 0);
        var target = new Panel
        {
            PanelId = "P2",
            Name = "OHC_2-BP",
            ThicknessMm = 18,
            Quantity = 3,
            Outline = Rect().Outline,
            Features = Rect().Features,
        };
        var next = PanelEdit.ReplaceGeometry(target, src);
        Assert.Equal("P2", next.PanelId);
        Assert.Equal("OHC_2-BP", next.Name);
        Assert.Equal(3, next.Quantity);
        Assert.Equal(130, next.Features.Single(f => f.FeatureId == "H1").X, 6);
    }

    [Fact]
    public void IsSmallPanel_by_short_edge()
    {
        var p = new Panel
        {
            PanelId = "S",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(70, 0), new(70, 200), new(0, 200)] },
        };
        Assert.True(PanelEdit.IsSmallPanel(p, out var reason));
        Assert.Contains("80", reason);
    }
}

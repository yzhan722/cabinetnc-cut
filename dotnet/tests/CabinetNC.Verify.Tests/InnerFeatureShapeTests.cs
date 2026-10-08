using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing.Verification;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Xunit.Abstractions;
using static CabinetNC.Verify.Tests.VerifyFixture;

namespace CabinetNC.Verify.Tests;

/// <summary>
/// Inner slots and pockets are where 计码 goes wrong in the shop. These exercise the real
/// planner (PocketClearer / GrooveClear) on awkward shapes and require the verifier to
/// agree the floor is cleared and nothing outside the feature is touched.
/// </summary>
public class InnerFeatureShapeTests(ITestOutputHelper output)
{
    static PanelFeature Poly(string id, string kind, double depth, params (double X, double Y)[] ring) => new()
    {
        FeatureId = id,
        Kind = kind,
        DepthMm = depth,
        FaceId = "A",
        Path = ring.Select(p => new Point2(p.X, p.Y)).ToList(),
        X = ring.Average(p => p.X),
        Y = ring.Average(p => p.Y),
    };

    VerifyReport RunTroy(Panel p, double rot = 0)
    {
        var places = new List<NestPlacement> { Place(p.PanelId, 20, 20, rot) };
        var r = Run(Input([p], places, EmitTroy(PlanOps([p], places))));
        output.WriteLine(Describe(r));
        return r;
    }

    VerifyReport RunGeneric(Panel p)
    {
        var places = new List<NestPlacement> { Place(p.PanelId, 20, 20) };
        var r = Run(Input([p], places, EmitGeneric(PlanOps([p], places)), ZDatum.BoardTop));
        output.WriteLine(Describe(r));
        return r;
    }

    [Fact]
    public void Tongue_groove_at_tool_width_follows_centreline()
    {
        var g = Groove("T", new Point2(20, 150), new Point2(380, 150), 6.35, 9);
        var tongue = new PanelFeature
        {
            FeatureId = g.FeatureId, Kind = g.Kind, DepthMm = g.DepthMm, WidthMm = g.WidthMm,
            FaceId = g.FaceId, Path = g.Path, X = g.X, Y = g.Y, Purpose = "tongue",
        };
        var r = RunTroy(Rect("A", 400, 300, 18, tongue));
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Groove_running_off_both_panel_edges_passes()
    {
        var r = RunTroy(Rect("A", 400, 300, 18, Groove("G", new Point2(0, 150), new Point2(400, 150), 10, 8)));
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Edge_open_slot_pocket_passes()
    {
        var r = RunTroy(Rect("A", 400, 300, 18, Pocket("S", 0, 100, 60, 116, 8)));
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void U_pocket_both_arms_cleared()
    {
        var u = Poly("U", "pocket", 8,
            (100, 50), (200, 50), (200, 130), (180, 130), (180, 70), (120, 70), (120, 130), (100, 130));
        var r = RunTroy(Rect("A", 400, 300, 18, u));
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Pinched_peanut_pocket_both_lobes_cleared()
    {
        // Two Ø60 lobes at x=80 and x=180 joined by a 16 mm neck; CCW outline.
        const double R = 30, neck = 8;
        var t0 = Math.Asin(neck / R);
        var pts = new List<(double X, double Y)>();
        for (var i = 0; i <= 40; i++) // left lobe: from top of neck around the far side to bottom of neck
        {
            var a = t0 + (2 * Math.PI - 2 * t0) * i / 40;
            pts.Add((80 + R * Math.Cos(a), 150 + R * Math.Sin(a)));
        }
        for (var i = 0; i <= 40; i++) // right lobe: from bottom of neck around the far side to top of neck
        {
            var a = Math.PI + t0 + (2 * Math.PI - 2 * t0) * i / 40;
            pts.Add((180 + R * Math.Cos(a), 150 + R * Math.Sin(a)));
        }
        var r = RunTroy(Rect("A", 400, 300, 18, Poly("PN", "pocket", 6, pts.ToArray())));
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Pocket_with_island_keeps_the_island()
    {
        var pocket = Pocket("R", 60, 60, 260, 240, 6);
        var withIsland = new PanelFeature
        {
            FeatureId = pocket.FeatureId, Kind = pocket.Kind, DepthMm = pocket.DepthMm, FaceId = pocket.FaceId,
            Path = pocket.Path, X = pocket.X, Y = pocket.Y,
            Holes = [[new(130, 120), new(190, 120), new(190, 180), new(130, 180)]],
        };
        var r = RunTroy(Rect("A", 400, 300, 18, withIsland));
        Assert.True(r.Ok, Describe(r));
        Assert.DoesNotContain(r.Issues, i => i.Code == VerifyCodes.BlindOvercut);
    }

    [Fact]
    public void Rotated_panel_with_groove_and_slot_passes()
    {
        var r = RunTroy(Rect("A", 400, 300, 18,
            Groove("G", new Point2(30, 180), new Point2(370, 180), 12, 6),
            Pocket("S", 0, 40, 60, 56, 8)), rot: 90);
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Generic_post_stepdown_passes_reach_full_depth()
    {
        var r = RunGeneric(Rect("A", 400, 300, 18,
            Pocket("P", 100, 100, 220, 200, 12),
            Groove("G", new Point2(30, 40), new Point2(370, 40), 10, 9)));
        Assert.True(r.Ok, Describe(r));
    }

    [Fact]
    public void Groove_cleared_too_shallow_is_caught()
    {
        var cad = Rect("A", 400, 300, 18, Groove("G", new Point2(30, 150), new Point2(370, 150), 10, 9));
        var made = Rect("A", 400, 300, 18, Groove("G", new Point2(30, 150), new Point2(370, 150), 10, 6));
        var places = new List<NestPlacement> { Place("A", 20, 20) };
        var r = Run(Input([cad], places, EmitTroy(PlanOps([made], places))));
        output.WriteLine(Describe(r));
        Assert.Contains(r.Issues, i => i.Code == VerifyCodes.FeatureNotCut && i.FeatureId == "G");
    }
}

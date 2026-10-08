using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class CamSafetyTests
{
    static Panel Panel(string id, double th, bool withHole = true) => new()
    {
        PanelId = id,
        ThicknessMm = th,
        Material = "oak",
        Outline = new Outline
        {
            Points = [new(0, 0), new(200, 0), new(200, 100), new(0, 100)],
        },
        Features = withHole
        ?
        [
            new PanelFeature
            {
                FeatureId = "H1", Kind = "holeVertical",
                X = 30, Y = 30, DiameterMm = 3, DepthMm = th - 2,
            },
            new PanelFeature
            {
                FeatureId = "G1", Kind = "grooveVertical",
                WidthMm = 6, DepthMm = 6,
                Path = [new(20, 20), new(180, 20)],
            },
        ]
        : [],
    };

    [Fact]
    public void Tongue_ranks_before_clearance_groove_and_outer()
    {
        var drill = new CutOp { Op = "drill", PanelId = "A", FeatureId = "H1" };
        var tongue = new CutOp { Op = "groove", PanelId = "A", FeatureId = "TG", IsTongue = true };
        var inner = new CutOp { Op = "contour", PanelId = "A", FeatureId = "CUT1" };
        var outer = new CutOp { Op = "contour", PanelId = "A" };

        Assert.Equal(0, CamSafety.SequenceRank(drill));
        Assert.Equal(1, CamSafety.SequenceRank(tongue));
        Assert.Equal(2, CamSafety.SequenceRank(new CutOp { Op = "groove", PanelId = "A" }));
        Assert.Equal(2, CamSafety.SequenceRank(new CutOp { Op = "pocket", PanelId = "A" }));
        Assert.Equal(3, CamSafety.SequenceRank(inner));
        Assert.Equal(4, CamSafety.SequenceRank(outer));

        var ordered = CamSafety.OrderSafe([outer, tongue, drill, inner]).ToList();
        Assert.Equal("drill", ordered[0].Op);
        Assert.True(ordered[1].IsTongue);
        Assert.Equal("CUT1", ordered[2].FeatureId);
        Assert.True(string.IsNullOrWhiteSpace(ordered[3].FeatureId));
    }

    [Fact]
    public void Outer_follows_after_drill_and_groove()
    {
        var ops = OpsPlanner.FeaturesToOps([Panel("P1", 18)]).ToList();
        var ranks = ops.Select(CamSafety.SequenceRank).ToList();
        var drillIdx = ops.FindIndex(o => o.Op == "drill");
        var grooveIdx = ops.FindIndex(o => o.Op == "groove");
        var outerIdx = ops.FindIndex(o => o.Op == "contour" && o.FeatureId is null);
        Assert.True(drillIdx >= 0 && grooveIdx >= 0 && outerIdx >= 0);
        Assert.True(drillIdx < outerIdx);
        Assert.True(grooveIdx < outerIdx);
        Assert.Equal(4, ranks[outerIdx]);
    }

    [Fact]
    public void Mixed_thickness_outer_depths_differ()
    {
        var ops = OpsPlanner.FeaturesToOps([Panel("A", 15), Panel("B", 18)]);
        var outer15 = ops.Single(o => o.PanelId == "A" && o.Op == "contour" && o.FeatureId is null);
        var outer18 = ops.Single(o => o.PanelId == "B" && o.Op == "contour" && o.FeatureId is null);
        Assert.Equal(15.5, outer15.DepthMm);
        Assert.Equal(18.5, outer18.DepthMm);
    }

    [Fact]
    public void Illegal_depth_fails_preflight()
    {
        var panel = Panel("P1", 18);
        var ops = OpsPlanner.FeaturesToOps([panel])
            .Select(o => o with
            {
                Placed = true,
                Path = o.Path ?? [(0, 0), (10, 0), (10, 10)],
                SheetX = 10,
                SheetY = 10,
                DepthMm = o.Op == "groove" ? 40 : o.DepthMm,
            })
            .ToList();
        var report = NcPreflight.Check(
            ops,
            MachineCatalog.Get("nesting_router_6"),
            1220, 2440,
            new Dictionary<string, Panel> { ["P1"] = panel });
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Code is "groove_too_deep" or "depth_spoilboard");
    }

    [Fact]
    public void Nc_does_not_use_global_contour_depth_for_thin_panel()
    {
        var panel = Panel("T15", 15);
        var place = new NestPlacement { PanelId = "T15", SheetIndex = 0, OffsetX = 20, OffsetY = 20 };
        var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps([panel]), [place]);
        var profile = MachineCatalog.Get("nesting_router_6"); // ContourDepthMm default 18
        var nc = NcEmitter.OpsToNc(ops, profile);
        Assert.Contains("depth=15.5", nc);
        Assert.DoesNotContain("(contour T15 tool=T1 depth=18)", nc);
    }
}

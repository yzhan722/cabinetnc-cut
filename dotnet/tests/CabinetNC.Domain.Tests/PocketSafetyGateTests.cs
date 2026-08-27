using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class PocketSafetyGateTests
{
    static Panel PanelWithPocket(double? pocketDepth, double pocketW, double pocketH, double toolHint = 6.35)
    {
        _ = toolHint;
        return new Panel
        {
            PanelId = "P1",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "PK1",
                    Kind = "pocket",
                    DepthMm = pocketDepth,
                    Path =
                    [
                        new(10, 10),
                        new(10 + pocketW, 10),
                        new(10 + pocketW, 10 + pocketH),
                        new(10, 10 + pocketH),
                    ],
                },
            ],
        };
    }

    [Fact]
    public void Pocket_without_depth_fails_preflight_pocket_depth_missing()
    {
        var panel = PanelWithPocket(pocketDepth: null, pocketW: 100, pocketH: 80);
        var ops = OpsPlanner.FeaturesToOps([panel])
            .Select(o => o with { Placed = true, SheetIndex = 0 })
            .ToList();
        var pocket = Assert.Single(ops, o => o.Op == "pocket");
        Assert.True(pocket.DepthMm is null or <= 0,
            "missing pocket depth must not be silently filled with panel thickness");

        var report = NcPreflight.Check(
            ops,
            MachineCatalog.Get("nesting_router_6"),
            1220, 2440,
            new Dictionary<string, Panel> { ["P1"] = panel });
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Level == "error" && i.Code == "pocket_depth_missing");
    }

    [Fact]
    public void World_coord_pocket_and_cutout_are_skipped()
    {
        var panel = new Panel
        {
            PanelId = "P1",
            ThicknessMm = 15,
            Outline = new Outline
            {
                Points = [new(0, 0), new(595, 0), new(595, 200), new(0, 200)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "FEAT-02",
                    Kind = "throughCutout",
                    DepthMm = 15,
                    Through = true,
                    Path = [new(387, 50), new(403, 50), new(403, 150), new(387, 150)],
                },
                new PanelFeature
                {
                    FeatureId = "FEAT-04",
                    Kind = "throughCutout",
                    DepthMm = 15,
                    Through = true,
                    Path = [new(-19509, 9762), new(-19493, 9762), new(-19493, 9862), new(-19509, 9862)],
                },
                new PanelFeature
                {
                    FeatureId = "FEAT-07",
                    Kind = "pocket",
                    DepthMm = 8,
                    Path = [new(-16600, 8008), new(-16565, 8008), new(-16565, 8043), new(-16600, 8043)],
                },
            ],
        };

        var ops = OpsPlanner.FeaturesToOps([panel]);
        Assert.Contains(ops, o => o.FeatureId == "FEAT-02" && o.Op == "contour");
        Assert.DoesNotContain(ops, o => o.FeatureId == "FEAT-04");
        Assert.DoesNotContain(ops, o => o.FeatureId == "FEAT-07");
    }

    [Fact]
    public void Export_sliver_pocket_is_skipped_and_does_not_block_preflight()
    {
        // Bedroom Style 3 BLVRC1 FEAT-01/05: 29.8 × 0.104 mm edge ribbon
        var panel = PanelWithPocket(pocketDepth: 8.337, pocketW: 29.818, pocketH: 0.104);
        var ops = OpsPlanner.FeaturesToOps([panel])
            .Select(o => o with { Placed = true, SheetIndex = 0 })
            .ToList();
        Assert.DoesNotContain(ops, o => o.Op == "pocket");

        var report = NcPreflight.Check(
            ops,
            MachineCatalog.Get("nesting_router_6"),
            1220, 2440,
            new Dictionary<string, Panel> { ["P1"] = panel });
        Assert.DoesNotContain(report.Issues, i => i.Code == "pocket_too_small_for_tool");
        Assert.True(report.Ok, NcPreflight.Format(report));
    }

    [Fact]
    public void Pocket_too_small_for_tool_fails_preflight_and_is_not_silent_skip()
    {
        // Tiny pocket: after toolR+onion inset Clipper yields empty / center-only
        var panel = PanelWithPocket(pocketDepth: 4, pocketW: 4, pocketH: 4);
        var ops = OpsPlanner.FeaturesToOps([panel])
            .Select(o => o with { Placed = true, SheetIndex = 0 })
            .ToList();
        Assert.Contains(ops, o => o.Op == "pocket"); // must still surface the feature
        var pocket = Assert.Single(ops, o => o.Op == "pocket");
        Assert.True(pocket.PocketTooSmallForTool || pocket.PathSegments is null or { Count: 0 });

        var report = NcPreflight.Check(
            ops,
            MachineCatalog.Get("nesting_router_6"),
            1220, 2440,
            new Dictionary<string, Panel> { ["P1"] = panel });
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Level == "error" && i.Code == "pocket_too_small_for_tool");
    }

    [Fact]
    public void Valid_pocket_passes_preflight_and_keeps_segmented_clear()
    {
        var panel = PanelWithPocket(pocketDepth: 6, pocketW: 100, pocketH: 80);
        var ops = OpsPlanner.FeaturesToOps([panel])
            .Select(o => o with { Placed = true, SheetIndex = 0, SheetX = o.Op == "drill" ? 1 : o.SheetX, SheetY = o.Op == "drill" ? 1 : o.SheetY })
            .ToList();
        // Attach-like sheet coords for contour path points (preflight OOB uses path)
        ops = ops.Select(o => o.Path is { Count: > 0 }
            ? o with { Path = o.Path.Select(p => (p.X + 20, p.Y + 20)).ToList(),
                PathSegments = o.PathSegments?.Select(seg => (IReadOnlyList<(double X, double Y)>)seg.Select(p => (p.X + 20, p.Y + 20)).ToList()).ToList(),
                FinishLoop = o.FinishLoop?.Select(p => (p.X + 20, p.Y + 20)).ToList() }
            : o).ToList();

        var pocket = Assert.Single(ops, o => o.Op == "pocket");
        Assert.Equal(6, pocket.DepthMm);
        Assert.False(pocket.PocketTooSmallForTool);
        Assert.True(pocket.PathSegments is { Count: >= 1 });
        Assert.NotNull(pocket.FinishLoop);

        var report = NcPreflight.Check(
            ops,
            MachineCatalog.Get("nesting_router_6"),
            1220, 2440,
            new Dictionary<string, Panel> { ["P1"] = panel });
        Assert.True(report.Ok, NcPreflight.Format(report));
        Assert.DoesNotContain(report.Issues, i => i.Code is "pocket_depth_missing" or "pocket_too_small_for_tool");
    }

    [Fact]
    public void Bundle_build_rejects_unsafe_pocket()
    {
        var panel = PanelWithPocket(pocketDepth: null, pocketW: 100, pocketH: 80);
        var places = new[] { new Nesting.NestPlacement { PanelId = "P1", SheetIndex = 0, OffsetX = 10, OffsetY = 10 } };
        var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps([panel]), places).ToList();
        var pkg = new CutPackage { SchemaName = CutPackage.Schema, JobId = "pk", Panels = [panel] };
        var ex = Assert.ThrowsAny<Exception>(() =>
            SheetBundleBuilder.Build(
                pkg, places, ops, MachineCatalog.Get("nesting_router_6"),
                panelsById: new Dictionary<string, Panel> { ["P1"] = panel },
                sheetWidthMm: 1220, sheetLengthMm: 2440));
        Assert.Contains("pocket", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

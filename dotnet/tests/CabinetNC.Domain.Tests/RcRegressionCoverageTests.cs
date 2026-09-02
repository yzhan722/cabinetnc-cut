using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

/// <summary>
/// Explicit RC regression checklist coverage (audit2). Each fact maps to a P0 acceptance item.
/// </summary>
public class RcRegressionCoverageTests
{
    static Panel Rect(string id, string mat, double th, double w, double h, bool hole = true, bool groove = false) => new()
    {
        PanelId = id,
        Material = mat,
        ThicknessMm = th,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
        Features = BuildFeatures(th, w, h, hole, groove),
        Identity = new WorkpieceIdentity { WorkpieceId = id, ModuleId = "M", ProjectId = "RC" },
    };

    static List<PanelFeature> BuildFeatures(double th, double w, double h, bool hole, bool groove)
    {
        var list = new List<PanelFeature>();
        if (hole)
        {
            list.Add(new PanelFeature
            {
                FeatureId = "H1", Kind = "holeVertical",
                X = w * 0.3, Y = h * 0.3, DiameterMm = 3, DepthMm = Math.Max(1, th - 2),
            });
        }
        if (groove)
        {
            list.Add(new PanelFeature
            {
                FeatureId = "G1", Kind = "grooveVertical",
                DepthMm = Math.Min(6, th - 1),
                Path = [new(10, 10), new(w - 10, 10)],
            });
        }
        return list;
    }

    [Fact]
    public void Multi_material_and_mixed_thickness_never_share_sheet()
    {
        var panels = new[]
        {
            Rect("A", "oak", 18, 400, 300),
            Rect("B", "mdf", 18, 350, 280),
            Rect("C", "oak", 15, 300, 200),
        };
        var nest = GroupedBlfNester.Pack(
            panels,
            new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true },
            [new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15 }],
            GroupedBlfNester.SizeOfOutline);
        var bySheet = nest.Placements.GroupBy(p => p.SheetIndex);
        foreach (var g in bySheet)
        {
            var mats = g.Select(p => panels.First(x => x.PanelId == p.PanelId))
                .Select(p => (p.Material, p.ThicknessMm)).Distinct().ToList();
            Assert.Single(mats);
        }
    }

    [Fact]
    public void Order_drill_before_outer_and_groove_depth_gate()
    {
        var panel = Rect("P", "oak", 18, 200, 150, hole: true, groove: true);
        var ops = OpsPlanner.FeaturesToOps([panel]).Select(o => o with { Placed = true }).ToList();
        var ordered = CamSafety.OrderSafe(ops).ToList();
        Assert.True(ordered.FindIndex(o => o.Op == "drill") < ordered.FindIndex(o => o.Op == "contour" && o.FeatureId is null));
        Assert.True(ordered.FindIndex(o => o.Op == "groove") < ordered.FindIndex(o => o.Op == "contour" && o.FeatureId is null));

        var deep = ops.Select(o => o.Op == "groove" ? o with { DepthMm = 40 } : o).ToList();
        var report = NcPreflight.Check(deep, MachineCatalog.Get("nesting_router_6"), 1220, 2440,
            new Dictionary<string, Panel> { ["P"] = panel });
        Assert.Contains(report.Issues, i => i.Code == "groove_too_deep");
    }

    [Fact]
    public void Pocket_safety_and_sheet_tool_bundle_regression()
    {
        var good = new Panel
        {
            PanelId = "G",
            Material = "oak",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(300, 0), new(300, 200), new(0, 200)] },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "H1", Kind = "holeVertical",
                    X = 40, Y = 40, DiameterMm = 3, DepthMm = 12,
                },
                new PanelFeature
                {
                    FeatureId = "PK", Kind = "pocket", DepthMm = 5,
                    Path = [new(30, 30), new(150, 30), new(150, 120), new(30, 120)],
                },
            ],
            Identity = new WorkpieceIdentity { WorkpieceId = "G", ModuleId = "M", ProjectId = "RC" },
        };
        var places = new[] { new NestPlacement { PanelId = "G", SheetIndex = 0, OffsetX = 20, OffsetY = 20 } };
        var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps([good]), places);
        var profile = MachineCatalog.Get("nesting_router_6");
        var pre = NcPreflight.Check(ops, profile, 1220, 2440, new Dictionary<string, Panel> { ["G"] = good });
        Assert.True(pre.Ok, NcPreflight.Format(pre));

        var pkg = new CutPackage { SchemaName = CutPackage.Schema, JobId = "rc", Panels = [good] };
        var bundle = SheetBundleBuilder.Build(pkg, places, ops, profile);
        Assert.All(bundle.Sheets, s => Assert.True(s.ToolPrograms.Count >= 1));
        Assert.All(bundle.Sheets.SelectMany(s => s.ToolPrograms), p =>
        {
            var tools = p.NcText.Split('\n').Count(l => l.StartsWith("(tool "));
            Assert.Equal(1, tools);
        });
    }

    [Fact]
    public void Stress_and_parity_hooks_still_green()
    {
        // Re-assert 120-scale nest stays under budget via existing stress path size sample
        var panels = Enumerable.Range(0, 40).Select(i =>
            Rect($"P{i}", i % 2 == 0 ? "oak" : "mdf", i % 3 == 0 ? 15 : 18, 180 + i % 7 * 10, 120 + i % 5 * 8)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nest = GroupedBlfNester.Pack(
            panels,
            new NestSettings { MarginMm = 12, ClearanceMm = 10, AllowRotation = true },
            [new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 12 }],
            GroupedBlfNester.SizeOfOutline);
        sw.Stop();
        Assert.True(nest.Placements.Count + nest.Unplaced.Count == 40);
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"nest {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Troy_recipe_and_sheet_tool_split_remain_distinct()
    {
        var panel = Rect("P", "oak", 18, 200, 150, hole: true, groove: true);
        var places = new[] { new NestPlacement { PanelId = "P", SheetIndex = 0, OffsetX = 20, OffsetY = 20 } };
        var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps([panel]), places);
        var profile = MachineCatalog.Get("nesting_router_6");

        var pkg = new CutPackage { SchemaName = CutPackage.Schema, JobId = "mix", Panels = [panel] };
        var bundle = SheetBundleBuilder.Build(pkg, places, ops, profile);
        Assert.All(bundle.Sheets.SelectMany(s => s.ToolPrograms), p =>
        {
            var tools = p.NcText.Replace("\r\n", "\n").Split('\n')
                .Count(l => l.StartsWith("(tool ", StringComparison.Ordinal));
            Assert.Equal(1, tools);
        });

        var troy = NcEmitter.OpsToNc(ops, profile, recipe: PostRecipe.TroyDefault());
        Assert.Contains("M6 T", troy, StringComparison.Ordinal);
        var headerTools = bundle.Sheets.SelectMany(s => s.ToolPrograms).Select(p => p.ToolId).Distinct().Count();
        Assert.True(headerTools >= 2, "split export should keep multiple single-tool files");
    }
}

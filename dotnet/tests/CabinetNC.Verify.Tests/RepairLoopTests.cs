using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Xunit.Abstractions;
using static CabinetNC.Verify.Tests.VerifyFixture;

namespace CabinetNC.Verify.Tests;

/// <summary>
/// The bounded plan → emit → verify loop: the verifier never changes, only the planner's
/// whitelisted knobs do, and it stops when the whitelist is exhausted.
/// </summary>
public class RepairLoopTests(ITestOutputHelper output)
{
    static (CutPackage Pkg, List<NestPlacement> Places) Job(params Panel[] panels)
    {
        var pkg = new CutPackage { SchemaName = CutPackage.Schema, JobId = "repair", Panels = panels };
        var places = panels.Select((p, i) => Place(p.PanelId, 20 + i * 450, 20)).ToList();
        return (pkg, places);
    }

    static Func<CamOverrides, IReadOnlyList<CutOp>> Planner(CutPackage pkg, IReadOnlyList<NestPlacement> places) =>
        ov => ContourToolOffset.Apply(
            OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(pkg.Panels, overrides: ov), places),
            ToolRadiusMm);

    [Fact]
    public void Groove_just_wider_than_tool_fails_then_passes_with_forced_clear()
    {
        // 7 mm groove, Ø6.35 cutter: inside the 1.15× "tool-width" band, so the planner runs
        // the centreline only and the slot comes out 6.35 wide. Round 2 forces area clearance.
        var (pkg, places) = Job(Rect("A", 400, 300, 18, Groove("G", new Point2(30, 150), new Point2(370, 150), 7, 6)));
        var bundle = SheetBundleBuilder.BuildWithRepair(
            pkg, places, Planner(pkg, places), Profile,
            new ExportVerifier(), new RepairPlanner(),
            sheetWidthMm: 1220, sheetLengthMm: 2440, recipe: PostRecipe.TroyDefault());

        foreach (var r in bundle.RepairTrail)
            output.WriteLine($"round {r.Round} ok={r.Ok} overrides={r.Overrides.Describe()}\n{VerifyReport.Format(r.Report)}");

        Assert.Equal(2, bundle.RepairTrail.Count);
        Assert.False(bundle.RepairTrail[0].Ok);
        Assert.Contains(bundle.RepairTrail[0].Report.Issues, i => i.Code == VerifyCodes.GrooveWidthMismatch);
        Assert.True(bundle.RepairTrail[1].Ok);
        var ov = bundle.Overrides.Get("A", "G");
        Assert.NotNull(ov);
        Assert.True(ov!.ForceGrooveClear);
        Assert.Contains("repair", SheetBundleBuilder.RepairTrailJson(bundle));
    }

    [Fact]
    public void Loop_gives_up_when_whitelist_is_exhausted()
    {
        // Ø5 blind hole: smallest mill is Ø6.35 — no process knob can cut it. Must block, not loop.
        var (pkg, places) = Job(Rect("A", 400, 300, 18, Hole("H", 50, 50, 5, depth: 12, through: false)));
        var ex = Assert.Throws<ExportVerifyException>(() => SheetBundleBuilder.BuildWithRepair(
            pkg, places, Planner(pkg, places), Profile,
            new ExportVerifier(), new RepairPlanner(),
            sheetWidthMm: 1220, sheetLengthMm: 2440, recipe: PostRecipe.TroyDefault(),
            enforcePreflight: false));
        output.WriteLine(string.Join("\n", ex.Trail.Select(r => $"round {r.Round} ok={r.Ok} {r.Overrides.Describe()}")));
        Assert.Contains(ex.Report.Issues, i => i.Code == VerifyCodes.FeatureNotCut && i.FeatureId == "H");
        Assert.InRange(ex.Trail.Count, 1, 2);
    }

    [Fact]
    public void Good_job_passes_in_one_round_with_no_overrides()
    {
        var (pkg, places) = Job(Rect("A", 400, 300, 18, Pocket("P", 100, 100, 220, 200, 8), Hole("H", 40, 40, 3)));
        var bundle = SheetBundleBuilder.BuildWithRepair(
            pkg, places, Planner(pkg, places), Profile,
            new ExportVerifier(), new RepairPlanner(),
            sheetWidthMm: 1220, sheetLengthMm: 2440, recipe: PostRecipe.TroyDefault());
        Assert.Single(bundle.RepairTrail);
        Assert.True(bundle.Overrides.IsEmpty);
    }

    [Fact]
    public void Planner_maps_codes_to_whitelisted_knobs_only()
    {
        var tools = ToolCatalog.DefaultMap();
        var planner = new RepairPlanner();

        VerifyReport Report(params VerifyIssue[] issues) => new() { Ok = false, Issues = issues };
        VerifyIssue Err(string code, string feature, double? measured = null, double? expected = null) =>
            new(code, VerifyIssue.Error, "A", feature, 0, 0, code) { MeasuredMm = measured, ExpectedMm = expected };

        // Floor residue: stepover + finish loop first, smaller tool second, then nothing.
        var r1 = planner.Propose(Report(Err(VerifyCodes.PocketFloorUncut, "P")), CamOverrides.Empty, tools)!;
        Assert.Equal(RepairPlanner.TightStepoverMm, r1.Get("A", "P")!.StepoverMm);
        Assert.True(r1.Get("A", "P")!.ForceFinishLoop);
        var r2 = planner.Propose(Report(Err(VerifyCodes.PocketFloorUncut, "P")), r1, tools)!;
        Assert.Equal("T1", r2.Get("A", "P")!.ToolId);
        Assert.Null(planner.Propose(Report(Err(VerifyCodes.PocketFloorUncut, "P")), r2, tools));

        // Narrow groove → forced clear; wide groove → smaller tool.
        var narrow = planner.Propose(Report(Err(VerifyCodes.GrooveWidthMismatch, "G", 10, 11)), CamOverrides.Empty, tools)!;
        Assert.True(narrow.Get("A", "G")!.ForceGrooveClear);
        var wide = planner.Propose(Report(Err(VerifyCodes.GrooveWidthMismatch, "G", 13, 10)), CamOverrides.Empty, tools)!;
        Assert.Equal("T1", wide.Get("A", "G")!.ToolId);

        // Depth / drill / stray: never "repaired" by a process knob.
        Assert.Null(planner.Propose(Report(Err(VerifyCodes.DepthMismatch, "P", 12, 8)), CamOverrides.Empty, tools));
        Assert.Null(planner.Propose(Report(Err(VerifyCodes.DrillCentreDrift, "H", 1.5, 0)), CamOverrides.Empty, tools));
        Assert.Null(planner.Propose(Report(new VerifyIssue(VerifyCodes.ThroughUndercut, VerifyIssue.Error, "A", null, 0, 0, "outline")), CamOverrides.Empty, tools));
    }
}

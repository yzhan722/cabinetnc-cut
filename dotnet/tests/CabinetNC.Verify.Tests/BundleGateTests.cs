using System.Text.Json;
using CabinetNC.Domain;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using static CabinetNC.Verify.Tests.VerifyFixture;

namespace CabinetNC.Verify.Tests;

/// <summary>The gate sits inside SheetBundleBuilder so every export road (Desktop, CLI, worker) hits it.</summary>
public class BundleGateTests
{
    static (CutPackage Pkg, List<NestPlacement> Places) Job(params Panel[] panels)
    {
        var pkg = new CutPackage { SchemaName = CutPackage.Schema, JobId = "gate", Panels = panels };
        var places = panels.Select((p, i) => Place(p.PanelId, 20 + i * 450, 20)).ToList();
        return (pkg, places);
    }

    [Fact]
    public void Good_job_builds_and_carries_verify_artifacts()
    {
        var (pkg, places) = Job(
            Rect("A", 400, 300, 18, Hole("H", 40, 40, 3), Cutout("W", 150, 90, 250, 150), Pocket("P", 280, 200, 360, 260, 8)),
            Rect("B", 300, 200, 18, Groove("G", new(30, 100), new(270, 100), 10, 6)));
        var ops = PlanOps(pkg.Panels, places);
        var bundle = SheetBundleBuilder.Build(pkg, places, ops, Profile,
            sheetWidthMm: 1220, sheetLengthMm: 2440,
            recipe: PostRecipe.TroyDefault(), verifier: new ExportVerifier());

        Assert.NotNull(bundle.Verify);
        Assert.True(bundle.Verify!.Ok, Domain.Manufacturing.Verification.VerifyReport.Format(bundle.Verify));
        var sheet = Assert.Single(bundle.Sheets);
        Assert.NotNull(sheet.VerifyReportJson);

        using var doc = JsonDocument.Parse(sheet.ManifestJson);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("BoardBottom", root.GetProperty("zDatum").GetString());
        Assert.Equal(2, root.GetProperty("placements").GetArrayLength());
        Assert.True(root.GetProperty("verify").GetProperty("ok").GetBoolean());

        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = SheetBundleBuilder.WriteToDirectory(bundle, dir);
            Assert.Contains(written, w => w.EndsWith("gate_S1.verify.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bad_program_blocks_the_bundle()
    {
        var (pkg, places) = Job(Rect("A", 400, 300, 18, Cutout("W", 150, 90, 250, 150)));
        // Generator drops the window: program no longer matches the panel.
        var ops = PlanOps(pkg.Panels, places).Where(o => o.FeatureId != "W").ToList();
        var ex = Assert.Throws<ExportVerifyException>(() => SheetBundleBuilder.Build(pkg, places, ops, Profile,
            sheetWidthMm: 1220, sheetLengthMm: 2440,
            recipe: PostRecipe.TroyDefault(), verifier: new ExportVerifier()));
        Assert.Contains(ex.Report.Issues, i => i.Code == VerifyCodes.FeatureNotCut && i.FeatureId == "W");
    }

    [Fact]
    public void Enforce_off_returns_report_instead_of_throwing()
    {
        var (pkg, places) = Job(Rect("A", 400, 300, 18, Cutout("W", 150, 90, 250, 150)));
        var ops = PlanOps(pkg.Panels, places).Where(o => o.FeatureId != "W").ToList();
        var bundle = SheetBundleBuilder.Build(pkg, places, ops, Profile,
            sheetWidthMm: 1220, sheetLengthMm: 2440,
            recipe: PostRecipe.TroyDefault(), verifier: new ExportVerifier(), enforceVerify: false);
        Assert.False(bundle.Verify!.Ok);
    }

    [Fact]
    public void Sheet_x_tool_split_programs_are_verified_together()
    {
        // Generic post: one program per tool with no M6 — the split tool id must drive the sweep radius.
        var (pkg, places) = Job(Rect("A", 400, 300, 18, Hole("H", 40, 40, 3), Pocket("P", 280, 200, 360, 260, 8)));
        var ops = PlanOps(pkg.Panels, places);
        var bundle = SheetBundleBuilder.Build(pkg, places, ops, Profile,
            sheetWidthMm: 1220, sheetLengthMm: 2440, verifier: new ExportVerifier());
        Assert.True(bundle.Sheets[0].ToolPrograms.Count >= 2);
        Assert.True(bundle.Verify!.Ok, Domain.Manufacturing.Verification.VerifyReport.Format(bundle.Verify));
    }
}

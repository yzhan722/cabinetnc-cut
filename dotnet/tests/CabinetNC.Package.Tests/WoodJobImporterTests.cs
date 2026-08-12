using CabinetNC.Domain;
using CabinetNC.FusionPackage;

namespace CabinetNC.Package.Tests;

public class WoodJobImporterTests
{
    static string? TryFixtureZip()
    {
        var walk = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var p = Path.Combine(walk, "Fixtures", "demo_woodjob_120.zip");
            if (File.Exists(p)) return p;
            var alt = Path.GetFullPath(Path.Combine(walk, "..", "..", "..", "Fixtures", "demo_woodjob_120.zip"));
            if (File.Exists(alt)) return alt;
            var samples = Path.GetFullPath(Path.Combine(walk, "..", "..", "..", "..", "..", "public", "samples", "demo_woodjob_120.zip"));
            if (File.Exists(samples)) return samples;
            var parent = Directory.GetParent(walk);
            if (parent is null) break;
            walk = parent.FullName;
        }
        return null;
    }

    [Fact]
    public void Imports_demo_woodjob_zip()
    {
        var fixture = TryFixtureZip();
        if (fixture is null)
        {
            // Fixture is optional in sparse checkouts; manufacturing-snapshot tests cover the new path.
#pragma warning disable xUnit1004
            return;
#pragma warning restore xUnit1004
        }
        var result = PackageImporter.FromPath(fixture);
        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => $"{e.Path}:{e.Message}")));
        Assert.NotNull(result.Package);
        Assert.Equal(CutPackage.WoodJobFormat, result.Package!.SchemaName);
        Assert.Equal(120, result.Package.Panels.Count);
        Assert.Equal(4, result.Package.Sheets.Count);
        Assert.Contains(result.Package.Panels, p => p.Features.Any(f => f.Kind == "holeVertical"));
        Assert.Contains(result.Package.Panels, p => p.Features.Any(f => f.Kind == "grooveVertical"));
        Assert.Contains(result.Package.Panels, p => p.Features.Any(f => f.Kind == "throughCutout" && f.Path is { Count: >= 3 }));
        Assert.True(result.Package.Sheets[0].WidthMm > 0);
        Assert.True(result.Package.Sheets[0].LengthMm > 0);
        Assert.True(result.Package.Sheets[0].KerfMm > 0);
        var grainLocked = result.Package.Panels.First(p => p.GrainDirection == "Y");
        Assert.False(grainLocked.MayRotate90);
        Assert.All(result.Package.Panels, p =>
        {
            Assert.NotNull(p.Identity);
            Assert.Equal(p.PanelId, p.Identity!.WorkpieceId);
            Assert.Equal(CutPackage.WoodJobFormat, p.Identity.SourceFormat);
            Assert.True(p.ThicknessMm > 0);
            Assert.NotNull(p.Orientation);
        });
    }

    [Fact]
    public void Rejects_missing_thickness_when_not_in_materials()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-wj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                """{"format":"cabinetnc.woodjob","schemaVersion":2,"jobId":"T"}""");
            File.WriteAllText(Path.Combine(dir, "parts.json"),
                """
                {"parts":[{"panelId":"P0","geometry":{"nestingPolygon":[[0,0],[100,0],[100,50],[0,50]]}}]}
                """);
            var result = WoodJobImporter.FromDirectory(dir);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.Code == "thickness");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Roundtrips_to_cut_package_json()
    {
        var fixture = TryFixtureZip();
        if (fixture is null) return;
        var imported = PackageImporter.FromPath(fixture);
        Assert.True(imported.Ok);
        var json = CutPackageJson.Serialize(imported.Package!);
        var again = CutPackageImporter.FromJson(json);
        Assert.True(again.Ok, string.Join("; ", again.Errors.Select(e => e.Message)));
        Assert.Equal(120, again.Package!.Panels.Count);
    }

    [Fact]
    public void Roundtrip_preserves_workpiece_contract_fields()
    {
        var fixture = TryFixtureZip();
        if (fixture is null) return;
        var imported = PackageImporter.FromPath(fixture);
        Assert.True(imported.Ok);
        var json = CutPackageJson.Serialize(imported.Package!);
        Assert.Contains("workpieceId", json);
        Assert.Contains("orientation", json);
        var again = CutPackageImporter.FromJson(json);
        Assert.True(again.Ok, string.Join("; ", again.Errors.Select(e => e.Message)));
        Assert.Equal(120, again.Package!.Panels.Count);
        Assert.All(again.Package.Panels, p => Assert.NotNull(p.Identity?.WorkpieceId));
    }
}

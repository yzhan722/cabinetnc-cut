using System.Runtime.CompilerServices;
using CabinetNC.Domain.Manufacturing;
using Xunit.Abstractions;

namespace CabinetNC.Domain.Tests;

/// <summary>
/// Replays every real shop program under testdata/regression/shop-anc.
/// The directory is the intake point for machine evidence; see its README.
/// </summary>
public class ShopAncFixtureTests(ITestOutputHelper output)
{
    static string FixtureDir([CallerFilePath] string? cs = null) =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(cs)!, "..", "testdata", "regression", "shop-anc"));

    [Fact]
    public void Fixture_intake_directory_is_documented()
    {
        var dir = FixtureDir();
        Assert.True(Directory.Exists(dir), $"missing {dir}");
        Assert.True(File.Exists(Path.Combine(dir, "README.md")), "shop-anc/README.md must explain how to add a fixture");
    }

    [Fact]
    public void Every_shop_anc_replays_into_panels()
    {
        var files = Directory.GetFiles(FixtureDir(), "*.anc", SearchOption.TopDirectoryOnly);
        output.WriteLine($"{files.Length} shop .anc fixture(s) present");
        if (files.Length == 0)
        {
            output.WriteLine("no fixtures yet — drop real machine programs here to lock them");
            return;
        }

        foreach (var file in files)
        {
            var result = NcReverse.FromText(File.ReadAllText(file));
            var name = Path.GetFileName(file);
            Assert.True(result.Strokes.Count > 10, $"{name}: too little motion recovered");
            Assert.DoesNotContain("no_motion", result.Warnings);
            Assert.DoesNotContain("no_contour", result.Warnings);
            Assert.DoesNotContain("no_panel", result.Warnings);
            var pkg = NcReverse.ToPackage(result, Path.GetFileNameWithoutExtension(file));
            Assert.NotEmpty(pkg.Panels);
            output.WriteLine($"{name}: strokes={result.Strokes.Count} ops={result.Ops.Count} panels={result.Panels.Count}");
        }
    }
}

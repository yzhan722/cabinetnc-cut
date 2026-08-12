using System.Diagnostics;
using CabinetNC.Domain.Nesting;
using CabinetNC.FusionPackage;

namespace CabinetNC.Package.Tests;

/// <summary>
/// Real-job acceptance for Clipper NFP against the desktop sample cnjob.
/// Compares primarily with Deepnest preview; BLF kept as fast baseline.
/// Skips when the file is not present on this machine.
/// </summary>
public class NfpCnjobAcceptanceTests
{
    const string DesktopCnjob = @"C:\Users\user\Desktop\Bunk Bed and Bathroom.cnjob";
    static readonly TimeSpan DeepnestBudget = TimeSpan.FromSeconds(90);

    [Fact]
    public void Bunk_bed_bathroom_nfp_places_without_polygon_collision()
    {
        if (!File.Exists(DesktopCnjob))
            return;

        var imported = PackageImporter.FromPath(DesktopCnjob);
        Assert.True(imported.Ok, string.Join("; ", imported.Errors.Select(e => e.Message)));
        Assert.NotNull(imported.Package);
        var panels = imported.Package!.Panels.ToList();
        Assert.True(panels.Count >= 5, $"expected multiple panels, got {panels.Count}");

        var groups = panels
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .Select(g => g.Key)
            .ToList();

        var stock = groups.Select(g => new NestSheetSpec
        {
            WidthMm = 1220,
            LengthMm = 2440,
            BorderMm = 15,
            SpacingMm = 12,
            AllowRotation = true,
            Material = g.Material,
            ThicknessMm = g.ThicknessMm,
            Label = g.ToString(),
        }).ToList();

        var settings = new NestSettings
        {
            MarginMm = 15,
            ClearanceMm = 12,
            AllowRotation = true,
            GrainLock = true,
        };

        var sw = Stopwatch.StartNew();
        var nfp = new ClipperNfpNestingEngine().Pack(
            panels, settings, stock, GroupedBlfNester.SizeOfOutline);
        sw.Stop();
        var nfpMs = sw.ElapsedMilliseconds;
        var nfpCollisions = NestValidator.FindPolygonCollisions(panels, nfp.Placements, settings.ClearanceMm);
        var nfpGate = NestExportGate.Check(
            panels, nfp.Placements, settings.ClearanceMm, allowAabbOverlap: true);
        var nfpUtil = UtilHint(nfp);

        // Primary comparison target: Deepnest-style preview engine (same UI option).
        NestResult? deepnest = null;
        long deepnestMs = -1;
        string deepnestNote = "";
        IReadOnlyList<NestCollision> deepnestCollisions = [];
        (bool Ok, IReadOnlyList<string> Errors) deepnestGate = (false, ["not_run"]);
        double deepnestUtil = 0;
        sw.Restart();
        try
        {
            using var cts = new CancellationTokenSource(DeepnestBudget);
            deepnest = new DeepnestPreviewNestingEngine().Pack(
                panels, settings, stock, GroupedBlfNester.SizeOfOutline, cts.Token);
            deepnestMs = sw.ElapsedMilliseconds;
            deepnestCollisions = NestValidator.FindPolygonCollisions(
                panels, deepnest.Placements, settings.ClearanceMm);
            deepnestGate = NestExportGate.Check(
                panels, deepnest.Placements, settings.ClearanceMm, allowAabbOverlap: true);
            deepnestUtil = UtilHint(deepnest);
            deepnestNote = "ok";
        }
        catch (OperationCanceledException)
        {
            deepnestMs = sw.ElapsedMilliseconds;
            deepnestNote = $"timeout_after_{DeepnestBudget.TotalSeconds:0}s";
        }
        catch (Exception ex)
        {
            deepnestMs = sw.ElapsedMilliseconds;
            deepnestNote = "error: " + ex.Message;
        }

        sw.Restart();
        var blf = new BlfNestingEngine().Pack(
            panels, settings, stock, GroupedBlfNester.SizeOfOutline);
        sw.Stop();
        var blfMs = sw.ElapsedMilliseconds;

        var reportPath = Path.Combine(
            Path.GetDirectoryName(DesktopCnjob)!,
            "Bunk Bed and Bathroom.nfp-acceptance.txt");
        File.WriteAllText(reportPath,
            $"""
            NFP acceptance · {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            job: {DesktopCnjob}
            panels: {panels.Count}
            material groups: {groups.Count}

            === NFP (candidate) ===
            engine: {nfp.Engine}
              placed: {nfp.Placements.Count}
              sheets: {nfp.SheetCount}
              unplaced: {nfp.Unplaced.Count}
              elapsedMs: {nfpMs}
              polygonCollisions: {nfpCollisions.Count}
              exportGateOk: {nfpGate.Ok}
              utilHint: {nfpUtil:0.0}%

            === Deepnest preview (primary compare) ===
            engine: {deepnest?.Engine ?? "(none)"}
              status: {deepnestNote}
              placed: {deepnest?.Placements.Count ?? 0}
              sheets: {deepnest?.SheetCount ?? 0}
              unplaced: {deepnest?.Unplaced.Count ?? 0}
              elapsedMs: {deepnestMs}
              polygonCollisions: {deepnestCollisions.Count}
              exportGateOk: {deepnestGate.Ok}
              utilHint: {deepnestUtil:0.0}%

            === BLF (fast baseline) ===
            engine: {blf.Engine}
              placed: {blf.Placements.Count}
              sheets: {blf.SheetCount}
              unplaced: {blf.Unplaced.Count}
              elapsedMs: {blfMs}

            NFP vs Deepnest:
              sheetDelta: {(deepnest is null ? "n/a" : (nfp.SheetCount - deepnest.SheetCount).ToString())}
              placedDelta: {(deepnest is null ? "n/a" : (nfp.Placements.Count - deepnest.Placements.Count).ToString())}
              speedup: {(deepnest is null || deepnestMs <= 0 ? "n/a" : $"{(double)deepnestMs / Math.Max(1, nfpMs):0.00}x")}

            NFP group reports:
            {string.Join(Environment.NewLine, nfp.GroupReports.Select(g =>
                $"  {g.Key}: {g.PlacedCount}/{g.PartCount} sheets={g.SheetCount} util={g.UtilizationPct:0.0}%"))}

            Deepnest group reports:
            {(deepnest is null ? "  (not available)" : string.Join(Environment.NewLine, deepnest.GroupReports.Select(g =>
                $"  {g.Key}: {g.PlacedCount}/{g.PartCount} sheets={g.SheetCount} util={g.UtilizationPct:0.0}%")))}

            NFP unplaced: {string.Join(", ", nfp.Unplaced.Take(20))}
            Deepnest unplaced: {(deepnest is null ? "(n/a)" : string.Join(", ", deepnest.Unplaced.Take(20)))}
            NFP gate errors: {string.Join(" | ", nfpGate.Errors.Take(8))}
            Deepnest gate errors: {string.Join(" | ", deepnestGate.Errors.Take(8))}
            """);

        Assert.Equal(panels.Count, nfp.Placements.Count + nfp.Unplaced.Count);
        Assert.True(nfp.Placements.Count > 0, "NFP placed nothing");
        Assert.Empty(nfpCollisions);
        Assert.True(nfpGate.Ok, string.Join("; ", nfpGate.Errors));
        Assert.True(nfpMs < 120_000, $"NFP too slow: {nfpMs}ms");

        // Compare against Deepnest when it finished inside the budget.
        if (deepnest is not null && deepnestNote == "ok")
        {
            Assert.True(nfp.Unplaced.Count <= deepnest.Unplaced.Count,
                $"NFP unplaced {nfp.Unplaced.Count} > Deepnest {deepnest.Unplaced.Count}");
            Assert.True(nfp.SheetCount <= deepnest.SheetCount + 2,
                $"NFP sheets {nfp.SheetCount} >> Deepnest {deepnest.SheetCount}");
            Assert.True(nfpMs <= deepnestMs || deepnestMs < 0,
                $"NFP slower than Deepnest: nfp={nfpMs}ms deepnest={deepnestMs}ms");
            Assert.True(nfpCollisions.Count <= deepnestCollisions.Count);
        }

        // Keep BLF sanity (must not collapse vs fast rectangle packer).
        Assert.True(nfp.SheetCount <= blf.SheetCount + 2,
            $"NFP sheets {nfp.SheetCount} >> BLF {blf.SheetCount}");
        Assert.True(nfp.Unplaced.Count <= blf.Unplaced.Count,
            $"NFP unplaced {nfp.Unplaced.Count} > BLF {blf.Unplaced.Count}");
    }

    static double UtilHint(NestResult r) =>
        r.GroupReports.Count == 0 ? 0 : r.GroupReports.Average(g => g.UtilizationPct);
}

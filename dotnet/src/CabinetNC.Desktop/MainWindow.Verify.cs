using System.IO;
using System.Text.Json;
using System.Windows;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;
using CabinetNC.Infrastructure.Diagnostics;
using CabinetNC.Verify;
using StatusKind = CabinetNC.Desktop.Core.StatusSeverity;

namespace CabinetNC.Desktop;

/// <summary>
/// 计码 gate on the export road. The verifier lives in CabinetNC.Verify and only sees
/// CAD intent + the emitted program; errors block the export and cannot be overridden.
/// When it fails, the repair planner may turn whitelisted process knobs (tighter stepover,
/// forced groove clearance, smaller cutter, wall pass) and the CAM stage is re-run —
/// at most <see cref="SheetBundleBuilder.DefaultRepairRounds"/> times.
/// </summary>
public partial class MainWindow
{
    static readonly IExportVerifier ExportVerifierInstance = new ExportVerifier();
    static readonly IRepairPlanner RepairPlannerInstance = new RepairPlanner();
    static readonly JsonSerializerOptions VerifyJsonOpts = new() { WriteIndented = true };

    /// <summary>Per-feature process overrides the repair loop settled on; feeds RebuildOpsOverlay.</summary>
    CamOverrides _camOverrides = CamOverrides.Empty;

    /// <summary>Verify each per-sheet program without any UI.</summary>
    IReadOnlyDictionary<string, VerifyReport> VerifyExportFiles(IReadOnlyList<ExportNcFile> files)
    {
        var result = new Dictionary<string, VerifyReport>(StringComparer.Ordinal);
        if (_session.Package is null) return result;
        var places = CurrentNestPlacements();
        var recipe = CurrentPostRecipe();
        var tools = ToolCatalog.DefaultMap();
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.NcText) || f.NcText.StartsWith("//", StringComparison.Ordinal))
                continue;
            result[f.FileName] = ExportVerifierInstance.Verify(new VerifyInput
            {
                Panels = _session.Package.Panels,
                Placements = places,
                SheetIndex = f.SheetIndex,
                Programs = [new VerifyProgram(f.NcText)],
                Tools = tools,
                ZDatum = VerifyInput.DatumOf(recipe),
                Bridges = recipe.Bridges,
            });
        }
        return result;
    }

    /// <summary>
    /// Verify the programs about to be written; on failure re-plan with whitelisted
    /// overrides and verify again (bounded). <paramref name="files"/> is replaced with the
    /// regenerated programs when a repair round ran. False = blocked (overrides restored).
    /// </summary>
    bool GuardExportVerify(ref List<ExportNcFile> files, out IReadOnlyDictionary<string, VerifyReport> reports)
    {
        // The verifier replays OSAI programs. The Syntec file is a different dialect.
        if (IsSyntecPost())
        {
            reports = new Dictionary<string, VerifyReport>(StringComparer.Ordinal);
            return true;
        }
        reports = VerifyExportFiles(files);
        if (reports.Values.All(r => r.Ok))
        {
            LogVerify(reports, files, ok: true, rounds: 1);
            return true;
        }

        var before = _camOverrides;
        var overrides = _camOverrides;
        var tools = ToolCatalog.DefaultMap();
        for (var round = 2; round <= SheetBundleBuilder.DefaultRepairRounds; round++)
        {
            var merged = VerifyReport.Merge(reports.Values.Where(r => !r.Ok));
            var next = RepairPlannerInstance.Propose(merged, overrides, tools);
            if (next is null || next.SameAs(overrides)) break;
            overrides = next;
            _camOverrides = overrides;
            RebuildOpsOverlay();
            var byName = _exportFiles.ToDictionary(f => f.FileName, StringComparer.Ordinal);
            files = files.Select(f => byName.GetValueOrDefault(f.FileName) ?? f).ToList();
            reports = VerifyExportFiles(files);
            if (reports.Values.All(r => r.Ok))
            {
                LogVerify(reports, files, ok: true, rounds: round, overrides: overrides);
                SetStatus($"计码验算：第 {round} 轮自动收紧工艺后通过 · {overrides.Describe()}", StatusKind.Warning);
                return true;
            }
        }

        // Blocked: put the CAM stage back to what the operator had.
        if (!_camOverrides.SameAs(before))
        {
            _camOverrides = before;
            RebuildOpsOverlay();
        }
        var failed = reports.Where(kv => !kv.Value.Ok).ToList();
        LogVerify(reports, files, ok: false, rounds: 0, overrides: overrides);
        var text = string.Join("\n\n", failed.Select(f =>
            $"{f.Key}\n" + VerifyReport.Format(new VerifyReport { Ok = false, Issues = f.Value.Errors.Take(12).ToList() })));
        UiDialog.Show(this,
            "计码验算未通过，禁止导出（G-code 与板件形状不符）：\n\n" + text +
            "\n\n已尝试自动收紧工艺仍未通过。验算不可跳过，请修正特征/刀具后重新计算。",
            "计码验算",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        SetStatus("计码验算未通过 · 已阻止导出", StatusKind.Error);
        return false;
    }

    void LogVerify(IReadOnlyDictionary<string, VerifyReport> reports, IReadOnlyList<ExportNcFile> files, bool ok, int rounds, CamOverrides? overrides = null)
    {
        UsageLog.LogActionResult("export.verify", new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["rounds"] = rounds,
            ["overrides"] = overrides?.Describe(),
            ["files"] = files.Select(f => f.FileName).ToArray(),
            ["errors"] = reports.Values.Sum(r => r.Issues.Count(i => i.IsError)),
            ["warnings"] = reports.Values.Sum(r => r.Issues.Count(i => !i.IsError)),
            ["codes"] = reports.Values.SelectMany(r => r.Issues).Select(i => i.Code).Distinct().ToArray(),
        }, error: ok ? null : string.Join("; ", reports.Values.SelectMany(r => r.Errors).Select(i => i.Code).Distinct().Take(8)));
    }

    void NoteRepairOutcome(IReadOnlyList<RepairRound> trail, CamOverrides overrides)
    {
        if (trail.Count <= 1) return;
        UsageLog.LogActionResult("export.bundle.repair", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["rounds"] = trail.Count,
            ["overrides"] = overrides.Describe(),
            ["firstRoundCodes"] = trail[0].Report.Errors.Select(i => i.Code).Distinct().ToArray(),
        });
        SetStatus($"计码验算：第 {trail.Count} 轮自动收紧工艺后通过 · {overrides.Describe()}", StatusKind.Warning);
    }

    /// <summary>Drop the verification report next to each written program (audit trail / golden input).</summary>
    static void WriteVerifyReports(IReadOnlyDictionary<string, VerifyReport> reports, string dir, string? singlePath)
    {
        foreach (var (file, report) in reports)
        {
            try
            {
                var target = singlePath is not null && reports.Count == 1
                    ? Path.ChangeExtension(singlePath, ".verify.json")
                    : Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".verify.json");
                File.WriteAllText(target, JsonSerializer.Serialize(report, VerifyJsonOpts));
            }
            catch (IOException)
            {
                // Report is an audit artefact; never fail the export for it.
            }
        }
    }

    void ShowVerifyBlocked(ExportVerifyException ex)
    {
        UsageLog.LogActionResult("export.bundle.verify", new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["sheet"] = ex.Report.SheetIndex,
            ["rounds"] = ex.Trail.Count,
            ["codes"] = ex.Report.Issues.Select(i => i.Code).Distinct().ToArray(),
        }, error: string.Join("; ", ex.Report.Errors.Select(i => i.Code).Distinct().Take(8)));
        UiDialog.Show(this,
            $"计码验算未通过（S{ex.Report.SheetIndex + 1}），禁止打包：\n\n" +
            VerifyReport.Format(new VerifyReport { Ok = false, Issues = ex.Report.Errors.Take(12).ToList() }) +
            (ex.Trail.Count > 1 ? $"\n\n已自动尝试 {ex.Trail.Count} 轮工艺收紧仍未通过。" : "") +
            "\n\n验算不可跳过。请修正特征/刀具后重新计算。",
            "计码验算",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        SetStatus("计码验算未通过 · 已阻止打包", StatusKind.Error);
    }
}

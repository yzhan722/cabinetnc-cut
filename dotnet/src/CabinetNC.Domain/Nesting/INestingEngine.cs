namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;
using System.Diagnostics;

/// <summary>Pluggable nesting engine. RC authority remains BLF (AABB), not NFP.</summary>
public interface INestingEngine
{
    string Name { get; }
    NestResult Pack(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> stockTemplates,
        Func<Panel, (double w, double h)> sizeOf,
        CancellationToken ct = default,
        IProgress<NestProgressReport>? progress = null);
}

public sealed class BlfNestingEngine : INestingEngine
{
    public string Name => "grouped_blf_v0";

    public NestResult Pack(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> stockTemplates,
        Func<Panel, (double w, double h)> sizeOf,
        CancellationToken ct = default,
        IProgress<NestProgressReport>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        return GroupedBlfNester.Pack(panels, settings, stockTemplates, sizeOf, ct, progress);
    }
}

/// <summary>
/// Advanced engine prototype — intentionally fails / times out so fallback path is testable.
/// Does NOT claim NFP. Part-in-part model is placeholder only.
/// </summary>
public sealed class AdvancedNestingEngineStub : INestingEngine
{
    public string Name => "advanced_stub_v0";
    public bool AlwaysFail { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1);

    public NestResult Pack(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> stockTemplates,
        Func<Panel, (double w, double h)> sizeOf,
        CancellationToken ct = default,
        IProgress<NestProgressReport>? progress = null)
    {
        _ = progress;
        if (AlwaysFail)
            throw new InvalidOperationException("advanced_stub: not implemented (no NFP)");
        // Simulate timeout path
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Timeout);
        linked.Token.ThrowIfCancellationRequested();
        throw new TimeoutException("advanced_stub: timeout");
    }
}

public sealed class NestEngineRequest
{
    public required IReadOnlyList<Panel> Panels { get; init; }
    public required NestSettings Settings { get; init; }
    public required IReadOnlyList<NestSheetSpec> StockTemplates { get; init; }
    public required Func<Panel, (double w, double h)> SizeOf { get; init; }
    /// <summary>preferred | blf | advanced/deepnest</summary>
    public string EnginePreference { get; init; } = "preferred";
    public TimeSpan AdvancedTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public IProgress<NestProgressReport>? Progress { get; init; }
}

public sealed class NestEngineRunLog
{
    public required string SelectedEngine { get; init; }
    public string? AttemptedEngine { get; init; }
    public string? FallbackReason { get; init; }
    public long ElapsedMs { get; init; }
    public double? UtilizationHintPct { get; init; }
}

/// <summary>Runs preferred engine with automatic BLF fallback.</summary>
public sealed class NestEngineRouter
{
    readonly INestingEngine _blf;
    readonly INestingEngine _advanced;

    public NestEngineRouter(INestingEngine? blf = null, INestingEngine? advanced = null)
    {
        _blf = blf ?? new BlfNestingEngine();
        _advanced = advanced ?? new AdvancedNestingEngineStub();
    }

    public (NestResult Result, NestEngineRunLog Log) Run(NestEngineRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var pref = (req.EnginePreference ?? "preferred").Trim().ToLowerInvariant();

        void Report(string message, int done = 0, int total = 0) =>
            req.Progress?.Report(new NestProgressReport
            {
                Done = done,
                Total = total,
                Message = message,
            });

        if (pref is "blf" or "grouped_blf" or "grouped_blf_v0")
        {
            Report("BLF 密排…");
            var r = ApplyPartsInPart(
                TagEngine(_blf.Pack(req.Panels, req.Settings, req.StockTemplates, req.SizeOf, ct, req.Progress), _blf.Name),
                req);
            sw.Stop();
            return (r, new NestEngineRunLog
            {
                SelectedEngine = _blf.Name,
                AttemptedEngine = _blf.Name,
                ElapsedMs = sw.ElapsedMilliseconds,
                UtilizationHintPct = UtilHint(r),
            });
        }

        if (pref is "advanced" or "deepnest" or "deepnest_next" or "nfp" or "clipper_nfp" or "preferred")
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(req.AdvancedTimeout);
                Report($"{_advanced.Name} 密排…");
                var adv = ApplyPartsInPart(
                    TagEngine(
                        _advanced.Pack(req.Panels, req.Settings, req.StockTemplates, req.SizeOf, cts.Token, req.Progress),
                        _advanced.Name),
                    req);
                sw.Stop();
                return (adv, new NestEngineRunLog
                {
                    SelectedEngine = _advanced.Name,
                    AttemptedEngine = _advanced.Name,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    UtilizationHintPct = UtilHint(adv),
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                Report("超时/失败 → BLF 回退…");
                var fallback = ApplyPartsInPart(
                    TagEngine(
                        _blf.Pack(req.Panels, req.Settings, req.StockTemplates, req.SizeOf, ct, req.Progress),
                        "blf_fallback"),
                    req);
                sw.Stop();
                return (fallback, new NestEngineRunLog
                {
                    SelectedEngine = "blf_fallback",
                    AttemptedEngine = _advanced.Name,
                    FallbackReason = ex.GetType().Name + ": " + ex.Message,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    UtilizationHintPct = UtilHint(fallback),
                });
            }
        }

        // Unknown preference → BLF
        Report("BLF 密排…");
        var def = ApplyPartsInPart(
            TagEngine(_blf.Pack(req.Panels, req.Settings, req.StockTemplates, req.SizeOf, ct, req.Progress), _blf.Name),
            req);
        sw.Stop();
        return (def, new NestEngineRunLog
        {
            SelectedEngine = _blf.Name,
            AttemptedEngine = pref,
            FallbackReason = "unknown_preference",
            ElapsedMs = sw.ElapsedMilliseconds,
            UtilizationHintPct = UtilHint(def),
        });
    }

    static NestResult TagEngine(NestResult r, string engine) =>
        new()
        {
            Engine = engine,
            Placements = r.Placements,
            SheetCount = r.SheetCount,
            Unplaced = r.Unplaced,
            UnplacedReasons = r.UnplacedReasons,
            GroupReports = r.GroupReports,
            SheetsUsed = r.SheetsUsed,
            PartInPartSlots = r.PartInPartSlots,
        };

    static NestResult ApplyPartsInPart(NestResult primary, NestEngineRequest req)
    {
        var pip = PartsInPartPacker.Apply(
            primary, req.Panels, req.Settings, req.StockTemplates, req.SizeOf);
        return TagEngine(pip, primary.Engine);
    }

    static double? UtilHint(NestResult r)
    {
        if (r.GroupReports.Count == 0) return null;
        return r.GroupReports.Average(g => g.UtilizationPct);
    }
}

/// <summary>Child panel nested inside a host through-cutout void.</summary>
public sealed class PartInPartSlot
{
    public required string HostPanelId { get; init; }
    public required string ChildPanelId { get; init; }
    public string? FeatureId { get; init; }
    public int SheetIndex { get; set; }
    public bool Enabled { get; init; } = true;
}

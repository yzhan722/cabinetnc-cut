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

/// <summary>Child panel nested inside a host through-cutout void.</summary>
public sealed class PartInPartSlot
{
    public required string HostPanelId { get; init; }
    public required string ChildPanelId { get; init; }
    public string? FeatureId { get; init; }
    public int SheetIndex { get; set; }
    public bool Enabled { get; init; } = true;
}

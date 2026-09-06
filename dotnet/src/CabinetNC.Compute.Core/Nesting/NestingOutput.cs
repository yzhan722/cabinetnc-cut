namespace CabinetNC.Compute.Core.Nesting;

public sealed record NestingPlacementOutput(
    string PanelId,
    int SheetIndex,
    double OffsetX,
    double OffsetY,
    double RotationDeg);

/// <summary>
/// <c>Code</c> is <c>engine_fallback</c> (router fell back to BLF; panel/sheet fields null) or
/// <c>aabb_gap</c> (two placed parts violate the requested spacing).
/// </summary>
public sealed record NestingWarningOutput(
    string Code,
    string Message,
    string? PanelIdA,
    string? PanelIdB,
    int? SheetIndex);

/// <summary>
/// <c>Ok=false</c> carries the exception message in <c>Error</c> and empty collections; it never throws.
/// </summary>
public sealed record NestingOutput(
    bool Ok,
    string Engine,
    IReadOnlyList<NestingPlacementOutput> Placements,
    int SheetCount,
    IReadOnlyList<string> Unplaced,
    IReadOnlyList<NestingWarningOutput> Warnings,
    string? Error);

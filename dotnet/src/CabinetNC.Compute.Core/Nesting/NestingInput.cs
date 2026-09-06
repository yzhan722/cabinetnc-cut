namespace CabinetNC.Compute.Core.Nesting;

/// <summary>
/// One rectangular part (AABB) for the transport-neutral nesting runner. Mirrors the
/// local worker's <c>NestPartMsg</c> field for field so both transports feed the same runner.
/// </summary>
public sealed record NestingPartInput(
    string PanelId,
    double WidthMm,
    double HeightMm,
    bool MayRotate,
    string? Material,
    double ThicknessMm);

/// <summary>
/// Rectangular nesting request. Non-positive sheet/spacing/border values fall back to the
/// legacy worker defaults (1220 × 2440, spacing 12, border 15) inside the runner.
/// </summary>
public sealed record NestingInput(
    IReadOnlyList<NestingPartInput> Parts,
    double SheetWidthMm,
    double SheetLengthMm,
    double SpacingMm,
    double BorderMm,
    bool AllowRotation);

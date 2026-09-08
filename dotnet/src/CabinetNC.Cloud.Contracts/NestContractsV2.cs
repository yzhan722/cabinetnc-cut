namespace CabinetNC.Cloud.Contracts;

// ---------------------------------------------------------------------------------------------
// Nest contract v2: everything the Desktop's local NestEngineRouter sees, so the server runs the
// identical algorithm on identical inputs. v1 (rectangles, one stock size) stays as a subset.
// ---------------------------------------------------------------------------------------------

public sealed record NestPointDto(double X, double Y);

/// <summary>A through-cutout ring in panel-local mm; hosts parts-in-part children.</summary>
public sealed record NestCutoutDto(string FeatureId, IReadOnlyList<NestPointDto> Ring);

public sealed record NestPanelDto(
    string PanelId,
    IReadOnlyList<NestPointDto> Outline,
    IReadOnlyList<NestCutoutDto> Cutouts,
    string? Material,
    double ThicknessMm,
    string? GrainDirection,
    IReadOnlyList<int>? AllowedRotations,
    int Quantity);

public sealed record NestBlockedRectDto(double MinX, double MinY, double MaxX, double MaxY);

public sealed record NestSheetDto(
    double WidthMm,
    double LengthMm,
    double BorderMm,
    double? InsetLeftMm,
    double? InsetBottomMm,
    double? InsetRightMm,
    double? InsetTopMm,
    double SpacingMm,
    bool AllowRotation,
    bool AllowPartsInPart,
    IReadOnlyList<NestBlockedRectDto> Blocked,
    string? Label,
    string? Material,
    double ThicknessMm,
    string SheetGrain);

public sealed record NestSettingsDto(
    double MarginMm,
    double ClearanceMm,
    bool AllowRotation,
    IReadOnlyList<int> AllowedRotations,
    double RotationStepDeg,
    bool GrainLock,
    string SheetGrain,
    bool MirrorPermission,
    bool PreferLockedPlacements);

public sealed record SubmitNestJobRequestV2(
    IReadOnlyList<NestPanelDto> Panels,
    IReadOnlyList<NestSheetDto> Sheets,
    NestSettingsDto Settings,
    string EnginePreference,
    int AdvancedTimeoutSeconds);

public sealed record NestUnplacedReasonDto(string PanelId, string Code, string Message);

public sealed record NestGroupReportDto(
    string Material,
    double ThicknessMm,
    int PartCount,
    int PlacedCount,
    int SheetCount,
    int LocalSheetStart,
    double UtilizationPct);

public sealed record NestPartInPartSlotDto(string HostPanelId, string ChildPanelId, string? FeatureId, int SheetIndex, bool Enabled);

public sealed record NestRunLogDto(string SelectedEngine, string? AttemptedEngine, string? FallbackReason, long ElapsedMs, double? UtilizationHintPct);

/// <summary>Stored object for a v2 job; hashed byte for byte into ResultSha256.</summary>
public sealed record NestJobResultPayloadV2(
    string Engine,
    IReadOnlyList<NestPlacementDto> Placements,
    int SheetCount,
    IReadOnlyList<string> Unplaced,
    IReadOnlyList<NestUnplacedReasonDto> UnplacedReasons,
    IReadOnlyList<NestGroupReportDto> GroupReports,
    IReadOnlyList<NestSheetDto> SheetsUsed,
    IReadOnlyList<NestPartInPartSlotDto> PartInPartSlots,
    NestRunLogDto Log);

/// <summary>API envelope for <c>GET /jobs/{id}/result</c> of a v2 job.</summary>
public sealed record NestJobResultV2(
    Guid JobId,
    NestJobResultPayloadV2 Result,
    string EngineVersion,
    string InputSha256,
    string ResultSha256,
    long DurationMs);

/// <summary>Pure-DTO validation shared by the API and the Desktop (no Domain dependency).</summary>
public static class NestRequestV2Rules
{
    public const int MaxPanels = 1000;
    public const int MaxSheets = 64;
    public const int MaxOutlinePoints = 5000;
    public const int MaxCutoutsPerPanel = 64;
    public const double MaxDimensionMm = 100_000;
    public const int MaxAdvancedTimeoutSeconds = 180;
    public static readonly IReadOnlyList<string> EnginePreferences = ["preferred", "nfp", "clipper_nfp", "advanced", "blf", "grouped_blf", "deepnest", "deepnest_next"];
    public static readonly IReadOnlyList<string> SheetGrains = ["None", "AlongLength", "AlongWidth"];

    /// <summary>Returns the first violation, or null when the request is acceptable.</summary>
    public static string? Validate(SubmitNestJobRequestV2 request)
    {
        if (request.Panels is null || request.Panels.Count == 0)
            return "At least one panel is required.";
        if (request.Panels.Count > MaxPanels)
            return $"At most {MaxPanels} panels per job.";
        if (request.Sheets is null || request.Sheets.Count == 0)
            return "At least one stock sheet is required.";
        if (request.Sheets.Count > MaxSheets)
            return $"At most {MaxSheets} stock sheets per job.";
        if (request.Settings is null)
            return "Settings are required.";
        if (string.IsNullOrWhiteSpace(request.EnginePreference) || !EnginePreferences.Contains(request.EnginePreference.Trim().ToLowerInvariant()))
            return $"enginePreference must be one of: {string.Join(", ", EnginePreferences)}.";
        if (request.AdvancedTimeoutSeconds is < 1 or > MaxAdvancedTimeoutSeconds)
            return $"advancedTimeoutSeconds must be between 1 and {MaxAdvancedTimeoutSeconds}.";

        var s = request.Settings;
        if (!Finite(s.MarginMm) || s.MarginMm < 0 || !Finite(s.ClearanceMm) || s.ClearanceMm < 0)
            return "Settings margin and clearance must be finite and non-negative.";
        if (!Finite(s.RotationStepDeg) || s.RotationStepDeg <= 0)
            return "Settings rotationStepDeg must be positive.";
        if (s.AllowedRotations is null || !SheetGrains.Contains(s.SheetGrain ?? ""))
            return $"Settings sheetGrain must be one of: {string.Join(", ", SheetGrains)}.";

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in request.Panels)
        {
            if (string.IsNullOrWhiteSpace(p.PanelId) || p.PanelId.Length > 200)
                return "Every panel needs a panelId of at most 200 characters.";
            if (!ids.Add(p.PanelId))
                return $"Duplicate panelId '{p.PanelId}'.";
            if (p.Outline is null || p.Outline.Count < 3)
                return $"Panel '{p.PanelId}' needs an outline with at least 3 points.";
            if (p.Outline.Count > MaxOutlinePoints)
                return $"Panel '{p.PanelId}' outline exceeds {MaxOutlinePoints} points.";
            if (p.Outline.Any(pt => !Finite(pt.X) || !Finite(pt.Y) || Math.Abs(pt.X) > MaxDimensionMm || Math.Abs(pt.Y) > MaxDimensionMm))
                return $"Panel '{p.PanelId}' has a non-finite or out-of-range outline point.";
            if (!Finite(p.ThicknessMm) || p.ThicknessMm < 0)
                return $"Panel '{p.PanelId}' thickness must be finite and non-negative.";
            if (p.Material is { Length: > 200 })
                return $"Panel '{p.PanelId}' material name is too long.";
            if (p.Quantity < 1)
                return $"Panel '{p.PanelId}' quantity must be at least 1.";
            if (p.Cutouts is { Count: > MaxCutoutsPerPanel })
                return $"Panel '{p.PanelId}' has too many cutouts.";
            if (p.Cutouts is not null && p.Cutouts.Any(c => c.Ring is null || c.Ring.Count < 3 || c.Ring.Any(pt => !Finite(pt.X) || !Finite(pt.Y))))
                return $"Panel '{p.PanelId}' has an invalid cutout ring.";
        }

        foreach (var sheet in request.Sheets)
        {
            if (!Finite(sheet.WidthMm) || !Finite(sheet.LengthMm) || sheet.WidthMm <= 0 || sheet.LengthMm <= 0
                || sheet.WidthMm > MaxDimensionMm || sheet.LengthMm > MaxDimensionMm)
                return "Every sheet needs finite positive dimensions.";
            if (!Finite(sheet.BorderMm) || sheet.BorderMm < 0 || !Finite(sheet.SpacingMm) || sheet.SpacingMm < 0)
                return "Sheet border and spacing must be finite and non-negative.";
            if (!SheetGrains.Contains(sheet.SheetGrain ?? ""))
                return $"Sheet sheetGrain must be one of: {string.Join(", ", SheetGrains)}.";
            if (sheet.Blocked is not null && sheet.Blocked.Any(b => !Finite(b.MinX) || !Finite(b.MinY) || !Finite(b.MaxX) || !Finite(b.MaxY) || b.MaxX < b.MinX || b.MaxY < b.MinY))
                return "A sheet keep-out rectangle is invalid.";
        }
        return null;
    }

    static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}

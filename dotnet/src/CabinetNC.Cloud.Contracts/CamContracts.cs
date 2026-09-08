namespace CabinetNC.Cloud.Contracts;

// ---------------------------------------------------------------------------------------------
// CAM (operations) and post-processor (NC) job contracts. Hand-written projections of the Domain
// types; the shop's tool choice, machine profile and recipe travel with every job so the server
// keeps no shop configuration state.
// ---------------------------------------------------------------------------------------------

public sealed record CadSegmentDto(string Type, NestPointDto Start, NestPointDto End, NestPointDto? Center, double RadiusMm, bool Cw);

public sealed record PanelFeatureDto(
    string FeatureId,
    string Kind,
    string? FaceId,
    bool Through,
    string? GroupId,
    string? Purpose,
    string? SourceRelationshipId,
    double X,
    double Y,
    double? DiameterMm,
    double? DepthMm,
    double? WidthMm,
    IReadOnlyList<NestPointDto>? Path,
    IReadOnlyList<NestPointDto>? Profile,
    IReadOnlyList<IReadOnlyList<NestPointDto>>? Holes,
    IReadOnlyList<CadSegmentDto>? ProfileSegments,
    IReadOnlyList<IReadOnlyList<CadSegmentDto>>? HoleSegments);

public sealed record CamPanelDto(
    string PanelId,
    IReadOnlyList<NestPointDto> Outline,
    IReadOnlyList<CadSegmentDto>? OutlineSegments,
    string? Material,
    double ThicknessMm,
    // Milling face for every operation of this panel (Panel.Side, else Orientation.MillingFace).
    string? Side,
    IReadOnlyList<PanelFeatureDto> Features);

public sealed record OperationsOptionsDto(
    bool EnableContour,
    bool EnableDrill,
    bool EnableGroove,
    double ClearanceLargeMinShortMm,
    double DrillMaxExclusiveMm,
    // Diameter of the contour tool chosen on the Desktop; 0 disables the automatic offset.
    double ContourToolDiameterMm);

public sealed record SubmitOperationsJobRequest(
    IReadOnlyList<CamPanelDto> Panels,
    IReadOnlyList<NestPlacementDto> Placements,
    OperationsOptionsDto Options);

public sealed record LocalBoundsDto(double MinX, double MinY, double MaxX, double MaxY);

public sealed record CutOpDto(
    string Op,
    string PanelId,
    string? FeatureId,
    bool Placed,
    int SheetIndex,
    double OffsetX,
    double OffsetY,
    double RotationDeg,
    double? X,
    double? Y,
    double? SheetX,
    double? SheetY,
    double? DiameterMm,
    double? DepthMm,
    double? WidthMm,
    double? StepdownMm,
    IReadOnlyList<NestPointDto>? Path,
    IReadOnlyList<IReadOnlyList<NestPointDto>>? PathSegments,
    IReadOnlyList<NestPointDto>? FinishLoop,
    bool ClosePath,
    bool PocketTooSmallForTool,
    LocalBoundsDto? PanelBounds,
    string? ToolId,
    string? Side,
    int SequenceGroup,
    bool Enabled,
    bool IsTongue,
    double? ThicknessMm,
    bool Through,
    IReadOnlyList<CadSegmentDto>? CadPath);

public sealed record OperationsJobResultPayload(
    IReadOnlyList<CutOpDto> Ops,
    int ContourCount,
    int DrillCount,
    int GrooveCount,
    int PocketCount);

public sealed record OperationsJobResult(
    Guid JobId,
    OperationsJobResultPayload Result,
    string EngineVersion,
    string InputSha256,
    string ResultSha256,
    long DurationMs);

public sealed record MachineProfileDto(
    string Id,
    string Name,
    string Dialect,
    string ProgramEnd,
    double SafeZMm,
    double FeedXyMmMin,
    double FeedZMmMin,
    double SpindleRpm,
    double ToolDiameterMm,
    double ContourDepthMm,
    double ContourStepdownMm,
    double DrillPeckMm,
    bool EnableContour,
    bool EnableDrill,
    bool EnableGroove,
    string? OriginNote);

public sealed record ProfileBridgeDto(string Id, string PanelId, string? FeatureId, int SheetIndex, double ArcLengthMm, double X, double Y, double WidthMm, string? PairId);

public sealed record PostRecipeDto(
    double SafeZMm,
    bool Z0IsBoardBottom,
    double TongueFeed,
    double TongueRpm,
    double TonguePlunge,
    double ClearanceFeed,
    double ClearanceRpm,
    double ClearancePlunge,
    double ProfileFirstFeed,
    double ProfileFirstRpm,
    double ProfileFirstPlunge,
    bool ProfileFirstRamp45,
    double ProfileFirstLeaveMm,
    double ProfileBridgeLeaveMm,
    double ProfileLastFeed,
    double ProfileLastRpm,
    double ProfileLastPlunge,
    double ProfileThroughZMm,
    double DrillPlunge,
    double DrillRpm,
    double DrillThroughZMm,
    double GuillotineFeed,
    double GuillotinePlunge,
    double GuillotineThroughZMm,
    bool HomeXyAtEnd,
    IReadOnlyList<ProfileBridgeDto> Bridges);

public sealed record SubmitPostJobRequest(
    IReadOnlyList<CutOpDto> Ops,
    MachineProfileDto Machine,
    PostRecipeDto Recipe);

public sealed record PostJobResultPayload(string NcText, int LineCount, string MachineId, string MachineName);

public sealed record PostJobResult(
    Guid JobId,
    PostJobResultPayload Result,
    string EngineVersion,
    string InputSha256,
    string ResultSha256,
    long DurationMs);

/// <summary>Pure-DTO bounds; the algorithms validate semantics themselves.</summary>
public static class CamRequestRules
{
    public const int MaxPanels = 1000;
    public const int MaxFeaturesPerPanel = 2000;
    public const int MaxOps = 200_000;
    public const double MaxDimensionMm = 100_000;

    public static string? Validate(SubmitOperationsJobRequest request)
    {
        if (request.Panels is null || request.Panels.Count == 0) return "At least one panel is required.";
        if (request.Panels.Count > MaxPanels) return $"At most {MaxPanels} panels per job.";
        if (request.Placements is null) return "Placements are required (may be empty).";
        if (request.Options is null) return "Options are required.";
        if (!Finite(request.Options.ContourToolDiameterMm) || request.Options.ContourToolDiameterMm < 0) return "contourToolDiameterMm must be finite and non-negative.";
        if (!Finite(request.Options.ClearanceLargeMinShortMm) || !Finite(request.Options.DrillMaxExclusiveMm)) return "Clearance and drill thresholds must be finite.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in request.Panels)
        {
            if (string.IsNullOrWhiteSpace(p.PanelId) || p.PanelId.Length > 200) return "Every panel needs a panelId of at most 200 characters.";
            if (!ids.Add(p.PanelId)) return $"Duplicate panelId '{p.PanelId}'.";
            if (p.Outline is null || p.Outline.Count < 3) return $"Panel '{p.PanelId}' needs an outline with at least 3 points.";
            if (p.Outline.Any(pt => !Finite(pt.X) || !Finite(pt.Y) || Math.Abs(pt.X) > MaxDimensionMm || Math.Abs(pt.Y) > MaxDimensionMm)) return $"Panel '{p.PanelId}' has an invalid outline point.";
            if (!Finite(p.ThicknessMm) || p.ThicknessMm < 0) return $"Panel '{p.PanelId}' thickness must be finite and non-negative.";
            if (p.Features is null) return $"Panel '{p.PanelId}' features must be a list (may be empty).";
            if (p.Features.Count > MaxFeaturesPerPanel) return $"Panel '{p.PanelId}' has too many features.";
            if (p.Features.Any(f => string.IsNullOrWhiteSpace(f.FeatureId) || string.IsNullOrWhiteSpace(f.Kind))) return $"Panel '{p.PanelId}' has a feature without id or kind.";
        }
        foreach (var pl in request.Placements)
        {
            if (!ids.Contains(pl.PanelId)) return $"Placement refers to unknown panel '{pl.PanelId}'.";
            if (!Finite(pl.OffsetX) || !Finite(pl.OffsetY) || !Finite(pl.RotationDeg) || pl.SheetIndex < 0) return $"Placement of '{pl.PanelId}' is invalid.";
        }
        return null;
    }

    public static string? Validate(SubmitPostJobRequest request)
    {
        if (request.Ops is null) return "Ops are required (may be empty).";
        if (request.Ops.Count > MaxOps) return $"At most {MaxOps} operations per job.";
        if (request.Machine is null || string.IsNullOrWhiteSpace(request.Machine.Id)) return "A machine profile with an id is required.";
        if (request.Recipe is null) return "A post recipe is required.";
        if (request.Ops.Any(o => string.IsNullOrWhiteSpace(o.Op) || string.IsNullOrWhiteSpace(o.PanelId))) return "Every operation needs op and panelId.";
        if (!Finite(request.Machine.SafeZMm) || !Finite(request.Machine.FeedXyMmMin) || !Finite(request.Recipe.SafeZMm)) return "Machine and recipe numbers must be finite.";
        return null;
    }

    static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}

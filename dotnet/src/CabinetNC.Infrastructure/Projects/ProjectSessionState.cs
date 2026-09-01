using System.Text.Json;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;

namespace CabinetNC.Infrastructure.Projects;

/// <summary>Stage 2–5 session blob stored in project.db <c>session_json</c>.</summary>
public sealed class ProjectSessionState
{
    public string Stage { get; set; } = "load";
    public string LabelerMachineId { get; set; } = "osai_e4_1325";
    public int ActiveNestSheet { get; set; }
    public bool OpsAllSheets { get; set; } = true;
    public bool ShowNest { get; set; }
    public string? NestEngine { get; set; }
    public string? NestEnginePreference { get; set; }
    public int NestSheetCount { get; set; }
    public string? SelectedExportFile { get; set; }
    public List<string> Unplaced { get; set; } = [];
    public List<string> LockedPanelIds { get; set; } = [];
    public List<NestSheetDto> NestSheetsUsed { get; set; } = [];
    public List<StockKindDto> StockKinds { get; set; } = [];
    public List<HeldPartDto> Holding { get; set; } = [];
    public List<PartInPartDto> PartInPart { get; set; } = [];
    public List<GuillotineDto> Guillotine { get; set; } = [];
    public ProjectCamSettings Cam { get; set; } = new();
    public List<BridgeDto> Bridges { get; set; } = [];
    public List<CutOpDto> Ops { get; set; } = [];
    public List<LabelAnchorDto> LabelAnchors { get; set; } = [];
}

public sealed class LabelAnchorDto
{
    public string PanelId { get; set; } = "";
    public double LocalX { get; set; }
    public double LocalY { get; set; }
}

public sealed class ProjectCamSettings
{
    public bool EnableTongue { get; set; } = true;
    public bool EnableProfile { get; set; } = true;
    public bool EnableProfileLast { get; set; } = true;
    public bool EnableClearance { get; set; } = true;
    public bool EnableBridges { get; set; } = true;
    public bool EnableDrilling { get; set; } = true;
    public bool HomeXyAtEnd { get; set; } = true;

    public string ProfFirstTool { get; set; } = "T2";
    public string ProfLastTool { get; set; } = "T2";
    public double ProfFirstFeed { get; set; } = 12000;
    public double ProfFirstRpm { get; set; } = 14500;
    public double ProfFirstPlunge { get; set; } = 1000;
    public bool ProfFirstRamp45 { get; set; }
    public double ProfFirstLeave { get; set; } = 0.5;
    public double ProfLastFeed { get; set; } = 20000;
    public double ProfLastRpm { get; set; } = 14500;
    public double ProfLastPlunge { get; set; } = 1000;
    public double ProfLastThrough { get; set; } = -0.55;

    public double TongueFeed { get; set; } = 9000;
    public double TongueRpm { get; set; } = 14500;
    public double TonguePlunge { get; set; } = 1000;

    public double ProfBridgeWidth { get; set; } = ProfileBridgePlanner.DefaultWidthMm;
    public double ProfTinyAreaM2 { get; set; } = ProfileBridgePlanner.TinyAreaM2;
    public double ProfLargeAreaM2 { get; set; } = ProfileBridgePlanner.LargeAreaM2;
    public double ProfStripAspect { get; set; } = ProfileBridgePlanner.StripAspect;

    public double ClrFeed { get; set; } = 12000;
    public double ClrRpm { get; set; } = 14500;
    public double ClrPlunge { get; set; } = 1000;
    public double ClrLargeMinShort { get; set; } = ClearanceToolPick.LargeMinShortMm;

    public double DrillPlunge { get; set; } = 1000;
    public double DrillRpm { get; set; } = 14500;
    public double DrillThrough { get; set; } = -0.55;
    public double DrillMaxExclusive { get; set; } = ClearanceToolPick.DrillMaxExclusiveMm;

    public double GuillotineFeed { get; set; } = 9000;
    public double GuillotinePlunge { get; set; } = 1000;
    public double GuillotineThrough { get; set; } = -0.55;
}

public sealed class NestSheetDto
{
    public double WidthMm { get; set; }
    public double LengthMm { get; set; }
    public double BorderMm { get; set; }
    public double? InsetLeftMm { get; set; }
    public double? InsetBottomMm { get; set; }
    public double? InsetRightMm { get; set; }
    public double? InsetTopMm { get; set; }
    public double SpacingMm { get; set; }
    public bool AllowRotation { get; set; } = true;
    public bool AllowPartsInPart { get; set; }
    public string? Label { get; set; }
    public string? Material { get; set; }
    public double ThicknessMm { get; set; }
}

public sealed class StockKindDto
{
    public string MaterialId { get; set; } = "";
    public string Label { get; set; } = "";
    public double ThicknessMm { get; set; }
    public double WidthMm { get; set; }
    public double LengthMm { get; set; }
    public double SpacingMm { get; set; }
    public double BorderMm { get; set; }
    public bool AllowRotate90 { get; set; } = true;
    public string SheetGrainKey { get; set; } = "none";
    public bool AllowPartsInPart { get; set; } = true;
    public bool UseLeftoverPieces { get; set; }
    public double LeftoverXMm { get; set; }
    public double LeftoverYMm { get; set; }
}

public sealed class HeldPartDto
{
    public string PanelId { get; set; } = "";
    public string Material { get; set; } = "";
    public double ThicknessMm { get; set; }
    public double RotationDeg { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
}

public sealed class PartInPartDto
{
    public string HostPanelId { get; set; } = "";
    public string ChildPanelId { get; set; } = "";
    public string? FeatureId { get; set; }
    public int SheetIndex { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class GuillotineDto
{
    public int SheetIndex { get; set; }
    public string Kind { get; set; } = "";
    public string? Label { get; set; }
    public double RemnantAreaMm2 { get; set; }
    public double RemnantMinEdgeMm { get; set; }
    public List<XyDto> Polyline { get; set; } = [];
    public List<GuillotineCutDto> Cuts { get; set; } = [];
    public List<GuillotinePieceDto> Pieces { get; set; } = [];
}

public sealed class GuillotineCutDto
{
    public string Kind { get; set; } = "";
    public string? Label { get; set; }
    public double RemnantAreaMm2 { get; set; }
    public double RemnantMinEdgeMm { get; set; }
    public List<XyDto> Polyline { get; set; } = [];
}

public sealed class GuillotinePieceDto
{
    public string Shape { get; set; } = "RECT";
    public double W { get; set; }
    public double H { get; set; }
    public double AreaMm2 { get; set; }
    public double MinEdgeMm { get; set; }
    public double LabelX { get; set; }
    public double LabelY { get; set; }
    public string? Label { get; set; }
}

public sealed class BridgeDto
{
    public string Id { get; set; } = "";
    public string PanelId { get; set; } = "";
    public string? FeatureId { get; set; }
    public int SheetIndex { get; set; }
    public double ArcLengthMm { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double WidthMm { get; set; }
    public string? PairId { get; set; }
}

public sealed class XyDto
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class BoundsDto
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

public sealed class CutOpDto
{
    public string Op { get; set; } = "";
    public string PanelId { get; set; } = "";
    public string? FeatureId { get; set; }
    public bool Placed { get; set; }
    public int SheetIndex { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double RotationDeg { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? SheetX { get; set; }
    public double? SheetY { get; set; }
    public double? DiameterMm { get; set; }
    public double? DepthMm { get; set; }
    public double? WidthMm { get; set; }
    public double? StepdownMm { get; set; }
    public List<XyDto>? Path { get; set; }
    public List<List<XyDto>>? PathSegments { get; set; }
    public List<XyDto>? FinishLoop { get; set; }
    public bool ClosePath { get; set; } = true;
    public bool PocketTooSmallForTool { get; set; }
    public BoundsDto? PanelBounds { get; set; }
    public string? ToolId { get; set; }
    public string? Side { get; set; }
    public int SequenceGroup { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsTongue { get; set; }
    public double? ThicknessMm { get; set; }
    public bool Through { get; set; }
}

public static class ProjectSessionCodec
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ProjectSessionState state) =>
        JsonSerializer.Serialize(state, JsonOpts);

    public static ProjectSessionState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProjectSessionState>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static CutOpDto FromOp(CutOp op) => new()
    {
        Op = op.Op,
        PanelId = op.PanelId,
        FeatureId = op.FeatureId,
        Placed = op.Placed,
        SheetIndex = op.SheetIndex,
        OffsetX = op.OffsetX,
        OffsetY = op.OffsetY,
        RotationDeg = op.RotationDeg,
        X = op.X,
        Y = op.Y,
        SheetX = op.SheetX,
        SheetY = op.SheetY,
        DiameterMm = op.DiameterMm,
        DepthMm = op.DepthMm,
        WidthMm = op.WidthMm,
        StepdownMm = op.StepdownMm,
        Path = FromPts(op.Path),
        PathSegments = op.PathSegments?.Select(seg => FromPts(seg) ?? []).ToList(),
        FinishLoop = FromPts(op.FinishLoop),
        ClosePath = op.ClosePath,
        PocketTooSmallForTool = op.PocketTooSmallForTool,
        PanelBounds = op.PanelBounds is { } b
            ? new BoundsDto { MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY }
            : null,
        ToolId = op.ToolId,
        Side = op.Side,
        SequenceGroup = op.SequenceGroup,
        Enabled = op.Enabled,
        IsTongue = op.IsTongue,
        ThicknessMm = op.ThicknessMm,
        Through = op.Through,
    };

    public static CutOp ToOp(CutOpDto d) => new()
    {
        Op = d.Op,
        PanelId = d.PanelId,
        FeatureId = d.FeatureId,
        Placed = d.Placed,
        SheetIndex = d.SheetIndex,
        OffsetX = d.OffsetX,
        OffsetY = d.OffsetY,
        RotationDeg = d.RotationDeg,
        X = d.X,
        Y = d.Y,
        SheetX = d.SheetX,
        SheetY = d.SheetY,
        DiameterMm = d.DiameterMm,
        DepthMm = d.DepthMm,
        WidthMm = d.WidthMm,
        StepdownMm = d.StepdownMm,
        Path = ToPts(d.Path),
        PathSegments = d.PathSegments?
            .Select(seg => (IReadOnlyList<(double X, double Y)>)ToPts(seg)!)
            .ToList(),
        FinishLoop = ToPts(d.FinishLoop),
        ClosePath = d.ClosePath,
        PocketTooSmallForTool = d.PocketTooSmallForTool,
        PanelBounds = d.PanelBounds is { } b
            ? new LocalBounds(b.MinX, b.MinY, b.MaxX, b.MaxY)
            : null,
        ToolId = d.ToolId,
        Side = d.Side,
        SequenceGroup = d.SequenceGroup,
        Enabled = d.Enabled,
        IsTongue = d.IsTongue,
        ThicknessMm = d.ThicknessMm,
        Through = d.Through,
    };

    public static BridgeDto FromBridge(ProfileBridge b) => new()
    {
        Id = b.Id,
        PanelId = b.PanelId,
        FeatureId = b.FeatureId,
        SheetIndex = b.SheetIndex,
        ArcLengthMm = b.ArcLengthMm,
        X = b.X,
        Y = b.Y,
        WidthMm = b.WidthMm,
        PairId = b.PairId,
    };

    public static ProfileBridge ToBridge(BridgeDto d) => new()
    {
        Id = string.IsNullOrWhiteSpace(d.Id) ? Guid.NewGuid().ToString("N")[..10] : d.Id,
        PanelId = d.PanelId,
        FeatureId = d.FeatureId,
        SheetIndex = d.SheetIndex,
        ArcLengthMm = d.ArcLengthMm,
        X = d.X,
        Y = d.Y,
        WidthMm = d.WidthMm > 0 ? d.WidthMm : ProfileBridgePlanner.DefaultWidthMm,
        PairId = d.PairId,
    };

    public static NestSheetDto FromSheet(NestSheetSpec s) => new()
    {
        WidthMm = s.WidthMm,
        LengthMm = s.LengthMm,
        BorderMm = s.BorderMm,
        InsetLeftMm = s.InsetLeftMm,
        InsetBottomMm = s.InsetBottomMm,
        InsetRightMm = s.InsetRightMm,
        InsetTopMm = s.InsetTopMm,
        SpacingMm = s.SpacingMm,
        AllowRotation = s.AllowRotation,
        AllowPartsInPart = s.AllowPartsInPart,
        Label = s.Label,
        Material = s.Material,
        ThicknessMm = s.ThicknessMm,
    };

    public static NestSheetSpec ToSheet(NestSheetDto d) => new()
    {
        WidthMm = d.WidthMm,
        LengthMm = d.LengthMm,
        BorderMm = d.BorderMm,
        InsetLeftMm = d.InsetLeftMm,
        InsetBottomMm = d.InsetBottomMm,
        InsetRightMm = d.InsetRightMm,
        InsetTopMm = d.InsetTopMm,
        SpacingMm = d.SpacingMm,
        AllowRotation = d.AllowRotation,
        AllowPartsInPart = d.AllowPartsInPart,
        Label = d.Label,
        Material = d.Material,
        ThicknessMm = d.ThicknessMm,
    };

    static List<XyDto>? FromPts(IReadOnlyList<(double X, double Y)>? pts) =>
        pts is null ? null : pts.Select(p => new XyDto { X = p.X, Y = p.Y }).ToList();

    static List<(double X, double Y)>? ToPts(List<XyDto>? pts) =>
        pts is null ? null : pts.Select(p => (p.X, p.Y)).ToList();
}

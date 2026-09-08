using CabinetNC.Cloud.Contracts;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Cloud.NestContract;

/// <summary>
/// Domain ↔ CAM/post wire contract. Every field the operations planner and the NC emitter read is
/// carried; the parity tests run both pipelines on original and round-tripped objects.
/// </summary>
public static class CamContractMapper
{
    // ---- geometry -------------------------------------------------------------------------------

    public static NestPointDto FromPoint(Point2 p) => new(p.X, p.Y);
    public static Point2 ToPoint(NestPointDto p) => new(p.X, p.Y);
    static NestPointDto FromTuple((double X, double Y) p) => new(p.X, p.Y);
    static (double X, double Y) ToTuple(NestPointDto p) => (p.X, p.Y);

    public static CadSegmentDto FromSegment(CadSegment s) =>
        new(s.Type, FromPoint(s.Start), FromPoint(s.End), s.Center is { } c ? FromPoint(c) : null, s.RadiusMm, s.Cw);

    public static CadSegment ToSegment(CadSegmentDto s) =>
        new(s.Type, ToPoint(s.Start), ToPoint(s.End), s.Center is { } c ? ToPoint(c) : null, s.RadiusMm, s.Cw);

    static IReadOnlyList<NestPointDto>? Points(IReadOnlyList<Point2>? pts) => pts?.Select(FromPoint).ToList();
    static IReadOnlyList<Point2>? Points(IReadOnlyList<NestPointDto>? pts) => pts?.Select(ToPoint).ToList();
    static IReadOnlyList<CadSegmentDto>? Segments(IReadOnlyList<CadSegment>? segs) => segs?.Select(FromSegment).ToList();
    static IReadOnlyList<CadSegment>? Segments(IReadOnlyList<CadSegmentDto>? segs) => segs?.Select(ToSegment).ToList();

    // ---- panels & features ----------------------------------------------------------------------

    public static PanelFeatureDto FromFeature(PanelFeature f) =>
        new(f.FeatureId, f.Kind, f.FaceId, f.Through, f.GroupId, f.Purpose, f.SourceRelationshipId, f.X, f.Y,
            f.DiameterMm, f.DepthMm, f.WidthMm, Points(f.Path), Points(f.Profile),
            f.Holes?.Select(h => (IReadOnlyList<NestPointDto>)h.Select(FromPoint).ToList()).ToList(),
            Segments(f.ProfileSegments),
            f.HoleSegments?.Select(h => (IReadOnlyList<CadSegmentDto>)h.Select(FromSegment).ToList()).ToList());

    public static PanelFeature ToFeature(PanelFeatureDto f) =>
        new()
        {
            FeatureId = f.FeatureId,
            Kind = f.Kind,
            FaceId = f.FaceId,
            Through = f.Through,
            GroupId = f.GroupId,
            Purpose = f.Purpose,
            SourceRelationshipId = f.SourceRelationshipId,
            X = f.X,
            Y = f.Y,
            DiameterMm = f.DiameterMm,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
            Path = Points(f.Path),
            Profile = Points(f.Profile),
            Holes = f.Holes?.Select(h => (IReadOnlyList<Point2>)h.Select(ToPoint).ToList()).ToList(),
            ProfileSegments = Segments(f.ProfileSegments),
            HoleSegments = f.HoleSegments?.Select(h => (IReadOnlyList<CadSegment>)h.Select(ToSegment).ToList()).ToList(),
        };

    public static CamPanelDto FromPanel(Panel p) =>
        new(p.PanelId, p.Outline.Points.Select(FromPoint).ToList(), Segments(p.Outline.Segments), p.Material, p.ThicknessMm,
            p.Side ?? p.Orientation?.MillingFace,
            p.Features.Select(FromFeature).ToList());

    public static Panel ToPanel(CamPanelDto p) =>
        new()
        {
            PanelId = p.PanelId,
            Material = p.Material,
            ThicknessMm = p.ThicknessMm,
            Side = p.Side,
            Outline = new Outline { Points = p.Outline.Select(ToPoint).ToList(), Segments = Segments(p.OutlineSegments) },
            Features = (p.Features ?? []).Select(ToFeature).ToList(),
        };

    public static NestPlacementDto FromPlacement(NestPlacement p) => new(p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg);
    public static NestPlacement ToPlacement(NestPlacementDto p) => new() { PanelId = p.PanelId, SheetIndex = p.SheetIndex, OffsetX = p.OffsetX, OffsetY = p.OffsetY, RotationDeg = p.RotationDeg };

    // ---- operations -----------------------------------------------------------------------------

    public static CutOpDto FromOp(CutOp o) =>
        new(o.Op, o.PanelId, o.FeatureId, o.Placed, o.SheetIndex, o.OffsetX, o.OffsetY, o.RotationDeg,
            o.X, o.Y, o.SheetX, o.SheetY, o.DiameterMm, o.DepthMm, o.WidthMm, o.StepdownMm,
            o.Path?.Select(FromTuple).ToList(),
            o.PathSegments?.Select(s => (IReadOnlyList<NestPointDto>)s.Select(FromTuple).ToList()).ToList(),
            o.FinishLoop?.Select(FromTuple).ToList(),
            o.ClosePath, o.PocketTooSmallForTool,
            o.PanelBounds is { } b ? new LocalBoundsDto(b.MinX, b.MinY, b.MaxX, b.MaxY) : null,
            o.ToolId, o.Side, o.SequenceGroup, o.Enabled, o.IsTongue, o.ThicknessMm, o.Through,
            Segments(o.CadPath));

    public static CutOp ToOp(CutOpDto o) =>
        new()
        {
            Op = o.Op,
            PanelId = o.PanelId,
            FeatureId = o.FeatureId,
            Placed = o.Placed,
            SheetIndex = o.SheetIndex,
            OffsetX = o.OffsetX,
            OffsetY = o.OffsetY,
            RotationDeg = o.RotationDeg,
            X = o.X,
            Y = o.Y,
            SheetX = o.SheetX,
            SheetY = o.SheetY,
            DiameterMm = o.DiameterMm,
            DepthMm = o.DepthMm,
            WidthMm = o.WidthMm,
            StepdownMm = o.StepdownMm,
            Path = o.Path?.Select(ToTuple).ToList(),
            PathSegments = o.PathSegments?.Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(ToTuple).ToList()).ToList(),
            FinishLoop = o.FinishLoop?.Select(ToTuple).ToList(),
            ClosePath = o.ClosePath,
            PocketTooSmallForTool = o.PocketTooSmallForTool,
            PanelBounds = o.PanelBounds is { } b ? new LocalBounds(b.MinX, b.MinY, b.MaxX, b.MaxY) : null,
            ToolId = o.ToolId,
            Side = o.Side,
            SequenceGroup = o.SequenceGroup,
            Enabled = o.Enabled,
            IsTongue = o.IsTongue,
            ThicknessMm = o.ThicknessMm,
            Through = o.Through,
            CadPath = Segments(o.CadPath),
        };

    public static OperationsJobResultPayload FromOps(IReadOnlyList<CutOp> ops) =>
        new(ops.Select(FromOp).ToList(),
            ops.Count(o => o.Op == "contour"), ops.Count(o => o.Op == "drill"), ops.Count(o => o.Op == "groove"), ops.Count(o => o.Op == "pocket"));

    // ---- machine & recipe -----------------------------------------------------------------------

    public static MachineProfileDto FromMachine(MachineProfile m) =>
        new(m.Id, m.Name, m.Dialect, m.ProgramEnd, m.SafeZMm, m.FeedXyMmMin, m.FeedZMmMin, m.SpindleRpm, m.ToolDiameterMm,
            m.ContourDepthMm, m.ContourStepdownMm, m.DrillPeckMm, m.EnableContour, m.EnableDrill, m.EnableGroove, m.OriginNote);

    public static MachineProfile ToMachine(MachineProfileDto m) =>
        new()
        {
            Id = m.Id,
            Name = m.Name,
            Dialect = m.Dialect,
            ProgramEnd = m.ProgramEnd,
            SafeZMm = m.SafeZMm,
            FeedXyMmMin = m.FeedXyMmMin,
            FeedZMmMin = m.FeedZMmMin,
            SpindleRpm = m.SpindleRpm,
            ToolDiameterMm = m.ToolDiameterMm,
            ContourDepthMm = m.ContourDepthMm,
            ContourStepdownMm = m.ContourStepdownMm,
            DrillPeckMm = m.DrillPeckMm,
            EnableContour = m.EnableContour,
            EnableDrill = m.EnableDrill,
            EnableGroove = m.EnableGroove,
            OriginNote = m.OriginNote,
        };

    public static ProfileBridgeDto FromBridge(ProfileBridge b) => new(b.Id, b.PanelId, b.FeatureId, b.SheetIndex, b.ArcLengthMm, b.X, b.Y, b.WidthMm, b.PairId);
    public static ProfileBridge ToBridge(ProfileBridgeDto b) => new() { Id = b.Id, PanelId = b.PanelId, FeatureId = b.FeatureId, SheetIndex = b.SheetIndex, ArcLengthMm = b.ArcLengthMm, X = b.X, Y = b.Y, WidthMm = b.WidthMm, PairId = b.PairId };

    public static PostRecipeDto FromRecipe(PostRecipe r) =>
        new(r.SafeZMm, r.Z0IsBoardBottom, r.TongueFeed, r.TongueRpm, r.TonguePlunge, r.ClearanceFeed, r.ClearanceRpm, r.ClearancePlunge,
            r.ProfileFirstFeed, r.ProfileFirstRpm, r.ProfileFirstPlunge, r.ProfileFirstRamp45, r.ProfileFirstLeaveMm, r.ProfileBridgeLeaveMm,
            r.ProfileLastFeed, r.ProfileLastRpm, r.ProfileLastPlunge, r.ProfileThroughZMm, r.DrillPlunge, r.DrillRpm, r.DrillThroughZMm,
            r.GuillotineFeed, r.GuillotinePlunge, r.GuillotineThroughZMm, r.HomeXyAtEnd, r.Bridges.Select(FromBridge).ToList());

    public static PostRecipe ToRecipe(PostRecipeDto r) =>
        new()
        {
            SafeZMm = r.SafeZMm,
            Z0IsBoardBottom = r.Z0IsBoardBottom,
            TongueFeed = r.TongueFeed,
            TongueRpm = r.TongueRpm,
            TonguePlunge = r.TonguePlunge,
            ClearanceFeed = r.ClearanceFeed,
            ClearanceRpm = r.ClearanceRpm,
            ClearancePlunge = r.ClearancePlunge,
            ProfileFirstFeed = r.ProfileFirstFeed,
            ProfileFirstRpm = r.ProfileFirstRpm,
            ProfileFirstPlunge = r.ProfileFirstPlunge,
            ProfileFirstRamp45 = r.ProfileFirstRamp45,
            ProfileFirstLeaveMm = r.ProfileFirstLeaveMm,
            ProfileBridgeLeaveMm = r.ProfileBridgeLeaveMm,
            ProfileLastFeed = r.ProfileLastFeed,
            ProfileLastRpm = r.ProfileLastRpm,
            ProfileLastPlunge = r.ProfileLastPlunge,
            ProfileThroughZMm = r.ProfileThroughZMm,
            DrillPlunge = r.DrillPlunge,
            DrillRpm = r.DrillRpm,
            DrillThroughZMm = r.DrillThroughZMm,
            GuillotineFeed = r.GuillotineFeed,
            GuillotinePlunge = r.GuillotinePlunge,
            GuillotineThroughZMm = r.GuillotineThroughZMm,
            HomeXyAtEnd = r.HomeXyAtEnd,
            Bridges = (r.Bridges ?? []).Select(ToBridge).ToList(),
        };
}

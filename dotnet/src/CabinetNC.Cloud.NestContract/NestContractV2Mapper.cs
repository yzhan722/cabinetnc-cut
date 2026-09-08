using CabinetNC.Cloud.Contracts;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Cloud.NestContract;

/// <summary>
/// Projects Domain nest inputs onto the v2 wire contract and back. The projection keeps exactly what
/// the nesting engines read (outline, cutout rings, material/thickness, grain, allowed rotations,
/// quantity); <see cref="ToPanel"/> of <see cref="FromPanel"/> is therefore nesting-equivalent to the
/// original panel, which the parity tests assert by running the engines on both.
/// </summary>
public static class NestContractV2Mapper
{
    public static SubmitNestJobRequestV2 ToRequest(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> sheets,
        string enginePreference,
        TimeSpan advancedTimeout) =>
        new(
            panels.Select(FromPanel).ToList(),
            sheets.Select(FromSheet).ToList(),
            FromSettings(settings),
            enginePreference,
            Math.Clamp((int)Math.Ceiling(advancedTimeout.TotalSeconds), 1, NestRequestV2Rules.MaxAdvancedTimeoutSeconds));

    public static NestPanelDto FromPanel(Panel panel) =>
        new(
            panel.PanelId,
            panel.Outline.Points.Select(p => new NestPointDto(p.X, p.Y)).ToList(),
            panel.Features
                .Where(PanelEdit.IsCutout)
                .Select(f => (f.FeatureId, Ring: f.Path ?? f.Profile))
                .Where(t => t.Ring is { Count: >= 3 })
                .Select(t => new NestCutoutDto(t.FeatureId, t.Ring!.Select(p => new NestPointDto(p.X, p.Y)).ToList()))
                .ToList(),
            panel.Material,
            panel.ThicknessMm,
            panel.GrainDirection ?? panel.Orientation?.GrainDirection,
            panel.AllowedRotations?.ToList(),
            panel.Quantity);

    public static Panel ToPanel(NestPanelDto dto) =>
        new()
        {
            PanelId = dto.PanelId,
            Material = dto.Material,
            ThicknessMm = dto.ThicknessMm,
            GrainDirection = dto.GrainDirection,
            AllowedRotations = dto.AllowedRotations?.ToList(),
            Quantity = dto.Quantity,
            Outline = new Outline { Points = dto.Outline.Select(p => new Point2(p.X, p.Y)).ToList() },
            Features = (dto.Cutouts ?? [])
                .Select(c => new PanelFeature
                {
                    FeatureId = c.FeatureId,
                    Kind = "cutout",
                    Through = true,
                    FaceId = "THROUGH",
                    Path = c.Ring.Select(p => new Point2(p.X, p.Y)).ToList(),
                })
                .ToList(),
        };

    public static NestSheetDto FromSheet(NestSheetSpec s) =>
        new(
            s.WidthMm, s.LengthMm, s.BorderMm, s.InsetLeftMm, s.InsetBottomMm, s.InsetRightMm, s.InsetTopMm,
            s.SpacingMm, s.AllowRotation, s.AllowPartsInPart,
            s.Blocked.Select(b => new NestBlockedRectDto(b.MinX, b.MinY, b.MaxX, b.MaxY)).ToList(),
            s.Label, s.Material, s.ThicknessMm, s.SheetGrain.ToString());

    public static NestSheetSpec ToSheet(NestSheetDto d) =>
        new()
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
            Blocked = (d.Blocked ?? []).Select(b => new NestBlockedRect { MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY }).ToList(),
            Label = d.Label,
            Material = d.Material,
            ThicknessMm = d.ThicknessMm,
            SheetGrain = Enum.Parse<SheetGrainKind>(d.SheetGrain, ignoreCase: true),
        };

    public static NestSettingsDto FromSettings(NestSettings s) =>
        new(s.MarginMm, s.ClearanceMm, s.AllowRotation, s.AllowedRotations.ToList(), s.RotationStepDeg, s.GrainLock,
            s.SheetGrain.ToString(), s.MirrorPermission, s.PreferLockedPlacements);

    public static NestSettings ToSettings(NestSettingsDto d) =>
        new()
        {
            MarginMm = d.MarginMm,
            ClearanceMm = d.ClearanceMm,
            AllowRotation = d.AllowRotation,
            AllowedRotations = d.AllowedRotations?.ToList() ?? [],
            RotationStepDeg = d.RotationStepDeg,
            GrainLock = d.GrainLock,
            SheetGrain = Enum.Parse<SheetGrainKind>(d.SheetGrain, ignoreCase: true),
            MirrorPermission = d.MirrorPermission,
            PreferLockedPlacements = d.PreferLockedPlacements,
        };

    /// <summary>The Desktop's part size: axis-aligned bounds of the outline. Both sides must use this one.</summary>
    public static (double w, double h) AabbSizeOf(Panel p)
    {
        var pts = p.Outline.Points;
        if (pts.Count < 2) return (0, 0);
        return (pts.Max(pt => pt.X) - pts.Min(pt => pt.X), pts.Max(pt => pt.Y) - pts.Min(pt => pt.Y));
    }

    public static NestJobResultPayloadV2 FromResult(NestResult result, NestEngineRunLog log) =>
        new(
            result.Engine,
            result.Placements.Select(p => new NestPlacementDto(p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)).ToList(),
            result.SheetCount,
            result.Unplaced.ToList(),
            result.UnplacedReasons.Select(r => new NestUnplacedReasonDto(r.PanelId, r.Code, r.Message)).ToList(),
            result.GroupReports.Select(g => new NestGroupReportDto(g.Key.Material, g.Key.ThicknessMm, g.PartCount, g.PlacedCount, g.SheetCount, g.LocalSheetStart, g.UtilizationPct)).ToList(),
            result.SheetsUsed.Select(FromSheet).ToList(),
            result.PartInPartSlots.Select(s => new NestPartInPartSlotDto(s.HostPanelId, s.ChildPanelId, s.FeatureId, s.SheetIndex, s.Enabled)).ToList(),
            new NestRunLogDto(log.SelectedEngine, log.AttemptedEngine, log.FallbackReason, log.ElapsedMs, log.UtilizationHintPct));

    public static (NestResult Result, NestEngineRunLog Log) ToResult(NestJobResultPayloadV2 payload)
    {
        var result = new NestResult
        {
            Engine = payload.Engine,
            Placements = payload.Placements.Select(p => new NestPlacement { PanelId = p.PanelId, SheetIndex = p.SheetIndex, OffsetX = p.OffsetX, OffsetY = p.OffsetY, RotationDeg = p.RotationDeg }).ToList(),
            SheetCount = payload.SheetCount,
            Unplaced = payload.Unplaced.ToList(),
            UnplacedReasons = payload.UnplacedReasons.Select(r => new NestUnplacedReason { PanelId = r.PanelId, Code = r.Code, Message = r.Message }).ToList(),
            GroupReports = payload.GroupReports.Select(g => new NestGroupReport
            {
                Key = new NestGroupKey(g.Material, g.ThicknessMm),
                PartCount = g.PartCount,
                PlacedCount = g.PlacedCount,
                SheetCount = g.SheetCount,
                LocalSheetStart = g.LocalSheetStart,
                UtilizationPct = g.UtilizationPct,
            }).ToList(),
            SheetsUsed = payload.SheetsUsed.Select(ToSheet).ToList(),
            PartInPartSlots = payload.PartInPartSlots.Select(s => new PartInPartSlot { HostPanelId = s.HostPanelId, ChildPanelId = s.ChildPanelId, FeatureId = s.FeatureId, SheetIndex = s.SheetIndex, Enabled = s.Enabled }).ToList(),
        };
        var log = new NestEngineRunLog
        {
            SelectedEngine = payload.Log.SelectedEngine,
            AttemptedEngine = payload.Log.AttemptedEngine,
            FallbackReason = payload.Log.FallbackReason,
            ElapsedMs = payload.Log.ElapsedMs,
            UtilizationHintPct = payload.Log.UtilizationHintPct,
        };
        return (result, log);
    }
}

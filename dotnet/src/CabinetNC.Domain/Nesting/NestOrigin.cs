namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

/// <summary>
/// Pack from the sheet's bottom-right without changing a part's shape.
/// The engine only packs from the bottom-left, so it runs on a left-right mirrored
/// copy of the outlines. Mirroring the result back restores every outline exactly
/// (a mirror done twice) and shifts the pack onto the right edge. Rotation θ comes
/// back as −θ, which is a turn, not a flip.
/// </summary>
public static class NestOrigin
{
    public static NestEngineRequest ToMirrorFrame(NestEngineRequest req) => new()
    {
        Panels = req.Panels.Select(MirrorProxy).ToList(),
        Settings = MirrorSettings(req.Settings),
        StockTemplates = req.StockTemplates.Select(MirrorSheet).ToList(),
        SizeOf = req.SizeOf,
        EnginePreference = req.EnginePreference,
        AdvancedTimeout = req.AdvancedTimeout,
        Progress = req.Progress,
    };

    public static NestResult FromMirrorFrame(
        NestResult frame,
        IReadOnlyList<Panel> realPanels,
        IReadOnlyList<NestSheetSpec> realTemplates)
    {
        var byId = new Dictionary<string, Panel>(StringComparer.Ordinal);
        foreach (var p in realPanels)
            byId[p.PanelId] = p;
        var fallbackW = realTemplates.Count > 0 ? realTemplates[0].WidthMm : 1220;

        var placements = new List<NestPlacement>(frame.Placements.Count);
        foreach (var p in frame.Placements)
        {
            var rot = NormalizeDeg(-p.RotationDeg);
            var sheetW = p.SheetIndex >= 0 && p.SheetIndex < frame.SheetsUsed.Count
                ? frame.SheetsUsed[p.SheetIndex].WidthMm
                : fallbackW;
            var w = byId.TryGetValue(p.PanelId, out var panel) ? RotatedWidth(panel, rot) : 0;
            placements.Add(new NestPlacement
            {
                PanelId = p.PanelId,
                SheetIndex = p.SheetIndex,
                OffsetX = sheetW - p.OffsetX - w,
                OffsetY = p.OffsetY,
                RotationDeg = rot,
            });
        }

        return new NestResult
        {
            Engine = frame.Engine,
            Placements = placements,
            SheetCount = frame.SheetCount,
            Unplaced = frame.Unplaced,
            UnplacedReasons = frame.UnplacedReasons,
            GroupReports = frame.GroupReports,
            SheetsUsed = frame.SheetsUsed.Select(MirrorSheet).ToList(),
            PartInPartSlots = frame.PartInPartSlots,
        };
    }

    /// <summary>Outline only. Features stay on the real panel, so the face is never flipped.</summary>
    public static Panel MirrorProxy(Panel p)
    {
        var pts = p.Outline.Points.Select(q => new Point2(-q.X, q.Y)).Reverse().ToList();
        return new Panel
        {
            PanelId = p.PanelId,
            Name = p.Name,
            Material = p.Material,
            ThicknessMm = p.ThicknessMm,
            DecorId = p.DecorId,
            SubstrateId = p.SubstrateId,
            ColorName = p.ColorName,
            SurfaceMode = p.SurfaceMode,
            Quantity = p.Quantity,
            AllowedRotations = p.AllowedRotations?.Select(r => (int)NormalizeDeg(-r)).ToList(),
            GrainDirection = p.GrainDirection,
            Outline = new Outline { Points = pts, Closed = p.Outline.Closed, Frame = p.Outline.Frame },
            Orientation = p.Orientation,
            Side = p.Side,
        };
    }

    public static NestSheetSpec MirrorSheet(NestSheetSpec s) => new()
    {
        WidthMm = s.WidthMm,
        LengthMm = s.LengthMm,
        BorderMm = s.BorderMm,
        InsetLeftMm = s.InsetRightMm,
        InsetBottomMm = s.InsetBottomMm,
        InsetRightMm = s.InsetLeftMm,
        InsetTopMm = s.InsetTopMm,
        SpacingMm = s.SpacingMm,
        AllowRotation = s.AllowRotation,
        AllowPartsInPart = s.AllowPartsInPart,
        Blocked = s.Blocked.Select(b => new NestBlockedRect
        {
            MinX = s.WidthMm - b.MaxX,
            MinY = b.MinY,
            MaxX = s.WidthMm - b.MinX,
            MaxY = b.MaxY,
        }).ToList(),
        Label = s.Label,
        Material = s.Material,
        ThicknessMm = s.ThicknessMm,
        SheetGrain = s.SheetGrain,
    };

    static NestSettings MirrorSettings(NestSettings s) => new()
    {
        MarginMm = s.MarginMm,
        ClearanceMm = s.ClearanceMm,
        AllowRotation = s.AllowRotation,
        AllowedRotations = s.AllowedRotations.Select(r => (int)NormalizeDeg(-r)).ToList(),
        RotationStepDeg = s.RotationStepDeg,
        GrainLock = s.GrainLock,
        SheetGrain = s.SheetGrain,
        MirrorPermission = s.MirrorPermission,
        PreferLockedPlacements = s.PreferLockedPlacements,
    };

    public static double RotatedWidth(Panel panel, double rotationDeg)
    {
        var pts = panel.Outline.Points;
        if (pts.Count == 0) return 0;
        var r = rotationDeg * Math.PI / 180;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var q in pts)
        {
            var x = q.X * c - q.Y * s;
            if (x < min) min = x;
            if (x > max) max = x;
        }
        return max - min;
    }

    public static double NormalizeDeg(double deg)
    {
        var d = Math.Round(deg, 6) % 360;
        if (d < 0) d += 360;
        return Math.Abs(d - 360) < 1e-6 ? 0 : d;
    }
}

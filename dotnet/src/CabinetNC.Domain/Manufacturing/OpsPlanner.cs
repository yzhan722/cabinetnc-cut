namespace CabinetNC.Domain.Manufacturing;

public sealed record CutOp
{
    public required string Op { get; init; } // contour | drill | groove | pocket
    public required string PanelId { get; init; }
    public string? FeatureId { get; init; }
    public bool Placed { get; init; }
    public int SheetIndex { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double RotationDeg { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? SheetX { get; init; }
    public double? SheetY { get; init; }
    public double? DiameterMm { get; init; }
    public double? DepthMm { get; init; }
    public double? WidthMm { get; init; }
    public double? StepdownMm { get; init; }
    public IReadOnlyList<(double X, double Y)>? Path { get; init; }
    /// <summary>Disjoint path segments for pocket clear (scan strokes). Prefer over flat Path.</summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>>? PathSegments { get; init; }
    /// <summary>Optional closed finish loop for pocket onion-skin boundary.</summary>
    public IReadOnlyList<(double X, double Y)>? FinishLoop { get; init; }
    /// <summary>When true (contours), emitter closes to first point; pockets use false.</summary>
    public bool ClosePath { get; init; } = true;
    /// <summary>Pocket inset cleared empty — tool cannot fit (must fail preflight).</summary>
    public bool PocketTooSmallForTool { get; init; }
    public Nesting.LocalBounds? PanelBounds { get; init; }
    /// <summary>Bound tool — required for export (Day 7).</summary>
    public string? ToolId { get; init; }
    /// <summary>A | B face.</summary>
    public string? Side { get; init; }
    public int SequenceGroup { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Tongue-receiving half groove — T1. All other grooves are T2.</summary>
    public bool IsTongue { get; init; }
    /// <summary>Panel thickness (mm). Needed when Z0 is the board bottom.</summary>
    public double? ThicknessMm { get; init; }
    /// <summary>Through feature — last Z uses through overshoot, not blind depth.</summary>
    public bool Through { get; init; }
    /// <summary>Exact CAD tool-centre loop when Fusion exported line/arc entities.</summary>
    public IReadOnlyList<Geometry.CadSegment>? CadPath { get; init; }
}

/// <summary>Port of src/ops.js featuresToOps + attachOpsToNest (contour + drill + groove).</summary>
public static class OpsPlanner
{
    public static IReadOnlyList<CutOp> FeaturesToOps(
        IEnumerable<Parts.Panel> panels,
        bool enableContour = true,
        bool enableDrill = true,
        bool enableGroove = true,
        double clearanceLargeMinShortMm = ClearanceToolPick.LargeMinShortMm,
        double drillMaxExclusiveMm = ClearanceToolPick.DrillMaxExclusiveMm)
    {
        clearanceLargeMinShortMm = ClearanceToolPick.NormalizeLargeMinShortMm(clearanceLargeMinShortMm);
        drillMaxExclusiveMm = ClearanceToolPick.NormalizeDrillMaxExclusiveMm(drillMaxExclusiveMm);
        var ops = new List<CutOp>();
        var panelList = panels.ToList();
        foreach (var panel in panelList)
        {
            var pts = panel.Outline.Points;
            var bounds = Nesting.NestTransform.BoundsOf(panel);
            if (enableContour && pts.Count >= 3)
            {
                ops.Add(new CutOp
                {
                    Op = "contour",
                    PanelId = panel.PanelId,
                    Path = pts.Select(p => (p.X, p.Y)).ToList(),
                    CadPath = panel.Outline.Segments,
                    PanelBounds = bounds,
                    DepthMm = CamSafety.OuterContourDepthMm(panel.ThicknessMm),
                    Side = panel.Side ?? panel.Orientation?.MillingFace,
                    ThicknessMm = panel.ThicknessMm,
                    Through = true,
                });
            }
            foreach (var f in panel.Features)
            {
                if (enableDrill && ClearanceToolPick.IsDrillHole(f, drillMaxExclusiveMm))
                {
                    if (PocketClearer.IsOffPanelArtifact(f.X, f.Y, bounds))
                        continue;
                    ops.Add(new CutOp
                    {
                        Op = "drill",
                        PanelId = panel.PanelId,
                        FeatureId = f.FeatureId,
                        X = f.X,
                        Y = f.Y,
                        DiameterMm = f.DiameterMm,
                        DepthMm = f.DepthMm ?? panel.ThicknessMm,
                        PanelBounds = bounds,
                        Side = panel.Side ?? panel.Orientation?.MillingFace,
                        ThicknessMm = panel.ThicknessMm,
                        Through = f.Through,
                    });
                }
                else if (enableContour
                    && Parts.PanelEdit.IsHole(f)
                    && ClearanceToolPick.CupOutline(f) is { Count: >= 3 } holeOutline)
                {
                    if (f.Through)
                        AddThroughHoleContour(ops, panel, f, holeOutline, bounds);
                    else
                        AddPocketOp(ops, panel, f, holeOutline, bounds, clearanceLargeMinShortMm);
                }
                else if (enableContour
                    && ClearanceToolPick.IsHingeFeature(f)
                    && ClearanceToolPick.CupOutline(f) is { Count: >= 3 } cupOutline)
                {
                    AddPocketOp(ops, panel, f, cupOutline, bounds, clearanceLargeMinShortMm);
                }
                else if (enableGroove && f.Kind.Contains("groove", StringComparison.OrdinalIgnoreCase)
                         && f.Path is { Count: >= 2 } path)
                {
                    if (PocketClearer.IsOffPanelArtifact(path.Select(p => (p.X, p.Y)).ToList(), bounds))
                        continue;
                    var isTongue = Parts.PanelEdit.IsTongueGroove(f);
                    var width = GrooveClear.ResolveWidthMm(f);
                    var toolId = isTongue
                        ? TroyRecipe.TongueToolId
                        : ClearanceToolPick.Pick(f, clearanceLargeMinShortMm);
                    var toolDia = ClearanceToolPick.DiameterOf(toolId);
                    IReadOnlyList<(double X, double Y)> groovePath =
                        path.Select(p => (p.X, p.Y)).ToList();
                    IReadOnlyList<IReadOnlyList<(double X, double Y)>>? segments = null;
                    IReadOnlyList<(double X, double Y)>? finish = null;
                    var tooSmall = false;
                    var cleared = GrooveClear.TryClear(f, toolDia, bounds);
                    if (cleared is not null)
                    {
                        if (cleared.TooSmallForTool)
                            tooSmall = true;
                        else
                        {
                            segments = cleared.Segments;
                            finish = cleared.FinishLoop;
                            if (cleared.Path.Count >= 2)
                                groovePath = cleared.Path;
                        }
                    }
                    ops.Add(new CutOp
                    {
                        Op = "groove",
                        PanelId = panel.PanelId,
                        FeatureId = f.FeatureId,
                        DepthMm = f.DepthMm,
                        WidthMm = width > 1e-9 ? width : f.WidthMm,
                        Path = groovePath,
                        PathSegments = segments,
                        FinishLoop = finish,
                        ClosePath = false,
                        PocketTooSmallForTool = tooSmall,
                        PanelBounds = bounds,
                        Side = panel.Side ?? panel.Orientation?.MillingFace,
                        IsTongue = isTongue,
                        ToolId = toolId,
                        ThicknessMm = panel.ThicknessMm,
                        Through = f.Through,
                    });
                }
                else if (enableContour && f.Kind.Contains("pocket", StringComparison.OrdinalIgnoreCase)
                         && f.Path is { Count: >= 3 } pocketPath)
                {
                    AddPocketOp(
                        ops, panel, f,
                        pocketPath.Select(p => (p.X, p.Y)).ToList(),
                        bounds, clearanceLargeMinShortMm);
                }
                else if (enableContour && f.Kind.Contains("cutout", StringComparison.OrdinalIgnoreCase)
                         && f.Path is { Count: >= 3 } cutPath)
                {
                    if (PocketClearer.IsOffPanelArtifact(cutPath.Select(p => (p.X, p.Y)).ToList(), bounds))
                        continue;
                    ops.Add(new CutOp
                    {
                        Op = "contour",
                        PanelId = panel.PanelId,
                        FeatureId = f.FeatureId,
                        DepthMm = f.DepthMm ?? CamSafety.OuterContourDepthMm(panel.ThicknessMm),
                        Path = cutPath.Select(p => (p.X, p.Y)).ToList(),
                        CadPath = f.ProfileSegments,
                        PanelBounds = bounds,
                        Side = panel.Side ?? panel.Orientation?.MillingFace,
                        ThicknessMm = panel.ThicknessMm,
                        Through = true,
                    });
                }
            }
        }

        var byId = panelList.ToDictionary(p => p.PanelId, p => p);
        var bound = ToolBinder.BindAll(ops);
        var depthApplied = CamSafety.ApplyPanelDepths(bound, byId);
        return CamSafety.OrderSafe(depthApplied).ToList();
    }

    static void AddThroughHoleContour(
        List<CutOp> ops,
        Parts.Panel panel,
        Parts.PanelFeature f,
        IReadOnlyList<(double X, double Y)> outline,
        Nesting.LocalBounds? bounds)
    {
        if (PocketClearer.IsExportSliver(outline))
            return;
        if (bounds is { } panelBounds && PocketClearer.IsOffPanelArtifact(outline, panelBounds))
            return;
        ops.Add(new CutOp
        {
            Op = "contour",
            PanelId = panel.PanelId,
            FeatureId = f.FeatureId,
            DepthMm = f.DepthMm ?? CamSafety.OuterContourDepthMm(panel.ThicknessMm),
            Path = outline,
            CadPath = f.ProfileSegments,
            DiameterMm = f.DiameterMm,
            PanelBounds = bounds,
            Side = panel.Side ?? panel.Orientation?.MillingFace,
            ThicknessMm = panel.ThicknessMm,
            Through = true,
        });
    }

    static void AddPocketOp(
        List<CutOp> ops,
        Parts.Panel panel,
        Parts.PanelFeature f,
        IReadOnlyList<(double X, double Y)> outline,
        Nesting.LocalBounds? bounds,
        double clearanceLargeMinShortMm)
    {
        if (PocketClearer.IsExportSliver(outline))
            return;
        if (bounds is { } panelBounds && PocketClearer.IsOffPanelArtifact(outline, panelBounds))
            return;

        var islands = PocketClearIslands.Keep(panel, f);
        var toolId = ClearanceToolPick.Pick(f, clearanceLargeMinShortMm, islandHoles: islands);
        var toolDia = ClearanceToolPick.DiameterOf(toolId);
        var directToSize = ClearanceToolPick.IsHingeFeature(f);
        var holes = islands
            .Select(ring => (IReadOnlyList<(double X, double Y)>)ring.Select(p => (p.X, p.Y)).ToList())
            .ToList();
        var cleared = PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = outline,
            Holes = holes,
            ToolDiameterMm = toolDia,
            OnionSkinMm = directToSize ? 0 : PocketClearer.DefaultOnionSkinMm,
            EmitFinishLoop = !directToSize && holes.Count == 0,
            CloseClearRings = directToSize,
            PanelBounds = bounds,
        });
        ops.Add(new CutOp
        {
            Op = "pocket",
            PanelId = panel.PanelId,
            FeatureId = f.FeatureId,
            DepthMm = f.DepthMm,
            Path = cleared.Path,
            PathSegments = cleared.Segments,
            FinishLoop = cleared.FinishLoop,
            ClosePath = false,
            PocketTooSmallForTool = cleared.TooSmallForTool,
            PanelBounds = bounds,
            Side = panel.Side ?? panel.Orientation?.MillingFace,
            StepdownMm = toolDia * 0.5,
            ToolId = toolId,
            ThicknessMm = panel.ThicknessMm,
            Through = f.Through,
        });
    }

    public static IReadOnlyList<CutOp> AttachToNest(IEnumerable<CutOp> ops, IEnumerable<Nesting.NestPlacement> placements)
    {
        var byId = placements.ToDictionary(p => p.PanelId, p => p);
        var opList = ops.ToList();
        var boundsByPanel = opList
            .Where(o => o.Op == "contour" && o.FeatureId is null && o.Path is { Count: >= 3 })
            .GroupBy(o => o.PanelId)
            .ToDictionary(
                g => g.Key,
                g => (Nesting.LocalBounds?)Nesting.NestTransform.BoundsOf(g.First().Path!));
        return opList.Select(op =>
        {
            if (!byId.TryGetValue(op.PanelId, out var place))
                return op with { Placed = false };

            var bounds = op.PanelBounds
                ?? boundsByPanel.GetValueOrDefault(op.PanelId)
                ?? (op.Path is { Count: > 0 } sourcePath
                    ? Nesting.NestTransform.BoundsOf(sourcePath)
                    : default);
            double? sheetX = null, sheetY = null;
            IReadOnlyList<(double X, double Y)>? path = op.Path;
            IReadOnlyList<IReadOnlyList<(double X, double Y)>>? pathSegments = op.PathSegments;
            IReadOnlyList<(double X, double Y)>? finishLoop = op.FinishLoop;
            IReadOnlyList<Geometry.CadSegment>? cadPath = op.CadPath;
            if (op.Op == "drill" && op.X is double x && op.Y is double y)
            {
                var (sx, sy) = Nesting.NestTransform.ToSheet(
                    x, y, bounds, place.OffsetX, place.OffsetY, place.RotationDeg);
                sheetX = RoundSheet(sx);
                sheetY = RoundSheet(sy);
            }
            else if (op.Path is { Count: > 0 } || op.PathSegments is { Count: > 0 })
            {
                (double X, double Y) Map((double X, double Y) p)
                {
                    var (sx, sy) = Nesting.NestTransform.ToSheet(
                        p.X, p.Y, bounds, place.OffsetX, place.OffsetY, place.RotationDeg);
                    return (RoundSheet(sx), RoundSheet(sy));
                }

                if (op.Path is { Count: > 0 })
                    path = op.Path.Select(Map).ToList();
                if (op.PathSegments is { Count: > 0 })
                    pathSegments = op.PathSegments.Select(seg => (IReadOnlyList<(double X, double Y)>)seg.Select(Map).ToList()).ToList();
                if (op.FinishLoop is { Count: > 0 })
                    finishLoop = op.FinishLoop.Select(Map).ToList();
                if (op.CadPath is { Count: > 0 })
                {
                    cadPath = Geometry.CadPath.Map(
                        op.CadPath,
                        p =>
                        {
                            var (sx, sy) = Nesting.NestTransform.ToSheet(
                                p.X, p.Y, bounds, place.OffsetX, place.OffsetY, place.RotationDeg);
                            return new Geometry.Point2(RoundSheet(sx), RoundSheet(sy));
                        });
                }
            }

            return op with
            {
                Placed = true,
                SheetIndex = place.SheetIndex,
                OffsetX = place.OffsetX,
                OffsetY = place.OffsetY,
                RotationDeg = place.RotationDeg,
                SheetX = sheetX,
                SheetY = sheetY,
                Path = path,
                PathSegments = pathSegments,
                FinishLoop = finishLoop,
                CadPath = cadPath,
            };
        }).ToList();
    }

    /// <summary>OSAI-Troy FORMAT X/Y 1.4 — keep a real fourth decimal, not millimetre-rounded then padded.</summary>
    static double RoundSheet(double v) =>
        Math.Round(v, 4, MidpointRounding.AwayFromZero);

}

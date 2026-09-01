using CabinetNC.Compute.Contracts;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using SkiaSharp;

namespace CabinetNC.Desktop;

/// <summary>Vite-parity Skia painter — geom dims/handles · nest grid/selection/ops overlay.</summary>
static class CanvasPainter
{
    public static void PaintGeom(SKCanvas canvas, int w, int h, Panel? panel, string? hoverHint)
    {
        canvas.Clear(new SKColor(0xF4, 0xF4, 0xF4));
        if (panel?.Outline.Points is not { Count: >= 2 })
        {
            DrawCentered(canvas, w, h, "选择左侧板件编辑几何");
            return;
        }

        var view = GeomInteraction.BuildView(panel, w, h);
        DrawGeomGrid(canvas, view);
        DrawOutline(canvas, view, panel, fill: new SKColor(0xE8, 0xF0, 0xFF), stroke: new SKColor(0x00, 0x66, 0xCC), 2f);

        var box = PanelEdit.BBox(panel);
        DrawDimH(canvas, view, box.MinX, box.MaxX, box.MinY, Fmt(box.W), outward: -1);
        DrawDimV(canvas, view, box.MinY, box.MaxY, box.MaxX, Fmt(box.H), outward: 1);

        foreach (var f in panel.Features)
        {
            if (PanelEdit.IsHole(f))
            {
                var (cx, cy) = GeomInteraction.ToScreen(view, f.X, f.Y);
                var r = Math.Max(3f, (float)((f.DiameterMm ?? 8) / 2.0) * view.Scale);
                using var hole = new SKPaint { Color = new SKColor(0x00, 0x00, 0xCC), IsStroke = true, StrokeWidth = 2, IsAntialias = true };
                canvas.DrawCircle(cx, cy, r, hole);
                DrawHandle(canvas, cx, cy, new SKColor(0x00, 0x00, 0xCC));
                DrawText(canvas, $"⌀{Fmt(f.DiameterMm ?? 0)}", cx + r + 4, cy + 4, 11, new SKColor(0x00, 0x00, 0xCC));
            }
            else if (PanelEdit.IsCutout(f) && (f.Path ?? f.Profile) is { Count: >= 3 } cutRing)
            {
                DrawClosedFeature(
                    canvas, view, cutRing,
                    fill: new SKColor(0x18, 0x6A, 0x8A, 0x55),
                    stroke: new SKColor(0x0E, 0x4A, 0x66),
                    dashed: true,
                    label: "通孔");
            }
            else if (PanelEdit.IsPocket(f) && (f.Path ?? f.Profile) is { Count: >= 3 } pocketRing)
            {
                var label = PanelEdit.FeatureDisplayLabel(f);
                if (string.IsNullOrEmpty(label))
                    label = f.DepthMm is > 0 ? $"pocket d{Fmt(f.DepthMm.Value)}" : "pocket";
                DrawClosedFeature(
                    canvas, view, pocketRing,
                    fill: new SKColor(0xC4, 0x7A, 0x00, 0x66),
                    stroke: new SKColor(0x8A, 0x52, 0x00),
                    dashed: false,
                    label: label,
                    holes: f.Holes);
            }
            else if (PanelEdit.IsGroove(f))
            {
                // Prefer CAD opening polygon; else reconstruct strip from centreline+width.
                var outline = GrooveGeometry.DisplayOutline(f);
                if (outline.Count >= 3)
                {
                    using var sk = new SKPath();
                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    for (var i = 0; i < outline.Count; i++)
                    {
                        var p = outline[i];
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                        var (sx, sy) = GeomInteraction.ToScreen(view, p.X, p.Y);
                        if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                    }
                    sk.Close();
                    using var fill = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = new SKColor(0xCC, 0x22, 0x22, 0x66),
                        IsAntialias = true,
                    };
                    using var stroke = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = new SKColor(0xAA, 0x00, 0x00),
                        StrokeWidth = 1.5f,
                        IsAntialias = true,
                    };
                    canvas.DrawPath(sk, fill);
                    canvas.DrawPath(sk, stroke);
                    var wMm = Math.Min(maxX - minX, maxY - minY);
                    if (wMm > 0.5)
                    {
                        var (tx, ty) = GeomInteraction.ToScreen(view, (minX + maxX) * 0.5, (minY + maxY) * 0.5);
                        DrawText(canvas, $"w{Fmt(wMm)}", tx + 6, ty - 4, 10, new SKColor(0x88, 0x00, 0x00));
                    }
                }
                // Edit handles stay on the centreline endpoints when present.
                if (f.Path is { Count: >= 2 } gp)
                {
                    foreach (var pt in gp)
                    {
                        var (sx, sy) = GeomInteraction.ToScreen(view, pt.X, pt.Y);
                        DrawHandle(canvas, sx, sy, new SKColor(0xCC, 0x00, 0x00));
                    }
                }
            }
        }

        if (PanelEdit.IsAxisAlignedRect(panel))
        {
            foreach (var (hx, hy) in new (double, double)[]
                     {
                         (box.MaxX, (box.MinY + box.MaxY) / 2),
                         (box.MinX, (box.MinY + box.MaxY) / 2),
                         ((box.MinX + box.MaxX) / 2, box.MaxY),
                         ((box.MinX + box.MaxX) / 2, box.MinY),
                     })
            {
                var (sx, sy) = GeomInteraction.ToScreen(view, hx, hy);
                DrawSquareHandle(canvas, sx, sy, new SKColor(0x33, 0x33, 0x33));
            }
        }

        DrawGeomGrain(canvas, view, panel, box);

        var grain = GrainAlign.NormalizePart(panel.GrainDirection ?? panel.Orientation?.GrainDirection);
        DrawText(canvas, panel.DisplayTitle, 10, 18, 13, new SKColor(0x22, 0x22, 0x22), bold: true);
        var hint = hoverHint ?? "Geom · 拖孔/槽端点/边手柄";
        DrawText(canvas, grain is null ? hint : $"{hint} · 木纹 {grain}", 10, 36, 11, new SKColor(0x55, 0x55, 0x55));
    }

    public readonly record struct NestHoldingItem(
        string PanelId,
        string Title,
        string Detail,
        SKRect Box,
        IReadOnlyList<(double X, double Y)> Outline,
        string GroupKey,
        string GroupLabel);

    public readonly record struct NestHoldingRegion(
        string GroupKey,
        string Label,
        SKRect Bounds,
        int Count);

    public readonly record struct HoldPreviewPart(
        string PanelId,
        double Ox,
        double Oy,
        double Rot);

    public readonly record struct NestPaintOpts(
        float SheetW,
        float SheetH,
        float Pad,
        float Scale,
        string? SelectedId,
        IReadOnlySet<string> Locked,
        IReadOnlySet<string> Conflicts,
        IReadOnlyList<CutOp>? OpsOverlay,
        bool ShowOps,
        CamFrame? ActiveCamFrame,
        int ActiveSheetIndex = 0,
        IReadOnlyList<(double X, double Y)>? GuillotinePolyline = null,
        string? GuillotineLabel = null,
        IReadOnlyList<(IReadOnlyList<(double X, double Y)> Poly, string? Label)>? GuillotineCuts = null,
        IReadOnlyList<(double X, double Y, string Text)>? GuillotinePieceLabels = null,
        float HoldingBayLeft = 0,
        IReadOnlyList<NestHoldingItem>? HoldingItems = null,
        IReadOnlyList<NestHoldingRegion>? HoldingRegions = null,
        string? HoldingDragId = null,
        IReadOnlyList<HoldPreviewPart>? HoldPreviews = null,
        bool HoldPreviewBlocked = false,
        (double X, double Y)? DragGuideFrom = null,
        (double X, double Y)? DragGuideTo = null,
        (double X, double Y)? MeasureFrom = null,
        (double X, double Y)? MeasureTo = null,
        IReadOnlySet<string>? SelectedIds = null,
        (double X0, double Y0, double X1, double Y1)? SelectionBox = null,
        bool SelectionCrossing = false,
        CamStrategyKind? HighlightStrategy = null,
        TroyPassKind? HighlightPass = null,
        OpsToolpathKind? HighlightToolpath = null,
        IReadOnlyList<ProfileBridge>? Bridges = null,
        IReadOnlyDictionary<string, (double X, double Y)>? LabelOverrides = null,
        bool LitePaint = false,
        IReadOnlyList<ToolStroke>? NcSimStrokes = null,
        double NcSimTimeSec = 0,
        bool FaintParts = false,
        float? OriginX = null,
        float? OriginY = null,
        IReadOnlyDictionary<int, double>? NcSimToolDiaMm = null,
        SheetGrainKind SheetGrain = SheetGrainKind.None);

    public static void PaintNest(
        SKCanvas canvas,
        int w,
        int h,
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacementMsg> placements,
        NestPaintOpts opts)
    {
        canvas.Clear(new SKColor(0xF4, 0xF4, 0xF4));
        var pad = opts.Pad;
        var ox = opts.OriginX ?? pad;
        var oy = opts.OriginY ?? pad;
        var scale = opts.Scale;
        var sw = opts.SheetW;
        var sh = opts.SheetH;
        if (scale <= 0) return;

        float ToSx(double x) => ox + (float)x * scale;
        float ToSy(double y) => oy + (sh - (float)y) * scale;

        // sheet
        using (var fill = new SKPaint { Color = SKColors.White, IsAntialias = true })
        using (var stroke = new SKPaint { Color = SKColors.Black, IsStroke = true, StrokeWidth = 1, IsAntialias = true })
        {
            canvas.DrawRect(ox, oy, sw * scale, sh * scale, fill);
            DrawSheetGrid(canvas, ox, oy, scale, sw, sh);
            canvas.DrawRect(ox, oy, sw * scale, sh * scale, stroke);
        }
        if (opts.SheetGrain != SheetGrainKind.None)
            DrawSheetGrain(canvas, ToSx, ToSy, sw, sh, opts.SheetGrain);

        DrawDimHScreen(canvas, ToSx(0), ToSx(sw), ToSy(sh) - 14, Fmt(sw));
        DrawDimVScreen(canvas, ToSy(0), ToSy(sh), ToSx(0) - 8, Fmt(sh));

        var byId = panels.ToDictionary(p => p.PanelId);
        var sheetIdx = Math.Max(0, opts.ActiveSheetIndex);
        var selectedIds = opts.SelectedIds;
        var drawList = placements.Where(p => p.SheetIndex == sheetIdx).ToList();
        if (selectedIds is { Count: > 0 } || opts.SelectedId is not null)
            drawList = drawList.OrderBy(p =>
                selectedIds?.Contains(p.PanelId) == true || p.PanelId == opts.SelectedId ? 1 : 0).ToList();

        foreach (var place in drawList)
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            var active = opts.SelectedId == place.PanelId
                         || (selectedIds?.Contains(place.PanelId) ?? false);
            var conflict = opts.Conflicts.Contains(place.PanelId);
            var locked = opts.Locked.Contains(place.PanelId);
            var bounds = NestTransform.BoundsOf(panel);

            SKColor fillC = conflict
                ? (active ? new SKColor(0xF5, 0xC6, 0xC6) : new SKColor(0xF0, 0xD0, 0xD0))
                : active ? new SKColor(0xCC, 0xDD, 0xEE) : new SKColor(0xEE, 0xEE, 0xEE);
            SKColor strokeC = conflict ? new SKColor(0xCC, 0x00, 0x00)
                : locked ? new SKColor(0xC0, 0x45, 0x2D)
                : active ? new SKColor(0x00, 0x66, 0xCC) : SKColors.Black;
            var lw = active || conflict || locked ? 2f : 1f;
            if (opts.FaintParts)
            {
                fillC = new SKColor(0xF4, 0xF4, 0xF4);
                strokeC = new SKColor(0xC4, 0xC4, 0xC4);
                lw = 1f;
            }

            using var path = BuildWorldPath(panel, place, ox, oy, scale, sh);
            using var fill = new SKPaint { Color = fillC, IsAntialias = true };
            using var stroke = new SKPaint { Color = strokeC, IsStroke = true, StrokeWidth = lw, IsAntialias = true };
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
            if (!opts.LitePaint && !opts.FaintParts)
                DrawPartGrain(canvas, panel, place, ToSx, ToSy, scale);

            // features on nest
            if (!opts.LitePaint)
            foreach (var f in panel.Features)
            {
                if (PanelEdit.IsHole(f))
                {
                    var (wx, wy) = NestTransform.ToSheet(
                        f.X, f.Y, bounds,
                        place.OffsetX, place.OffsetY, place.RotationDeg);
                    var cx = ToSx(wx);
                    var cy = ToSy(wy);
                    var r = Math.Max(1.5f, (float)((f.DiameterMm ?? 8) / 2) * scale);
                    using var hp = new SKPaint { Color = new SKColor(0x00, 0x00, 0xCC), IsStroke = true, StrokeWidth = 1, IsAntialias = true };
                    canvas.DrawCircle(cx, cy, r, hp);
                    if (active)
                        DrawText(canvas, $"⌀{Fmt(f.DiameterMm ?? 0)}", cx + r + 2, cy, 10, new SKColor(0x00, 0x00, 0xCC));
                }
                else if (PanelEdit.IsCutout(f) && (f.Path ?? f.Profile) is { Count: >= 3 } cutRing)
                {
                    DrawClosedFeatureOnSheet(
                        canvas, cutRing, bounds, place, ToSx, ToSy,
                        fill: new SKColor(0x18, 0x6A, 0x8A, 0x55),
                        stroke: new SKColor(0x0E, 0x4A, 0x66));
                }
                else if (PanelEdit.IsPocket(f) && (f.Path ?? f.Profile) is { Count: >= 3 } pocketRing)
                {
                    DrawClosedFeatureOnSheet(
                        canvas, pocketRing, bounds, place, ToSx, ToSy,
                        fill: new SKColor(0xC4, 0x7A, 0x00, 0x66),
                        stroke: new SKColor(0x8A, 0x52, 0x00),
                        holes: f.Holes);
                }
                else if (PanelEdit.IsGroove(f))
                {
                    var outline = GrooveGeometry.DisplayOutline(f);
                    if (outline.Count >= 3)
                    {
                        DrawClosedFeatureOnSheet(
                            canvas, outline, bounds, place, ToSx, ToSy,
                            fill: new SKColor(0xCC, 0x22, 0x22, 0x66),
                            stroke: new SKColor(0xAA, 0x00, 0x00));
                    }
                }
            }

            // Short shop label (not raw PanelId / @layflat…); fit inside part width.
            var aabb = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            var lx = ToSx(aabb.MinX) + 2;
            var ly = ToSy(aabb.MaxY) + 12;
            var partW = Math.Max(0f, ToSx(aabb.MaxX) - ToSx(aabb.MinX) - 4f);
            var partH = Math.Max(0f, ToSy(aabb.MinY) - ToSy(aabb.MaxY));
            var fontSize = active ? 12f : 10f;
            // Tiny strips: only label when selected to avoid overlapping neighbors.
            if (active || (partW >= 28 && partH >= 14))
            {
                var shortName = string.IsNullOrWhiteSpace(panel.DisplayPartName)
                    ? panel.DisplayTitle
                    : panel.DisplayPartName;
                var label = locked ? $"[锁] {shortName}" : shortName;
                label = EllipsizeToWidth(label, Math.Max(12f, partW), fontSize, bold: active);
                DrawText(canvas, label, lx, ly, fontSize,
                    active ? new SKColor(0x00, 0x66, 0xCC) : new SKColor(0x22, 0x22, 0x22),
                    bold: active);
            }

            if (active && !opts.LitePaint)
            {
                DrawDimHScreen(canvas, ToSx(aabb.MinX), ToSx(aabb.MaxX), ToSy(aabb.MinY) + 12, Fmt(aabb.MaxX - aabb.MinX));
                DrawDimVScreen(canvas, ToSy(aabb.MinY), ToSy(aabb.MaxY), ToSx(aabb.MaxX) + 10, Fmt(aabb.MaxY - aabb.MinY));
            }

            if (opts.LitePaint) continue;

            (double X, double Y)? ov = opts.LabelOverrides is { } map
                && map.TryGetValue(panel.PanelId, out var o)
                ? o
                : null;
            var anchor = LabelAnchorFinder.Find(panel, place.RotationDeg, ov);
            var (lxSheet, lySheet) = NestTransform.ToSheet(
                anchor.LocalX, anchor.LocalY, bounds,
                place.OffsetX, place.OffsetY, place.RotationDeg);
            DrawLabelMark(canvas, ToSx, ToSy, scale, lxSheet, lySheet, anchor);
        }

        if (opts.HoldPreviews is { Count: > 0 } previews)
        {
            var showDims = previews.Count == 1;
            foreach (var preview in previews)
            {
                if (!byId.TryGetValue(preview.PanelId, out var previewPanel)) continue;
                DrawHoldPreview(
                    canvas, previewPanel, ToSx, ToSy, pad, scale, sh,
                    preview.Ox, preview.Oy, preview.Rot,
                    opts.HoldPreviewBlocked, showDims);
            }
        }

        if (drawList.Count == 0)
            DrawText(canvas, $"本张大板无摆位（第 {sheetIdx + 1} 张）", ox + 8, oy + 20, 12, new SKColor(0x66, 0x66, 0x66));

        var gCuts = opts.GuillotineCuts;
        if (gCuts is null && opts.GuillotinePolyline is { Count: >= 2 } gpoly)
            gCuts = [(gpoly, opts.GuillotineLabel)];
        if (gCuts is { Count: > 0 })
        {
            using var stroke = new SKPaint
            {
                Color = new SKColor(0xC4, 0x5A, 0x00),
                IsStroke = true,
                StrokeWidth = 2.5f,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash([10f, 6f], 0),
            };
            using var mark = new SKPaint { Color = new SKColor(0xC4, 0x5A, 0x00), IsAntialias = true };
            foreach (var (poly, label) in gCuts)
            {
                if (poly.Count < 2) continue;
                using var path = new SKPath();
                for (var i = 0; i < poly.Count; i++)
                {
                    var sx = ToSx(poly[i].X);
                    var sy = ToSy(poly[i].Y);
                    if (i == 0) path.MoveTo(sx, sy);
                    else path.LineTo(sx, sy);
                }
                canvas.DrawPath(path, stroke);
                foreach (var p in poly)
                    canvas.DrawCircle(ToSx(p.X), ToSy(p.Y), 3.5f, mark);
                if (!string.IsNullOrWhiteSpace(label) && opts.GuillotinePieceLabels is not { Count: > 0 })
                {
                    var mid = poly[poly.Count / 2];
                    DrawText(canvas, label!, ToSx(mid.X) + 6, ToSy(mid.Y) - 6, 11,
                        new SKColor(0x8A, 0x3E, 0x00), bold: true);
                }
            }
        }

        if (opts.GuillotinePieceLabels is { Count: > 0 } pieceLabels)
        {
            foreach (var (x, y, text) in pieceLabels)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                DrawText(canvas, text, ToSx(x), ToSy(y), 11,
                    new SKColor(0x8A, 0x3E, 0x00), bold: true);
            }
        }

        if (opts.DragGuideFrom is { } from && opts.DragGuideTo is { } to)
            DrawDragGuide(canvas, ToSx, ToSy, from, to);

        if (opts.MeasureFrom is { } mFrom && opts.MeasureTo is { } mTo)
            DrawMeasureGuide(canvas, ToSx, ToSy, mFrom, mTo);

        if (opts.SelectionBox is { } box)
            DrawSelectionBox(canvas, ToSx, ToSy, box, opts.SelectionCrossing);

        if (opts.ShowOps && opts.OpsOverlay is { Count: > 0 } ops)
            PaintOpsOverlay(canvas, ops, ToSx, ToSy, scale, opts.ActiveCamFrame, opts.ActiveSheetIndex,
                opts.HighlightStrategy);

        if (opts.ShowOps && opts.Bridges is { Count: > 0 } bridges)
            PaintBridges(canvas, bridges, ToSx, ToSy, scale, opts.ActiveSheetIndex);

        if (opts.NcSimStrokes is { Count: > 0 } sim)
            PaintNcSim(canvas, sim, opts.NcSimTimeSec, ToSx, ToSy, scale, opts.NcSimToolDiaMm);

        if (opts.HoldingBayLeft > 0 && opts.HoldingBayLeft < w - 4)
            PaintHoldingBay(
                canvas, w, h, opts.HoldingBayLeft,
                opts.HoldingItems, opts.HoldingRegions,
                opts.SelectedId, opts.HoldingDragId, opts.SelectedIds);
    }

    /// <summary>Wider bay so square multi-column cards fit beside the sheet.</summary>
    public const float NestHoldingBayWidth = 340f;

    /// <summary>
    /// Group by material/thickness into labeled sub-regions; cards are square multi-column
    /// within each region (same geometry used for hit-testing).
    /// </summary>
    public static (List<NestHoldingItem> Items, List<NestHoldingRegion> Regions) LayoutHoldingItems(
        IReadOnlyList<(string PanelId, string Title, string Detail, IReadOnlyList<(double X, double Y)> Outline, string GroupKey, string GroupLabel)> items,
        float bayLeft,
        float bayWidth,
        float canvasH,
        float topPad = 56f,
        float bottomPad = 36f)
    {
        var list = new List<NestHoldingItem>();
        var regions = new List<NestHoldingRegion>();
        const float gap = 8f;
        const float sidePad = 10f;
        const float cell = 96f;
        const float headerH = 22f;
        const float regionGap = 12f;
        var innerW = Math.Max(cell, bayWidth - sidePad * 2);
        var cols = Math.Max(1, (int)Math.Floor((innerW + gap) / (cell + gap)));
        var maxY = canvasH - bottomPad;
        var yCursor = topPad;

        foreach (var group in items
                     .GroupBy(i => i.GroupKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.First().GroupLabel, StringComparer.OrdinalIgnoreCase))
        {
            var groupItems = group.ToList();
            var label = groupItems[0].GroupLabel;
            var rows = (int)Math.Ceiling(groupItems.Count / (double)cols);
            var contentH = rows * cell + Math.Max(0, rows - 1) * gap;
            var regionTop = yCursor;
            var regionBottom = regionTop + headerH + 4 + contentH + 8;
            if (regionTop + headerH > maxY) break;

            var cardsTop = regionTop + headerH + 4;
            var before = list.Count;
            for (var i = 0; i < groupItems.Count; i++)
            {
                var col = i % cols;
                var row = i / cols;
                var x = bayLeft + sidePad + col * (cell + gap);
                var y = cardsTop + row * (cell + gap);
                if (y + cell > maxY) break;
                var it = groupItems[i];
                list.Add(new NestHoldingItem(
                    it.PanelId, it.Title, it.Detail,
                    new SKRect(x, y, x + cell, y + cell),
                    it.Outline, it.GroupKey, it.GroupLabel));
            }

            var placedInGroup = list.Count - before;
            if (placedInGroup == 0) break;

            regionBottom = Math.Min(maxY, cardsTop + contentH + 8);
            regions.Add(new NestHoldingRegion(
                group.Key,
                $"{label} · {placedInGroup}",
                new SKRect(bayLeft + 6, regionTop, bayLeft + bayWidth - 6, regionBottom),
                placedInGroup));
            yCursor = regionBottom + regionGap;
            if (yCursor >= maxY) break;
        }

        return (list, regions);
    }

    static void PaintHoldingBay(
        SKCanvas canvas,
        int w,
        int h,
        float bayLeft,
        IReadOnlyList<NestHoldingItem>? items,
        IReadOnlyList<NestHoldingRegion>? regions,
        string? selectedId,
        string? dragId,
        IReadOnlySet<string>? selectedIds = null)
    {
        using (var fill = new SKPaint { Color = new SKColor(0xEE, 0xF1, 0xF4), IsAntialias = true })
            canvas.DrawRect(bayLeft, 0, w - bayLeft, h, fill);
        using (var edge = new SKPaint { Color = new SKColor(0xCC, 0xD0, 0xD4), IsStroke = true, StrokeWidth = 1 })
            canvas.DrawLine(bayLeft, 0, bayLeft, h, edge);

        DrawText(canvas, "板件待用区", bayLeft + 10, 28, 13, new SKColor(0x33, 0x33, 0x33), bold: true);
        DrawText(canvas, "按材料分区 · Ctrl 多选 · 拖回同材料大板", bayLeft + 10, 44, 10, new SKColor(0x77, 0x77, 0x77));

        if (items is null || items.Count == 0)
        {
            DrawText(canvas, "（空）", bayLeft + 10, 72, 11, new SKColor(0x99, 0x99, 0x99));
            return;
        }

        if (regions is not null)
        {
            foreach (var region in regions)
            {
                using var band = new SKPaint
                {
                    Color = new SKColor(0xE3, 0xE8, 0xED),
                    IsAntialias = true,
                };
                canvas.DrawRoundRect(region.Bounds, 6, 6, band);
                using var bandStroke = new SKPaint
                {
                    Color = new SKColor(0xC0, 0xC6, 0xCC),
                    IsStroke = true,
                    StrokeWidth = 1,
                    IsAntialias = true,
                };
                canvas.DrawRoundRect(region.Bounds, 6, 6, bandStroke);

                var label = region.Label.Length > 36 ? region.Label[..35] + "…" : region.Label;
                DrawText(canvas, label, region.Bounds.Left + 8, region.Bounds.Top + 16, 11,
                    new SKColor(0x22, 0x44, 0x66), bold: true);
            }
        }

        foreach (var it in items)
        {
            var active = it.PanelId == selectedId
                         || it.PanelId == dragId
                         || (selectedIds?.Contains(it.PanelId) ?? false);
            using var fill = new SKPaint
            {
                Color = active ? new SKColor(0xCC, 0xDD, 0xEE) : new SKColor(0xFF, 0xFF, 0xFF),
                IsAntialias = true,
            };
            using var stroke = new SKPaint
            {
                Color = active ? new SKColor(0x00, 0x66, 0xCC) : new SKColor(0x88, 0x88, 0x88),
                IsStroke = true,
                StrokeWidth = active ? 2f : 1f,
                IsAntialias = true,
            };
            canvas.DrawRoundRect(it.Box, 6, 6, fill);
            canvas.DrawRoundRect(it.Box, 6, 6, stroke);

            var shapeRect = new SKRect(
                it.Box.Left + 8,
                it.Box.Top + 8,
                it.Box.Right - 8,
                it.Box.Bottom - 28);
            DrawHoldingShape(canvas, shapeRect, it.Outline, active);

            var title = it.Title.Length > 14 ? it.Title[..13] + "…" : it.Title;
            DrawText(canvas, title, it.Box.Left + 6, it.Box.Bottom - 10, 10,
                active ? new SKColor(0x00, 0x66, 0xCC) : new SKColor(0x33, 0x33, 0x33),
                bold: active);
        }
    }

    static void DrawHoldingShape(
        SKCanvas canvas,
        SKRect fit,
        IReadOnlyList<(double X, double Y)> outline,
        bool active)
    {
        if (outline.Count < 2)
        {
            using var ph = new SKPaint
            {
                Color = new SKColor(0xDD, 0xDD, 0xDD),
                IsStroke = true,
                StrokeWidth = 1,
            };
            canvas.DrawRect(fit, ph);
            return;
        }

        var minX = outline.Min(p => p.X);
        var minY = outline.Min(p => p.Y);
        var maxX = outline.Max(p => p.X);
        var maxY = outline.Max(p => p.Y);
        var bw = Math.Max(1e-6, maxX - minX);
        var bh = Math.Max(1e-6, maxY - minY);
        var s = Math.Min(fit.Width / (float)bw, fit.Height / (float)bh) * 0.9f;
        var ox = fit.MidX - (float)(bw * s) * 0.5f;
        var oy = fit.MidY + (float)(bh * s) * 0.5f; // Y-up local → screen down

        using var path = new SKPath();
        for (var i = 0; i < outline.Count; i++)
        {
            var sx = ox + (float)(outline[i].X - minX) * s;
            var sy = oy - (float)(outline[i].Y - minY) * s;
            if (i == 0) path.MoveTo(sx, sy);
            else path.LineTo(sx, sy);
        }
        path.Close();

        using var fill = new SKPaint
        {
            Color = active ? new SKColor(0xB8, 0xD4, 0xEE) : new SKColor(0xE8, 0xE8, 0xE8),
            IsAntialias = true,
        };
        using var stroke = new SKPaint
        {
            Color = active ? new SKColor(0x00, 0x66, 0xCC) : new SKColor(0x44, 0x44, 0x44),
            IsStroke = true,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
    }

    static void PaintOpsOverlay(
        SKCanvas canvas,
        IReadOnlyList<CutOp> ops,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float scale,
        CamFrame? activeFrame,
        int sheetIndex,
        CamStrategyKind? highlight)
    {
        foreach (var op in ops.Where(o => o.Placed && o.SheetIndex == sheetIndex))
        {
            var focused = highlight is null || CamStrategy.Classify(op) == highlight;
            var active = activeFrame?.Op == op;
            var alpha = (byte)(focused ? 255 : 55);
            SKColor Color(byte r, byte g, byte b) =>
                active ? new SKColor(0xFF, 0xC1, 0x07) : new SKColor(r, g, b, alpha);

            if (op.Op == "remnant" && op.Path is { Count: >= 2 } remnantPath)
            {
                using var sk = new SKPath();
                for (var i = 0; i < remnantPath.Count; i++)
                {
                    var sx = toSx(remnantPath[i].X);
                    var sy = toSy(remnantPath[i].Y);
                    if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                }
                using var paint = new SKPaint
                {
                    Color = Color(0xC4, 0x5A, 0x00),
                    IsStroke = true,
                    StrokeWidth = active ? 4f : focused ? 2.8f : 1.6f,
                    IsAntialias = true,
                };
                canvas.DrawPath(sk, paint);
            }
            else if (op.Op == "contour" && op.Path is { Count: >= 2 } path)
            {
                using var sk = new SKPath();
                for (var i = 0; i < path.Count; i++)
                {
                    var sx = toSx(path[i].X);
                    var sy = toSy(path[i].Y);
                    if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                }
                sk.Close();
                using (var first = new SKPaint
                {
                    Color = Color(0x22, 0x77, 0xCC),
                    IsStroke = true,
                    StrokeWidth = active ? 4f : focused ? 2.4f : 1.4f,
                    IsAntialias = true,
                })
                    canvas.DrawPath(sk, first);
                using (var last = new SKPaint
                {
                    Color = Color(0xC0, 0x39, 0x2B),
                    IsStroke = true,
                    StrokeWidth = active ? 3f : focused ? 1.8f : 1.2f,
                    IsAntialias = true,
                    PathEffect = SKPathEffect.CreateDash([7, 5], 0),
                })
                    canvas.DrawPath(sk, last);
            }
            else if (op.Op == "drill" && op.SheetX is double sx0 && op.SheetY is double sy0)
            {
                var cx = toSx(sx0);
                var cy = toSy(sy0);
                var r = Math.Max(2f, (float)((op.DiameterMm ?? 8) / 2) * scale);
                using var paint = new SKPaint
                {
                    Color = Color(0x00, 0x44, 0xFF),
                    IsStroke = true,
                    StrokeWidth = active ? 4 : focused ? 2 : 1,
                    IsAntialias = true,
                };
                canvas.DrawCircle(cx, cy, r, paint);
                canvas.DrawLine(cx - r, cy, cx + r, cy, paint);
                canvas.DrawLine(cx, cy - r, cx, cy + r, paint);
            }
            else if (op.Op == "pocket")
            {
                using var paint = new SKPaint
                {
                    Color = Color(0x2E, 0xA8, 0x4A),
                    IsStroke = true,
                    StrokeWidth = active ? 3f : focused ? 1.6f : 1f,
                    IsAntialias = true,
                };
                var segments = op.PathSegments;
                if (segments is { Count: > 0 })
                {
                    foreach (var seg in segments)
                    {
                        if (seg.Count < 2) continue;
                        using var sk = new SKPath();
                        for (var i = 0; i < seg.Count; i++)
                        {
                            var sx = toSx(seg[i].X);
                            var sy = toSy(seg[i].Y);
                            if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                        }
                        canvas.DrawPath(sk, paint);
                    }
                }
                else if (op.Path is { Count: >= 2 } ppath)
                {
                    using var sk = new SKPath();
                    for (var i = 0; i < ppath.Count; i++)
                    {
                        var sx = toSx(ppath[i].X);
                        var sy = toSy(ppath[i].Y);
                        if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                    }
                    canvas.DrawPath(sk, paint);
                }
                if (op.FinishLoop is { Count: >= 3 } loop)
                {
                    using var sk = new SKPath();
                    for (var i = 0; i < loop.Count; i++)
                    {
                        var sx = toSx(loop[i].X);
                        var sy = toSy(loop[i].Y);
                        if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                    }
                    sk.Close();
                    using var finish = new SKPaint
                    {
                        Color = Color(0x1E, 0x6E, 0x32),
                        IsStroke = true,
                        StrokeWidth = focused ? 2f : 1f,
                        IsAntialias = true,
                        PathEffect = SKPathEffect.CreateDash([4, 3], 0),
                    };
                    canvas.DrawPath(sk, finish);
                }
            }
            else if (op.Op == "groove")
            {
                var grooveColor = op.IsTongue
                    ? Color(0xE6, 0x7E, 0x22)
                    : Color(0x2E, 0xA8, 0x4A);
                if (op.PathSegments is { Count: > 0 } || op.FinishLoop is { Count: >= 3 })
                {
                    using var paint = new SKPaint
                    {
                        Color = grooveColor,
                        IsStroke = true,
                        StrokeWidth = active ? 3f : focused ? 1.8f : 1f,
                        IsAntialias = true,
                    };
                    if (op.PathSegments is { Count: > 0 })
                    {
                        foreach (var seg in op.PathSegments)
                        {
                            if (seg.Count < 2) continue;
                            using var sk = new SKPath();
                            for (var i = 0; i < seg.Count; i++)
                            {
                                var sx = toSx(seg[i].X);
                                var sy = toSy(seg[i].Y);
                                if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                            }
                            canvas.DrawPath(sk, paint);
                        }
                    }
                    if (op.FinishLoop is { Count: >= 3 } loop)
                    {
                        using var sk = new SKPath();
                        for (var i = 0; i < loop.Count; i++)
                        {
                            var sx = toSx(loop[i].X);
                            var sy = toSy(loop[i].Y);
                            if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                        }
                        sk.Close();
                        canvas.DrawPath(sk, paint);
                    }
                }
                else if (op.Path is { Count: >= 2 } gpath)
                {
                    var center = gpath.Select(p => new Point2(p.X, p.Y)).ToList();
                    var outline = GrooveGeometry.OutlineFromCenterline(
                        center,
                        op.WidthMm is > 1e-9 ? op.WidthMm.Value : 0);
                    using var sk = new SKPath();
                    if (outline.Count >= 3)
                    {
                        for (var i = 0; i < outline.Count; i++)
                        {
                            var sx = toSx(outline[i].X);
                            var sy = toSy(outline[i].Y);
                            if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                        }
                        sk.Close();
                    }
                    else
                    {
                        for (var i = 0; i < gpath.Count; i++)
                        {
                            var sx = toSx(gpath[i].X);
                            var sy = toSy(gpath[i].Y);
                            if (i == 0) sk.MoveTo(sx, sy); else sk.LineTo(sx, sy);
                        }
                    }
                    using var paint = new SKPaint
                    {
                        Color = grooveColor,
                        IsStroke = true,
                        StrokeWidth = active ? 3f : focused ? 1.8f : 1f,
                        IsAntialias = true,
                    };
                    canvas.DrawPath(sk, paint);
                }
            }
        }

        if (activeFrame is not null)
        {
            var cx = toSx(activeFrame.X);
            var cy = toSy(activeFrame.Y);
            using var marker = new SKPaint
            {
                Color = new SKColor(0xFF, 0xC1, 0x07),
                IsAntialias = true,
            };
            using var ring = new SKPaint
            {
                Color = SKColors.Black,
                IsStroke = true,
                StrokeWidth = 1.5f,
                IsAntialias = true,
            };
            canvas.DrawCircle(cx, cy, 5, marker);
            canvas.DrawCircle(cx, cy, 5, ring);
        }
    }

    static void PaintBridges(
        SKCanvas canvas,
        IReadOnlyList<ProfileBridge> bridges,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float scale,
        int sheetIndex)
    {
        foreach (var b in bridges.Where(x => x.SheetIndex == sheetIndex))
        {
            var sx = toSx(b.X);
            var sy = toSy(b.Y);
            var r = Math.Max(5.5f, 2.2f * Math.Max(1f, scale));
            using var fill = new SKPaint
            {
                Color = b.PairId is null
                    ? new SKColor(0xF1, 0xC4, 0x0F)
                    : new SKColor(0xE6, 0x7E, 0x22),
                IsAntialias = true,
            };
            using var stroke = new SKPaint
            {
                Color = new SKColor(0x4A, 0x2C, 0x0A),
                IsStroke = true,
                StrokeWidth = 1.4f,
                IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round,
            };
            using var diamond = new SKPath();
            diamond.MoveTo(sx, sy - r);
            diamond.LineTo(sx + r, sy);
            diamond.LineTo(sx, sy + r);
            diamond.LineTo(sx - r, sy);
            diamond.Close();
            canvas.DrawPath(diamond, fill);
            canvas.DrawPath(diamond, stroke);
        }
    }

    static void PaintNcSim(
        SKCanvas canvas,
        IReadOnlyList<ToolStroke> strokes,
        double timeSec,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float scale,
        IReadOnlyDictionary<int, double>? shopDia = null)
    {
        var pose = NcCutSim.At(strokes, timeSec);

        for (var i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            if (i > pose.StrokeIndex)
            {
                DrawNcStroke(canvas, s, 0, 1, false, toSx, toSy, scale, shopDia);
                continue;
            }

            if (i == pose.StrokeIndex && pose.Along < 1 - 1e-6)
            {
                if (pose.Along > 1e-6)
                    DrawNcStroke(canvas, s, 0, pose.Along, true, toSx, toSy, scale, shopDia);
                DrawNcStroke(canvas, s, pose.Along, 1, false, toSx, toSy, scale, shopDia);
                continue;
            }

            DrawNcStroke(canvas, s, 0, 1, true, toSx, toSy, scale, shopDia);
        }

        if (pose.StrokeIndex < 0) return;
        var r = Math.Max(3.2f, (float)(NcCutSim.ToolDiameterMm(pose.ToolNum, shopDia) * 0.5 * scale));
        var cx = toSx(pose.X);
        var cy = toSy(pose.Y);
        var fillC = pose.Rapid
            ? new SKColor(0x88, 0x88, 0x88, 0xB0)
            : pose.Z >= 0.25
                ? new SKColor(0xE6, 0x7E, 0x22, 0xE0)
                : new SKColor(0x1A, 0x6B, 0xB5, 0xE0);
        using var fill = new SKPaint { Color = fillC, IsAntialias = true };
        using var ring = new SKPaint
        {
            Color = new SKColor(0x22, 0x22, 0x22),
            IsStroke = true,
            StrokeWidth = 1.4f,
            IsAntialias = true,
        };
        canvas.DrawCircle(cx, cy, r, fill);
        canvas.DrawCircle(cx, cy, r, ring);
    }

    static void DrawNcStroke(
        SKCanvas canvas,
        ToolStroke s,
        double a0,
        double a1,
        bool done,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float scale,
        IReadOnlyDictionary<int, double>? shopDia = null)
    {
        a0 = Math.Clamp(a0, 0, 1);
        a1 = Math.Clamp(a1, 0, 1);
        if (a1 - a0 < 1e-6) return;

        var kind = NcCutSim.KindOf(s);
        var rapid = kind == NcCutSim.StrokeKind.Rapid;
        var color = kind switch
        {
            NcCutSim.StrokeKind.Leave => done
                ? new SKColor(0xE6, 0x7E, 0x22, 0xB8)
                : new SKColor(0xE6, 0x7E, 0x22, 0x40),
            NcCutSim.StrokeKind.Through => done
                ? new SKColor(0x0B, 0x4F, 0x8C, 0xB8)
                : new SKColor(0x1A, 0x6B, 0xB5, 0x40),
            _ => done
                ? new SKColor(0x88, 0x88, 0x88)
                : new SKColor(0xBB, 0xBB, 0xBB, 0x90),
        };
        var dia = NcCutSim.ToolDiameterMm(s.ToolNum, shopDia);
        var sw = NcCutSim.CutStrokeWidthPx(s.ToolNum, scale, rapid, shopDia);

        if (!s.Arc && s.XyLen < 0.2)
        {
            if (!done) return;
            var tip = NcCutSim.PointAlong(s, a1);
            using var tick = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawCircle(toSx(tip.X), toSy(tip.Y), Math.Max(1.8f, (float)(dia * 0.5 * scale)), tick);
            return;
        }

        using var dash = rapid ? SKPathEffect.CreateDash([6f, 4f], 0) : null;
        using var paint = new SKPaint
        {
            Color = color,
            IsStroke = true,
            StrokeWidth = sw,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            PathEffect = dash,
        };
        if (s.Arc && s.R is double rad && rad > 1e-6
            && OsaiTroyParser.TryArcSweep(s.X0, s.Y0, s.X1, s.Y1, rad, s.Cw, out var cx, out var cy, out _, out _))
        {
            var p0 = NcCutSim.PointAlong(s, a0);
            var p1 = NcCutSim.PointAlong(s, a1);
            var mid = NcCutSim.PointAlong(s, (a0 + a1) * 0.5);
            var scx = toSx(cx);
            var scy = toSy(cy);
            var rx = Math.Abs(toSx(cx + rad) - scx);
            var ry = Math.Abs(toSy(cy + rad) - scy);
            if (rx > 0.5f && ry > 0.5f)
            {
                float Ang(double x, double y) =>
                    (float)(Math.Atan2(toSy(y) - scy, toSx(x) - scx) * 180 / Math.PI);
                static float CwDelta(float from, float to)
                {
                    var d = to - from;
                    while (d < 0) d += 360;
                    while (d >= 360) d -= 360;
                    return d;
                }
                var sa = Ang(p0.X, p0.Y);
                var sweepCw = CwDelta(sa, Ang(p1.X, p1.Y));
                var midCw = CwDelta(sa, Ang(mid.X, mid.Y));
                var sweep = midCw <= sweepCw + 1 ? sweepCw : sweepCw - 360;
                using var path = new SKPath();
                path.MoveTo(toSx(p0.X), toSy(p0.Y));
                path.ArcTo(new SKRect(scx - rx, scy - ry, scx + rx, scy + ry), sa, sweep, false);
                canvas.DrawPath(path, paint);
                return;
            }
        }
        var a = NcCutSim.PointAlong(s, a0);
        var b = NcCutSim.PointAlong(s, a1);
        canvas.DrawLine(toSx(a.X), toSy(a.Y), toSx(b.X), toSy(b.Y), paint);
    }

    static void DrawHoldPreview(
        SKCanvas canvas,
        Panel panel,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float pad,
        float scale,
        float sheetH,
        double ox,
        double oy,
        double rotDeg,
        bool blocked,
        bool showDims)
    {
        var place = new NestPlacementMsg
        {
            OffsetX = ox,
            OffsetY = oy,
            RotationDeg = rotDeg,
        };
        using var path = BuildWorldPath(panel, place, pad, pad, scale, sheetH);
        var fillC = blocked
            ? new SKColor(0xCC, 0x33, 0x33, 0x55)
            : new SKColor(0x00, 0x66, 0xCC, 0x55);
        var strokeC = blocked
            ? new SKColor(0xCC, 0x22, 0x22)
            : new SKColor(0x00, 0x66, 0xCC);
        using var fill = new SKPaint { Color = fillC, IsAntialias = true };
        using var stroke = new SKPaint
        {
            Color = strokeC,
            IsStroke = true,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([7f, 5f], 0),
        };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);

        if (showDims)
        {
            var aabb = NestDrag.Aabb(panel, ox, oy, rotDeg);
            DrawDimHScreen(canvas, toSx(aabb.MinX), toSx(aabb.MaxX), toSy(aabb.MinY) + 12, Fmt(aabb.MaxX - aabb.MinX));
            DrawDimVScreen(canvas, toSy(aabb.MinY), toSy(aabb.MaxY), toSx(aabb.MaxX) + 10, Fmt(aabb.MaxY - aabb.MinY));
        }

        var bounds = NestTransform.BoundsOf(panel);
        foreach (var f in panel.Features)
        {
            if (!PanelEdit.IsHole(f)) continue;
            var (wx, wy) = NestTransform.ToSheet(
                f.X, f.Y, bounds, ox, oy, rotDeg);
            var r = Math.Max(1.5f, (float)((f.DiameterMm ?? 8) / 2) * scale);
            using var hp = new SKPaint
            {
                Color = strokeC,
                IsStroke = true,
                StrokeWidth = 1.2f,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash([4f, 3f], 0),
            };
            canvas.DrawCircle(toSx(wx), toSy(wy), r, hp);
        }
    }

    public static SKPath BuildWorldPath(
        Panel panel, NestPlacementMsg place, float padX, float padY, float scale, float sheetH)
    {
        var path = new SKPath();
        var first = true;
        var bounds = NestTransform.BoundsOf(panel);
        foreach (var pt in panel.Outline.Points)
        {
            var (wx, wy) = NestTransform.ToSheet(
                pt.X, pt.Y, bounds,
                place.OffsetX, place.OffsetY, place.RotationDeg);
            var x = padX + (float)wx * scale;
            var y = padY + (sheetH - (float)wy) * scale;
            if (first) { path.MoveTo(x, y); first = false; }
            else path.LineTo(x, y);
        }
        path.Close();
        return path;
    }

    static void DrawGeomGrid(SKCanvas canvas, GeomInteraction.View view)
    {
        using var paint = new SKPaint { Color = new SKColor(0xE0, 0xE0, 0xE0), IsStroke = true, StrokeWidth = 1 };
        using var major = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), IsStroke = true, StrokeWidth = 1 };
        const double step = 50;
        var x0 = Math.Floor(view.OriginX / step) * step;
        var y0 = Math.Floor(view.OriginY / step) * step;
        for (var x = x0; x <= view.OriginX + view.WorldW + step; x += step)
        {
            var (sx0, sy0) = GeomInteraction.ToScreen(view, x, view.OriginY);
            var (_, sy1) = GeomInteraction.ToScreen(view, x, view.OriginY + view.WorldH);
            canvas.DrawLine(sx0, sy0, sx0, sy1, Math.Abs(x % (step * 2)) < 0.01 ? major : paint);
        }
        for (var y = y0; y <= view.OriginY + view.WorldH + step; y += step)
        {
            var (sx0, sy0) = GeomInteraction.ToScreen(view, view.OriginX, y);
            var (sx1, _) = GeomInteraction.ToScreen(view, view.OriginX + view.WorldW, y);
            canvas.DrawLine(sx0, sy0, sx1, sy0, Math.Abs(y % (step * 2)) < 0.01 ? major : paint);
        }
    }

    static void DrawSheetGrid(SKCanvas canvas, float ox, float oy, float scale, float sw, float sh)
    {
        using var paint = new SKPaint { Color = new SKColor(0xE8, 0xE8, 0xE8), IsStroke = true, StrokeWidth = 1 };
        const float step = 50;
        for (float x = 0; x <= sw; x += step)
            canvas.DrawLine(ox + x * scale, oy, ox + x * scale, oy + sh * scale, paint);
        for (float y = 0; y <= sh; y += step)
            canvas.DrawLine(ox, oy + (sh - y) * scale, ox + sw * scale, oy + (sh - y) * scale, paint);
    }

    static void DrawOutline(SKCanvas canvas, GeomInteraction.View view, Panel panel, SKColor fill, SKColor stroke, float lw)
    {
        using var path = new SKPath();
        var pts = panel.Outline.Points;
        for (var i = 0; i < pts.Count; i++)
        {
            var (sx, sy) = GeomInteraction.ToScreen(view, pts[i].X, pts[i].Y);
            if (i == 0) path.MoveTo(sx, sy); else path.LineTo(sx, sy);
        }
        path.Close();
        using var f = new SKPaint { Color = fill, IsAntialias = true };
        using var s = new SKPaint { Color = stroke, IsStroke = true, StrokeWidth = lw, IsAntialias = true };
        canvas.DrawPath(path, f);
        canvas.DrawPath(path, s);
    }

    static void DrawClosedFeature(
        SKCanvas canvas,
        GeomInteraction.View view,
        IReadOnlyList<Point2> ring,
        SKColor fill,
        SKColor stroke,
        bool dashed,
        string label,
        IReadOnlyList<IReadOnlyList<Point2>>? holes = null)
    {
        using var sk = new SKPath { FillType = SKPathFillType.EvenOdd };
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        AppendRing(sk, ring, p => GeomInteraction.ToScreen(view, p.X, p.Y), ref minX, ref maxX, ref minY, ref maxY);
        foreach (var hole in holes ?? [])
        {
            if (hole.Count < 3) continue;
            AppendRing(sk, hole, p => GeomInteraction.ToScreen(view, p.X, p.Y), ref minX, ref maxX, ref minY, ref maxY);
        }
        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = fill,
            IsAntialias = true,
        };
        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = stroke,
            StrokeWidth = 1.6f,
            IsAntialias = true,
            PathEffect = dashed ? SKPathEffect.CreateDash([5, 3], 0) : null,
        };
        canvas.DrawPath(sk, fillPaint);
        canvas.DrawPath(sk, strokePaint);
        if (!string.IsNullOrWhiteSpace(label))
        {
            var (tx, ty) = GeomInteraction.ToScreen(view, (minX + maxX) * 0.5, (minY + maxY) * 0.5);
            DrawText(canvas, label, tx + 4, ty - 2, 10, stroke);
        }
    }

    static void DrawLabelMark(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float scale,
        double sheetX,
        double sheetY,
        LabelAnchor anchor)
    {
        var left = toSx(sheetX - anchor.WidthMm * 0.5);
        var right = toSx(sheetX + anchor.WidthMm * 0.5);
        var top = toSy(sheetY + anchor.HeightMm * 0.5);
        var bottom = toSy(sheetY - anchor.HeightMm * 0.5);
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);
        var rect = new SKRect(left, top, right, bottom);
        var fillC = new SKColor(0xFF, 0xF3, 0xC4, 0xE6);
        var strokeC = new SKColor(0x8A, 0x6A, 0x00);
        var radius = Math.Max(1.5f, 2.5f * Math.Max(0.4f, scale));
        using (var fill = new SKPaint { Color = fillC, IsAntialias = true })
            canvas.DrawRoundRect(rect, radius, radius, fill);
        using (var stroke = new SKPaint
        {
            Color = strokeC,
            IsStroke = true,
            StrokeWidth = 1.2f,
            IsAntialias = true,
        })
            canvas.DrawRoundRect(rect, radius, radius, stroke);

        var fold = Math.Min(rect.Width, rect.Height) * 0.28f;
        if (fold >= 3)
        {
            using var foldPath = new SKPath();
            foldPath.MoveTo(rect.Right - fold, rect.Top);
            foldPath.LineTo(rect.Right, rect.Top);
            foldPath.LineTo(rect.Right, rect.Top + fold);
            foldPath.Close();
            using var foldFill = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xB0), IsAntialias = true };
            using var foldStroke = new SKPaint
            {
                Color = strokeC,
                IsStroke = true,
                StrokeWidth = 1,
                IsAntialias = true,
            };
            canvas.DrawPath(foldPath, foldFill);
            canvas.DrawPath(foldPath, foldStroke);
        }

        var cx = toSx(sheetX);
        var cy = toSy(sheetY);
        using var cross = new SKPaint
        {
            Color = strokeC,
            IsStroke = true,
            StrokeWidth = 1.1f,
            IsAntialias = true,
        };
        canvas.DrawLine(cx - 3, cy, cx + 3, cy, cross);
        canvas.DrawLine(cx, cy - 3, cx, cy + 3, cross);
    }

    static void DrawClosedFeatureOnSheet(
        SKCanvas canvas,
        IReadOnlyList<Point2> ring,
        LocalBounds bounds,
        NestPlacementMsg place,
        Func<double, float> toSx,
        Func<double, float> toSy,
        SKColor fill,
        SKColor stroke,
        IReadOnlyList<IReadOnlyList<Point2>>? holes = null)
    {
        using var gpath = new SKPath { FillType = SKPathFillType.EvenOdd };
        (float X, float Y) ToScreen(Point2 p)
        {
            var (wx, wy) = NestTransform.ToSheet(
                p.X, p.Y, bounds,
                place.OffsetX, place.OffsetY, place.RotationDeg);
            return (toSx(wx), toSy(wy));
        }
        double minX = 0, maxX = 0, minY = 0, maxY = 0;
        AppendRing(gpath, ring, ToScreen, ref minX, ref maxX, ref minY, ref maxY);
        foreach (var hole in holes ?? [])
        {
            if (hole.Count < 3) continue;
            AppendRing(gpath, hole, ToScreen, ref minX, ref maxX, ref minY, ref maxY);
        }
        using var gFill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = fill,
            IsAntialias = true,
        };
        using var gStroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = stroke,
            StrokeWidth = 1.4f,
            IsAntialias = true,
        };
        canvas.DrawPath(gpath, gFill);
        canvas.DrawPath(gpath, gStroke);
    }

    static void AppendRing(
        SKPath path,
        IReadOnlyList<Point2> ring,
        Func<Point2, (float X, float Y)> toScreen,
        ref double minX,
        ref double maxX,
        ref double minY,
        ref double maxY)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            var (sx, sy) = toScreen(p);
            if (i == 0) path.MoveTo(sx, sy); else path.LineTo(sx, sy);
        }
        path.Close();
    }

    static void DrawMeasureGuide(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        (double X, double Y) from,
        (double X, double Y) to) =>
        DrawDistanceOverlay(
            canvas, toSx, toSy, from, to,
            color: new SKColor(0xC4, 0x5A, 0x00),
            hideIfTiny: false);

    static void DrawDragGuide(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        (double X, double Y) from,
        (double X, double Y) to) =>
        DrawDistanceOverlay(
            canvas, toSx, toSy, from, to,
            color: new SKColor(0x00, 0x66, 0xCC),
            hideIfTiny: true);

    static void DrawDistanceOverlay(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        (double X, double Y) from,
        (double X, double Y) to,
        SKColor color,
        bool hideIfTiny)
    {
        var dist = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
        if (hideIfTiny && dist < 0.05) return;

        var x0 = toSx(from.X);
        var y0 = toSy(from.Y);
        var x1 = toSx(to.X);
        var y1 = toSy(to.Y);

        using (var line = new SKPaint
        {
            Color = color,
            IsStroke = true,
            StrokeWidth = 1.6f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8f, 5f], 0),
            StrokeCap = SKStrokeCap.Round,
        })
            canvas.DrawLine(x0, y0, x1, y1, line);

        using var dot = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawCircle(x0, y0, 3.5f, dot);
        canvas.DrawCircle(x1, y1, 3.5f, dot);

        var label = Fmt(dist) + " mm";
        var mx = (x0 + x1) * 0.5f;
        var my = (y0 + y1) * 0.5f;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        // Offset perpendicular so the number sits beside the midpoint, not on the line.
        var ox = len > 1 ? -dy / len * 14f : 0;
        var oy = len > 1 ? dx / len * 14f : -14f;
        if (oy > 0) { ox = -ox; oy = -oy; }

        using var font = new SKFont(UiTypeface(bold: true), 12);
        var tw = font.MeasureText(label);
        var tx = mx + ox - tw * 0.5f;
        var ty = my + oy + 4;
        using (var bg = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE6), IsAntialias = true })
            canvas.DrawRoundRect(tx - 4, ty - 13, tw + 8, 18, 3, 3, bg);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(label, tx, ty, SKTextAlign.Left, font, paint);
    }

    static void DrawSelectionBox(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        (double X0, double Y0, double X1, double Y1) box,
        bool crossing)
    {
        var x0 = toSx(Math.Min(box.X0, box.X1));
        var y0 = toSy(Math.Max(box.Y0, box.Y1));
        var x1 = toSx(Math.Max(box.X0, box.X1));
        var y1 = toSy(Math.Min(box.Y0, box.Y1));
        var w = x1 - x0;
        var h = y1 - y0;
        if (w < 1 && h < 1) return;

        var fill = crossing
            ? new SKColor(0x22, 0xAA, 0x44, 0x28)
            : new SKColor(0x00, 0x66, 0xCC, 0x28);
        var stroke = crossing
            ? new SKColor(0x1B, 0x8A, 0x38)
            : new SKColor(0x00, 0x66, 0xCC);
        using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
        using var strokePaint = new SKPaint
        {
            Color = stroke,
            IsStroke = true,
            StrokeWidth = 1.4f,
            IsAntialias = true,
            PathEffect = crossing ? SKPathEffect.CreateDash([7f, 5f], 0) : null,
        };
        canvas.DrawRect(x0, y0, w, h, fillPaint);
        canvas.DrawRect(x0, y0, w, h, strokePaint);
    }

    static void DrawSheetGrain(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float sw,
        float sh,
        SheetGrainKind grain)
    {
        var alongY = grain == SheetGrainKind.AlongLength;
        using var paint = new SKPaint
        {
            Color = new SKColor(0x8A, 0x6A, 0x2B, 0x70),
            IsStroke = true,
            StrokeWidth = 1.2f,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        const int n = 5;
        for (var i = 1; i <= n; i++)
        {
            if (alongY)
            {
                var x = sw * i / (n + 1);
                canvas.DrawLine(toSx(x), toSy(sh * 0.12), toSx(x), toSy(sh * 0.88), paint);
            }
            else
            {
                var y = sh * i / (n + 1);
                canvas.DrawLine(toSx(sw * 0.12), toSy(y), toSx(sw * 0.88), toSy(y), paint);
            }
        }
    }

    static void DrawGeomGrain(
        SKCanvas canvas,
        GeomInteraction.View view,
        Panel panel,
        (double MinX, double MinY, double MaxX, double MaxY, double W, double H) box)
    {
        var grain = GrainAlign.NormalizePart(panel.GrainDirection ?? panel.Orientation?.GrainDirection);
        if (grain is null) return;
        var alongX = grain == "X";
        var nx = alongX ? 1d : 0d;
        var ny = alongX ? 0d : 1d;
        var axis = alongX ? box.W : box.H;
        var half = Math.Max(12, axis * 0.38);
        DrawSlenderGrainArrow(
            canvas,
            x => GeomInteraction.ToScreen(view, x, box.MinY).Sx,
            y => GeomInteraction.ToScreen(view, box.MinX, y).Sy,
            (box.MinX + box.MaxX) * 0.5,
            (box.MinY + box.MaxY) * 0.5,
            nx, ny, half,
            strokePx: 1.6f,
            tipMm: Math.Clamp(axis * 0.06, 10, 22));
    }

    static void DrawPartGrain(
        SKCanvas canvas,
        Panel panel,
        NestPlacementMsg place,
        Func<double, float> toSx,
        Func<double, float> toSy,
        float scale)
    {
        var part = GrainAlign.NormalizePart(panel.GrainDirection ?? panel.Orientation?.GrainDirection);
        var axis = GrainAlign.WorldAxis(part, place.RotationDeg);
        if (axis is null) return;
        var bounds = NestTransform.BoundsOf(panel);
        var cx = (bounds.MinX + bounds.MaxX) * 0.5;
        var cy = (bounds.MinY + bounds.MaxY) * 0.5;
        var (wx, wy) = NestTransform.ToSheet(
            cx, cy, bounds, place.OffsetX, place.OffsetY, place.RotationDeg);
        var along = part == "X" ? bounds.MaxX - bounds.MinX : bounds.MaxY - bounds.MinY;
        var half = Math.Max(10, along * 0.38);
        var nx = axis.Value.X;
        var ny = axis.Value.Y;
        var len = Math.Sqrt(nx * nx + ny * ny);
        if (len < 1e-9) return;
        nx /= len;
        ny /= len;
        DrawSlenderGrainArrow(
            canvas, toSx, toSy, wx, wy, nx, ny, half,
            strokePx: Math.Clamp(0.9f * scale / 6f, 0.85f, 1.25f),
            tipMm: Math.Clamp(along * 0.055, 7, 16));
    }

    static void DrawSlenderGrainArrow(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        double cx,
        double cy,
        double nx,
        double ny,
        double halfSpanMm,
        float strokePx,
        double tipMm)
    {
        var x0 = cx - nx * halfSpanMm;
        var y0 = cy - ny * halfSpanMm;
        var x1 = cx + nx * halfSpanMm;
        var y1 = cy + ny * halfSpanMm;
        var color = new SKColor(0x8A, 0x42, 0x08, 0xE6);
        using var paint = new SKPaint
        {
            Color = color,
            IsStroke = true,
            StrokeWidth = strokePx,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(toSx(x0), toSy(y0), toSx(x1), toSy(y1), paint);
        DrawGrainTip(canvas, toSx, toSy, x1, y1, nx, ny, tipMm, strokePx, color);
        DrawGrainTip(canvas, toSx, toSy, x0, y0, -nx, -ny, tipMm, strokePx, color);
    }

    static void DrawGrainTip(
        SKCanvas canvas,
        Func<double, float> toSx,
        Func<double, float> toSy,
        double x,
        double y,
        double nx,
        double ny,
        double size,
        float strokePx,
        SKColor color)
    {
        var px = -ny;
        var py = nx;
        var ax = x - nx * size + px * size * 0.26;
        var ay = y - ny * size + py * size * 0.26;
        var bx = x - nx * size - px * size * 0.26;
        var by = y - ny * size - py * size * 0.26;
        using var paint = new SKPaint
        {
            Color = color,
            IsStroke = true,
            StrokeWidth = strokePx,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(toSx(x), toSy(y), toSx(ax), toSy(ay), paint);
        canvas.DrawLine(toSx(x), toSy(y), toSx(bx), toSy(by), paint);
    }

    static void DrawHandle(SKCanvas canvas, float x, float y, SKColor color)
    {
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        using var ring = new SKPaint { Color = SKColors.White, IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawCircle(x, y, 5, fill);
        canvas.DrawCircle(x, y, 5, ring);
    }

    static void DrawSquareHandle(SKCanvas canvas, float x, float y, SKColor color)
    {
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRect(x - 5, y - 5, 10, 10, fill);
        using var ring = new SKPaint { Color = SKColors.White, IsStroke = true, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRect(x - 5, y - 5, 10, 10, ring);
    }

    static void DrawDimH(SKCanvas canvas, GeomInteraction.View view, double x0, double x1, double y, string label, int outward)
    {
        var (a0, ay) = GeomInteraction.ToScreen(view, x0, y);
        var (a1, _) = GeomInteraction.ToScreen(view, x1, y);
        DrawDimHScreen(canvas, a0, a1, ay + outward * 14, label);
    }

    static void DrawDimV(SKCanvas canvas, GeomInteraction.View view, double y0, double y1, double x, string label, int outward)
    {
        var (ax, b0) = GeomInteraction.ToScreen(view, x, y0);
        var (_, b1) = GeomInteraction.ToScreen(view, x, y1);
        DrawDimVScreen(canvas, b0, b1, ax + outward * 14, label);
    }

    static void DrawDimHScreen(SKCanvas canvas, float a0, float a1, float y, string label)
    {
        using var p = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsStroke = true, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(a0, y - 4, a0, y + 4, p);
        canvas.DrawLine(a1, y - 4, a1, y + 4, p);
        canvas.DrawLine(a0, y, a1, y, p);
        DrawText(canvas, label, (a0 + a1) / 2 - 12, y - 3, 11, new SKColor(0x33, 0x33, 0x33));
    }

    static void DrawDimVScreen(SKCanvas canvas, float b0, float b1, float x, string label)
    {
        using var p = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsStroke = true, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(x - 4, b0, x + 4, b0, p);
        canvas.DrawLine(x - 4, b1, x + 4, b1, p);
        canvas.DrawLine(x, b0, x, b1, p);
        DrawText(canvas, label, x + 4, (b0 + b1) / 2, 11, new SKColor(0x33, 0x33, 0x33));
    }

    static SKTypeface? _uiTypeface;
    static SKTypeface? _uiTypefaceBold;

    /// <summary>
    /// Segoe UI has no CJK glyphs (shows tofu □). Prefer YaHei / UI fonts that cover Chinese.
    /// </summary>
    static SKTypeface UiTypeface(bool bold)
    {
        if (bold)
            return _uiTypefaceBold ??= ResolveUiTypeface(bold: true);
        return _uiTypeface ??= ResolveUiTypeface(bold: false);
    }

    static SKTypeface ResolveUiTypeface(bool bold)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        foreach (var family in new[]
                 {
                     "Microsoft YaHei UI",
                     "Microsoft YaHei",
                     "微软雅黑",
                     "Noto Sans CJK SC",
                     "Source Han Sans SC",
                     "Segoe UI",
                 })
        {
            var tf = SKTypeface.FromFamilyName(family, style);
            if (tf is null) continue;
            // Accept only if it can draw a common CJK ideograph (avoid silent tofu).
            if (tf.ContainsGlyph('板') || family is "Segoe UI")
                return tf;
            tf.Dispose();
        }
        return SKTypeface.Default;
    }

    static void DrawText(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool bold = false)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        using var font = new SKFont(UiTypeface(bold), size);
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }

    /// <summary>Truncate with ellipsis so nest labels stay inside the part AABB width.</summary>
    static string EllipsizeToWidth(string text, float maxWidthPx, float fontSize, bool bold)
    {
        if (string.IsNullOrEmpty(text) || maxWidthPx <= 8) return "";
        using var font = new SKFont(UiTypeface(bold), fontSize);
        if (font.MeasureText(text) <= maxWidthPx) return text;
        const string ellipsis = "…";
        var lo = 0;
        var hi = text.Length;
        var best = ellipsis;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var candidate = mid <= 0 ? ellipsis : text[..mid] + ellipsis;
            if (font.MeasureText(candidate) <= maxWidthPx)
            {
                best = candidate;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    static void DrawCentered(SKCanvas canvas, int w, int h, string text)
    {
        using var paint = new SKPaint { Color = new SKColor(0x88, 0x88, 0x88), IsAntialias = true };
        using var font = new SKFont(UiTypeface(bold: false), 14);
        canvas.DrawText(text, w / 2f, h / 2f, SKTextAlign.Center, font, paint);
    }

    static string Fmt(double v) =>
        Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.0");
}

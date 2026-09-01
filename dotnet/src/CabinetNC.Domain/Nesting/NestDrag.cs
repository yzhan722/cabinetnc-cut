namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

/// <summary>Port of src/render.js nest clamp / AABB resolve for drag drop.</summary>
public static class NestDrag
{
    public static double SnapMm(double v, double step = 10) =>
        Math.Round(v / step) * step;

    /// <summary>0/90/180/270 clockwise from <paramref name="rotDeg"/>.</summary>
    public static double RotateClockwise90(double rotDeg)
    {
        var r = ((int)Math.Round(rotDeg) % 360 + 360) % 360;
        return (r + 90) % 360;
    }

    /// <summary>AABB lower-left so the part centre sits on <paramref name="mx"/>,<paramref name="my"/>.</summary>
    public static (double Ox, double Oy) OffsetCenteredOn(
        Panel panel, double mx, double my, double rotDeg)
    {
        var (w, h) = SizeRotated(panel, rotDeg);
        return (mx - w * 0.5, my - h * 0.5);
    }

    public readonly record struct PackedPart(string Id, double LocalOx, double LocalOy, double Rot);

    /// <summary>
    /// Pack holding-bay parts into a left-to-right cluster (wraps when wider than
    /// <paramref name="maxWidth"/>). Local offsets are relative to the group lower-left.
    /// </summary>
    public static (double GroupW, double GroupH, IReadOnlyList<PackedPart> Parts) PackHoldCluster(
        IReadOnlyList<(string Id, double W, double H, double Rot)> parts,
        double spacingMm,
        double maxWidth)
    {
        var list = new List<PackedPart>(parts.Count);
        if (parts.Count == 0) return (0, 0, list);

        double x = 0, y = 0, rowH = 0, groupW = 0;
        foreach (var p in parts)
        {
            var w = Math.Max(0, p.W);
            var h = Math.Max(0, p.H);
            if (maxWidth > 0 && x > 0 && x + w > maxWidth + 1e-9)
            {
                y += rowH + spacingMm;
                x = 0;
                rowH = 0;
            }
            list.Add(new PackedPart(p.Id, x, y, p.Rot));
            x += w + spacingMm;
            if (h > rowH) rowH = h;
            groupW = Math.Max(groupW, x - spacingMm);
        }
        return (groupW, y + rowH, list);
    }

    public static (double Ox, double Oy) ClampGroupOnSheet(
        double groupW,
        double groupH,
        double ox,
        double oy,
        double sheetW,
        double sheetH,
        double borderMm) =>
        ClampGroupOnSheet(groupW, groupH, ox, oy, sheetW, sheetH, SheetInsets.Uniform(borderMm));

    public static (double Ox, double Oy) ClampGroupOnSheet(
        double groupW,
        double groupH,
        double ox,
        double oy,
        double sheetW,
        double sheetH,
        SheetInsets inset)
    {
        var minX = inset.Left;
        var minY = inset.Bottom;
        var maxX = Math.Max(minX, sheetW - inset.Right - groupW);
        var maxY = Math.Max(minY, sheetH - inset.Top - groupH);
        return (Clamp(ox, minX, maxX), Clamp(oy, minY, maxY));
    }

    /// <summary>Keep the AABB centre when rotation swaps width/height.</summary>
    public static (double Ox, double Oy) OffsetKeepingCenter(
        Panel panel, double ox, double oy, double oldRotDeg, double newRotDeg)
    {
        var (w, h) = SizeRotated(panel, oldRotDeg);
        var (nw, nh) = SizeRotated(panel, newRotDeg);
        return (ox + (w - nw) * 0.5, oy + (h - nh) * 0.5);
    }

    /// <summary>Lock a drag delta to left/right or up/down (dominant axis).</summary>
    public static (double Dx, double Dy) CardinalDelta(double dx, double dy) =>
        Math.Abs(dx) >= Math.Abs(dy) ? (dx, 0) : (0, dy);

    /// <summary>Sheet offset while dragging; <paramref name="cardinalOnly"/> = Alt orthogonal lock.</summary>
    public static (double Ox, double Oy) DragOffset(
        double origOx,
        double origOy,
        double startMx,
        double startMy,
        double mx,
        double my,
        bool cardinalOnly,
        double snapMm = 1)
    {
        var dx = mx - startMx;
        var dy = my - startMy;
        if (cardinalOnly)
            (dx, dy) = CardinalDelta(dx, dy);
        return (SnapMm(origOx + dx, snapMm), SnapMm(origOy + dy, snapMm));
    }

    public static (double MinX, double MinY, double MaxX, double MaxY) RectFromCorners(
        double x0, double y0, double x1, double y1) =>
        (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

    /// <summary>AutoCAD: drag left (end X &lt; start X) = crossing; right = window.</summary>
    public static bool IsCrossingSelect(double startX, double endX) => endX < startX;

    public static bool FullyInside(
        (double MinX, double MinY, double MaxX, double MaxY) part,
        (double MinX, double MinY, double MaxX, double MaxY) window) =>
        part.MinX >= window.MinX && part.MaxX <= window.MaxX
        && part.MinY >= window.MinY && part.MaxY <= window.MaxY;

    public static bool Overlaps(
        (double MinX, double MinY, double MaxX, double MaxY) a,
        (double MinX, double MinY, double MaxX, double MaxY) b) =>
        AabbConflict(a, b, gap: 0);

    /// <summary>
    /// Window (right): fully inside. Crossing (left): any AABB overlap.
    /// </summary>
    public static List<string> BoxSelect(
        IEnumerable<(string Id, double MinX, double MinY, double MaxX, double MaxY)> parts,
        double startX,
        double startY,
        double endX,
        double endY)
    {
        var window = RectFromCorners(startX, startY, endX, endY);
        var crossing = IsCrossingSelect(startX, endX);
        var hits = new List<string>();
        foreach (var part in parts)
        {
            var box = (part.MinX, part.MinY, part.MaxX, part.MaxY);
            if (crossing ? Overlaps(box, window) : FullyInside(box, window))
                hits.Add(part.Id);
        }
        return hits;
    }

    public static (double Ox, double Oy) ClampOnSheet(
        Panel panel,
        double ox,
        double oy,
        double rotDeg,
        double sheetW,
        double sheetH,
        double borderMm) =>
        ClampOnSheet(panel, ox, oy, rotDeg, sheetW, sheetH, SheetInsets.Uniform(borderMm));

    public static (double Ox, double Oy) ClampOnSheet(
        Panel panel,
        double ox,
        double oy,
        double rotDeg,
        double sheetW,
        double sheetH,
        SheetInsets inset)
    {
        var (w, h) = SizeRotated(panel, rotDeg);
        var minX = inset.Left;
        var minY = inset.Bottom;
        var maxX = Math.Max(minX, sheetW - inset.Right - w);
        var maxY = Math.Max(minY, sheetH - inset.Top - h);
        return (Clamp(ox, minX, maxX), Clamp(oy, minY, maxY));
    }

    /// <summary>One part in a rigid drag group. Rel* is world − grabbed world.</summary>
    public readonly record struct SlideMember(
        string Id,
        Panel Panel,
        double RelOx,
        double RelOy,
        double Rot);

    /// <summary>
    /// 华容道-style slide: from → to, stop at the last on-sheet pose that keeps
    /// spacing. Axes resolve separately (dominant first) so the group can slide
    /// along blockers. If <paramref name="fromOx"/> is already illegal, recover
    /// toward <paramref name="safeOx"/> first.
    /// </summary>
    public static (double Ox, double Oy) SlideTo(
        IReadOnlyList<SlideMember> moving,
        string grabbedId,
        double fromOx,
        double fromOy,
        double toOx,
        double toOy,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, Panel> byId,
        double sheetW,
        double sheetH,
        double spacingMm,
        double borderMm,
        double safeOx,
        double safeOy,
        IReadOnlySet<(string A, string B)>? ignorePairs = null) =>
        SlideTo(
            moving, grabbedId, fromOx, fromOy, toOx, toOy, sheetIndex, others, byId,
            sheetW, sheetH, spacingMm, SheetInsets.Uniform(borderMm), safeOx, safeOy, ignorePairs);

    public static (double Ox, double Oy) SlideTo(
        IReadOnlyList<SlideMember> moving,
        string grabbedId,
        double fromOx,
        double fromOy,
        double toOx,
        double toOy,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, Panel> byId,
        double sheetW,
        double sheetH,
        double spacingMm,
        SheetInsets inset,
        double safeOx,
        double safeOy,
        IReadOnlySet<(string A, string B)>? ignorePairs = null)
    {
        bool Ok(double ox, double oy) =>
            PoseFits(moving, grabbedId, ox, oy, sheetIndex, others, byId, sheetW, sheetH, spacingMm, inset, ignorePairs);

        if (!Ok(fromOx, fromOy))
        {
            (fromOx, fromOy) = SearchPose(Ok, safeOx, safeOy, fromOx, fromOy);
            if (!Ok(fromOx, fromOy))
            {
                var probed = ProbeFromBorders(
                    Ok, moving, grabbedId, toOx, toOy, sheetW, sheetH, inset);
                if (!probed.Found)
                    return (safeOx, safeOy);
                fromOx = probed.Ox;
                fromOy = probed.Oy;
            }
        }

        var dx = toOx - fromOx;
        var dy = toOy - fromOy;
        double x = fromOx, y = fromOy;
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            x = SearchPose(Ok, fromOx, fromOy, toOx, fromOy).Ox;
            y = SearchPose(Ok, x, fromOy, x, toOy).Oy;
        }
        else
        {
            y = SearchPose(Ok, fromOx, fromOy, fromOx, toOy).Oy;
            x = SearchPose(Ok, fromOx, y, toOx, y).Ox;
        }

        var snapped = (SnapMm(x, 1), SnapMm(y, 1));
        if (Ok(snapped.Item1, snapped.Item2))
            return snapped;
        return (x, y);
    }

    public static bool PoseFits(
        IReadOnlyList<SlideMember> moving,
        string grabbedId,
        double ox,
        double oy,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, Panel> byId,
        double sheetW,
        double sheetH,
        double spacingMm,
        double borderMm,
        IReadOnlySet<(string A, string B)>? ignorePairs = null) =>
        PoseFits(
            moving, grabbedId, ox, oy, sheetIndex, others, byId, sheetW, sheetH, spacingMm,
            SheetInsets.Uniform(borderMm), ignorePairs);

    public static bool PoseFits(
        IReadOnlyList<SlideMember> moving,
        string grabbedId,
        double ox,
        double oy,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, Panel> byId,
        double sheetW,
        double sheetH,
        double spacingMm,
        SheetInsets inset,
        IReadOnlySet<(string A, string B)>? ignorePairs = null)
    {
        if (moving.Count == 0) return true;
        var movingIds = new HashSet<string>(moving.Select(m => m.Id), StringComparer.Ordinal);
        var grabbed = moving.FirstOrDefault(m => m.Id == grabbedId);
        var originX = ox;
        var originY = oy;
        if (grabbed.Id is not null)
        {
            originX = ox - grabbed.RelOx;
            originY = oy - grabbed.RelOy;
        }

        foreach (var m in moving)
        {
            var mx = originX + m.RelOx;
            var my = originY + m.RelOy;
            var box = Aabb(m.Panel, mx, my, m.Rot);
            if (box.MinX < inset.Left - 0.05 || box.MinY < inset.Bottom - 0.05
                || box.MaxX > sheetW - inset.Right + 0.05 || box.MaxY > sheetH - inset.Top + 0.05)
                return false;
            foreach (var op in others)
            {
                if (op.SheetIndex != sheetIndex || movingIds.Contains(op.PanelId)) continue;
                if (ignorePairs is not null && ignorePairs.Contains((m.Id, op.PanelId))) continue;
                if (!byId.TryGetValue(op.PanelId, out var other)) continue;
                var ob = Aabb(other, op.Ox, op.Oy, op.Rot);
                if (AabbConflict(box, ob, spacingMm))
                    return false;
            }
        }
        return true;
    }

    static (double Ox, double Oy) SearchPose(
        Func<double, double, bool> ok,
        double fromOx,
        double fromOy,
        double toOx,
        double toOy)
    {
        if (ok(toOx, toOy)) return (toOx, toOy);
        if (!ok(fromOx, fromOy)) return (fromOx, fromOy);
        var loX = fromOx;
        var loY = fromOy;
        var hiX = toOx;
        var hiY = toOy;
        for (var i = 0; i < 24; i++)
        {
            var midX = (loX + hiX) * 0.5;
            var midY = (loY + hiY) * 0.5;
            if (ok(midX, midY))
            {
                loX = midX;
                loY = midY;
            }
            else
            {
                hiX = midX;
                hiY = midY;
            }
        }
        return (loX, loY);
    }

    static (double Ox, double Oy, bool Found) ProbeFromBorders(
        Func<double, double, bool> ok,
        IReadOnlyList<SlideMember> moving,
        string grabbedId,
        double toOx,
        double toOy,
        double sheetW,
        double sheetH,
        SheetInsets inset)
    {
        if (ok(toOx, toOy)) return (toOx, toOy, true);
        var grabbed = moving.FirstOrDefault(m => m.Id == grabbedId);
        if (grabbed.Id is null && moving.Count > 0) grabbed = moving[0];
        if (grabbed.Id is null) return (toOx, toOy, false);
        var min = ClampOnSheet(grabbed.Panel, -1e9, -1e9, grabbed.Rot, sheetW, sheetH, inset);
        var max = ClampOnSheet(grabbed.Panel, 1e9, 1e9, grabbed.Rot, sheetW, sheetH, inset);
        var tries = new[]
        {
            SearchPose(ok, min.Ox, toOy, toOx, toOy),
            SearchPose(ok, max.Ox, toOy, toOx, toOy),
            SearchPose(ok, toOx, min.Oy, toOx, toOy),
            SearchPose(ok, toOx, max.Oy, toOx, toOy),
        };
        (double Ox, double Oy, double Dist)? best = null;
        foreach (var t in tries)
        {
            if (!ok(t.Ox, t.Oy)) continue;
            var d = (t.Ox - toOx) * (t.Ox - toOx) + (t.Oy - toOy) * (t.Oy - toOy);
            if (best is null || d < best.Value.Dist)
                best = (t.Ox, t.Oy, d);
        }
        return best is null ? (toOx, toOy, false) : (best.Value.Ox, best.Value.Oy, true);
    }

    public static (double Ox, double Oy, bool Blocked) Resolve(
        Panel panel,
        string panelId,
        double ox,
        double oy,
        double rotDeg,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, Panel> byId,
        double sheetW,
        double sheetH,
        double spacingMm,
        double borderMm,
        (double Ox, double Oy) fallback,
        bool allowOverlap,
        IReadOnlySet<(string A, string B)>? ignorePairs = null,
        bool trueShape = false) =>
        Resolve(
            panel, panelId, ox, oy, rotDeg, sheetIndex, others, byId, sheetW, sheetH, spacingMm,
            SheetInsets.Uniform(borderMm), fallback, allowOverlap, ignorePairs, trueShape);

    public static (double Ox, double Oy, bool Blocked) Resolve(
        Panel panel,
        string panelId,
        double ox,
        double oy,
        double rotDeg,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, Panel> byId,
        double sheetW,
        double sheetH,
        double spacingMm,
        SheetInsets inset,
        (double Ox, double Oy) fallback,
        bool allowOverlap,
        IReadOnlySet<(string A, string B)>? ignorePairs = null,
        bool trueShape = false)
    {
        var clamped = ClampOnSheet(panel, ox, oy, rotDeg, sheetW, sheetH, inset);
        if (allowOverlap) return (clamped.Ox, clamped.Oy, false);

        if (trueShape)
        {
            if (NestValidator.HasPolygonConflict(
                    panel, panelId, clamped.Ox, clamped.Oy, rotDeg, sheetIndex,
                    byId, others, spacingMm, ignorePairs))
                return (fallback.Ox, fallback.Oy, true);
            return (clamped.Ox, clamped.Oy, false);
        }

        var box = Aabb(panel, clamped.Ox, clamped.Oy, rotDeg);
        foreach (var op in others)
        {
            if (op.PanelId == panelId || op.SheetIndex != sheetIndex) continue;
            if (ignorePairs is not null && ignorePairs.Contains((panelId, op.PanelId))) continue;
            if (!byId.TryGetValue(op.PanelId, out var other)) continue;
            var ob = Aabb(other, op.Ox, op.Oy, op.Rot);
            if (AabbConflict(box, ob, spacingMm))
                return (fallback.Ox, fallback.Oy, true);
        }
        return (clamped.Ox, clamped.Oy, false);
    }

    /// <summary>Keep the part AABB inside <paramref name="minX"/>…<paramref name="maxY"/>.</summary>
    public static (double Ox, double Oy) ClampInBounds(
        Panel panel,
        double ox,
        double oy,
        double rotDeg,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        var (w, h) = SizeRotated(panel, rotDeg);
        var loX = minX;
        var loY = minY;
        var hiX = Math.Max(loX, maxX - w);
        var hiY = Math.Max(loY, maxY - h);
        return (Clamp(ox, loX, hiX), Clamp(oy, loY, hiY));
    }

    public static (double MinX, double MinY, double MaxX, double MaxY) Aabb(
        Panel panel, double ox, double oy, double rotDeg)
    {
        var (w, h) = SizeRotated(panel, rotDeg);
        return (ox, oy, ox + w, oy + h);
    }

    public static (double W, double H) SizeRotated(Panel panel, double rotDeg)
    {
        var pts = panel.Outline.Points;
        if (pts.Count < 2) return (0, 0);
        var w = pts.Max(p => p.X) - pts.Min(p => p.X);
        var h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
        var r = ((int)Math.Round(rotDeg) % 360 + 360) % 360;
        return r is 90 or 270 ? (h, w) : (w, h);
    }

    static bool AabbConflict(
        (double MinX, double MinY, double MaxX, double MaxY) a,
        (double MinX, double MinY, double MaxX, double MaxY) b,
        double gap)
    {
        return !(a.MaxX + gap <= b.MinX || b.MaxX + gap <= a.MinX ||
                 a.MaxY + gap <= b.MinY || b.MaxY + gap <= a.MinY);
    }

    static double Clamp(double v, double lo, double hi) =>
        v < lo ? lo : v > hi ? hi : v;
}

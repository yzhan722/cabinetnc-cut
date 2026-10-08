namespace CabinetNC.Domain.Parts;

using CabinetNC.Domain.Geometry;

/// <summary>Port of src/geom/panel.js edit ops — returns new Panel instances.</summary>
public static class PanelEdit
{
    public static (double MinX, double MinY, double MaxX, double MaxY, double W, double H) BBox(Panel panel)
    {
        var pts = panel.Outline.Points;
        if (pts.Count == 0) return (0, 0, 0, 0, 0, 0);
        var minX = pts.Min(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxX = pts.Max(p => p.X);
        var maxY = pts.Max(p => p.Y);
        return (minX, minY, maxX, maxY, maxX - minX, maxY - minY);
    }

    public static bool IsAxisAlignedRect(Panel panel)
    {
        var pts = panel.Outline.Points;
        if (pts.Count < 4) return false;
        var (minX, minY, maxX, maxY, w, h) = BBox(panel);
        if (w < 1e-6 || h < 1e-6) return false;
        var uniq = new HashSet<(long, long)>();
        foreach (var p in pts)
        {
            var qx = (long)Math.Round(p.X * 1000);
            var qy = (long)Math.Round(p.Y * 1000);
            uniq.Add((qx, qy));
            var onEdge =
                (Math.Abs(p.X - minX) < 1e-6 || Math.Abs(p.X - maxX) < 1e-6) &&
                (p.Y >= minY - 1e-6 && p.Y <= maxY + 1e-6)
                ||
                (Math.Abs(p.Y - minY) < 1e-6 || Math.Abs(p.Y - maxY) < 1e-6) &&
                (p.X >= minX - 1e-6 && p.X <= maxX + 1e-6);
            if (!onEdge) return false;
        }
        return uniq.Count == 4;
    }

    public static Panel MoveHole(Panel panel, string featureId, double x, double y)
    {
        var feats = panel.Features.Select(f =>
        {
            if (!IsHole(f) || f.FeatureId != featureId) return f;
            return CloneFeature(f, x: x, y: y);
        }).ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel MoveGroovePoint(Panel panel, string featureId, int pointIndex, double x, double y)
    {
        var feats = panel.Features.Select(f =>
        {
            if (!IsGroove(f) || f.FeatureId != featureId || f.Path is null) return f;
            if (pointIndex < 0 || pointIndex >= f.Path.Count) return f;
            var path = f.Path.ToList();
            path[pointIndex] = new Point2(x, y);
            return CloneFeature(f, path: path);
        }).ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel TranslateFeatures(Panel panel, double dx, double dy)
    {
        Point2 Map(Point2 p) => new(p.X + dx, p.Y + dy);
        var feats = panel.Features.Select(f => MapFeature(f, Map)).ToList();
        return ClonePanel(panel, feats);
    }

    /// <summary>Translate only the listed features (CAD MOVE). Ids not on the panel are ignored.</summary>
    public static Panel TranslateFeatures(Panel panel, IEnumerable<string> featureIds, double dx, double dy)
    {
        var ids = featureIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 || (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)) return panel;
        Point2 Map(Point2 p) => new(p.X + dx, p.Y + dy);
        var feats = panel.Features
            .Select(f => ids.Contains(f.FeatureId) ? MapFeature(f, Map) : f)
            .ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel TranslateFeature(Panel panel, string featureId, double dx, double dy) =>
        TranslateFeatures(panel, [featureId], dx, dy);

    /// <summary>
    /// CAD COPY: duplicate the listed features displaced by (dx, dy). New ids are
    /// <c>{id}_c</c>, <c>{id}_c2</c>… so the source keeps its Fusion identity.
    /// </summary>
    public static (Panel Panel, IReadOnlyList<string> NewIds) CopyFeatures(
        Panel panel, IEnumerable<string> featureIds, double dx, double dy)
    {
        var ids = featureIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return (panel, []);
        Point2 Map(Point2 p) => new(p.X + dx, p.Y + dy);
        var used = new HashSet<string>(panel.Features.Select(f => f.FeatureId), StringComparer.OrdinalIgnoreCase);
        var feats = panel.Features.ToList();
        var created = new List<string>();
        foreach (var f in panel.Features)
        {
            if (!ids.Contains(f.FeatureId)) continue;
            var id = $"{f.FeatureId}_c";
            var n = 2;
            while (!used.Add(id))
                id = $"{f.FeatureId}_c{n++}";
            feats.Add(WithId(MapFeature(f, Map), id));
            created.Add(id);
        }
        return (ClonePanel(panel, feats), created);
    }

    public static Panel RemoveFeatures(Panel panel, IEnumerable<string> featureIds)
    {
        var ids = featureIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return panel;
        var feats = panel.Features.Where(f => !ids.Contains(f.FeatureId)).ToList();
        return ClonePanel(panel, feats);
    }

    /// <summary>Copy outline + features from <paramref name="source"/> onto <paramref name="target"/>, keeping target identity.</summary>
    public static Panel ReplaceGeometry(Panel target, Panel source) =>
        ClonePanel(target, source.Features, source.Outline);

    /// <summary>
    /// Every point of the feature (hole rim, path, profile, islands) lies inside the outline
    /// polygon or within <paramref name="tolMm"/> of its edge. Grooves that run out to the
    /// board edge therefore pass; a feature pushed off the board or onto a notch fails.
    /// </summary>
    public static bool IsFeatureInsideOutline(Panel panel, PanelFeature f, double tolMm = 1.0)
    {
        var ring = panel.Outline.Points;
        if (ring.Count < 3) return true;
        foreach (var p in FeatureSamplePoints(f))
        {
            if (PointInPolygon(p.X, p.Y, ring)) continue;
            if (DistanceToRing(p.X, p.Y, ring) <= tolMm) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// How far (mm) the feature's worst sample point sits outside the outline; 0 when fully
    /// inside. Lets an editor allow moves that do not make an existing overhang any worse.
    /// </summary>
    public static double FeatureOutsideMm(Panel panel, PanelFeature f)
    {
        var ring = panel.Outline.Points;
        if (ring.Count < 3) return 0;
        var worst = 0.0;
        foreach (var p in FeatureSamplePoints(f))
        {
            if (PointInPolygon(p.X, p.Y, ring)) continue;
            worst = Math.Max(worst, DistanceToRing(p.X, p.Y, ring));
        }
        return worst;
    }

    public static double FeatureOutsideMm(Panel panel, string featureId)
    {
        var f = panel.Features.FirstOrDefault(x => x.FeatureId == featureId);
        return f is null ? 0 : FeatureOutsideMm(panel, f);
    }

    public static IEnumerable<string> FeaturesOutsideOutline(Panel panel, IEnumerable<string> featureIds, double tolMm = 1.0)
    {
        var ids = featureIds.ToHashSet(StringComparer.Ordinal);
        foreach (var f in panel.Features)
        {
            if (!ids.Contains(f.FeatureId)) continue;
            if (!IsFeatureInsideOutline(panel, f, tolMm))
                yield return f.FeatureId;
        }
    }

    /// <summary>
    /// What a geometry edit invalidates downstream. Nest only reads the outline and
    /// through cutouts (parts-in-part voids); every other feature change is CAM-only.
    /// </summary>
    public static EditImpact ClassifyChange(Panel before, Panel after)
    {
        if (!SameRing(before.Outline.Points, after.Outline.Points))
            return EditImpact.Nest;
        var cutBefore = before.Features.Where(IsCutout).Select(CutoutKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var cutAfter = after.Features.Where(IsCutout).Select(CutoutKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!cutBefore.SequenceEqual(cutAfter, StringComparer.Ordinal))
            return EditImpact.Nest;
        var fb = before.Features.Select(FeatureKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var fa = after.Features.Select(FeatureKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return fb.SequenceEqual(fa, StringComparer.Ordinal) ? EditImpact.None : EditImpact.FeaturesOnly;
    }

    static IEnumerable<Point2> FeatureSamplePoints(PanelFeature f)
    {
        if (IsHole(f) && f.Path is null && f.Profile is null)
        {
            var r = Math.Max(0, (f.DiameterMm ?? 0) * 0.5);
            yield return new Point2(f.X, f.Y);
            if (r > 0)
            {
                yield return new Point2(f.X + r, f.Y);
                yield return new Point2(f.X - r, f.Y);
                yield return new Point2(f.X, f.Y + r);
                yield return new Point2(f.X, f.Y - r);
            }
            yield break;
        }
        foreach (var p in f.Path ?? []) yield return p;
        foreach (var p in f.Profile ?? []) yield return p;
        foreach (var ring in f.Holes ?? [])
            foreach (var p in ring) yield return p;
        if (f.Path is null && f.Profile is null)
            yield return new Point2(f.X, f.Y);
    }

    static bool PointInPolygon(double x, double y, IReadOnlyList<Point2> pts)
    {
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            var xi = pts[i].X;
            var yi = pts[i].Y;
            var xj = pts[j].X;
            var yj = pts[j].Y;
            var hit = yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi;
            if (hit) inside = !inside;
        }
        return inside;
    }

    static double DistanceToRing(double x, double y, IReadOnlyList<Point2> ring)
    {
        var best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var ax = ring[j].X;
            var ay = ring[j].Y;
            var bx = ring[i].X;
            var by = ring[i].Y;
            var vx = bx - ax;
            var vy = by - ay;
            var len2 = vx * vx + vy * vy;
            var t = len2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * vx + (y - ay) * vy) / len2, 0, 1);
            var px = ax + t * vx - x;
            var py = ay + t * vy - y;
            best = Math.Min(best, Math.Sqrt(px * px + py * py));
        }
        return best;
    }

    static bool SameRing(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].X - b[i].X) > 1e-6 || Math.Abs(a[i].Y - b[i].Y) > 1e-6)
                return false;
        }
        return true;
    }

    static string CutoutKey(PanelFeature f) =>
        $"{f.FeatureId}|{PointsKey(f.Path ?? f.Profile)}|{string.Join(";", (f.Holes ?? []).Select(PointsKey))}";

    static string FeatureKey(PanelFeature f) =>
        string.Join('|',
            f.FeatureId, f.Kind, f.FaceId ?? "", f.Through ? "T" : "B", f.Purpose ?? "",
            Fmt(f.X), Fmt(f.Y), Fmt(f.DiameterMm), Fmt(f.DepthMm), Fmt(f.WidthMm),
            PointsKey(f.Path), PointsKey(f.Profile),
            string.Join(";", (f.Holes ?? []).Select(PointsKey)));

    static string PointsKey(IReadOnlyList<Point2>? pts) =>
        pts is null ? "" : string.Join(",", pts.Select(p => $"{Fmt(p.X)}:{Fmt(p.Y)}"));

    static string Fmt(double? v) =>
        v is null ? "" : Math.Round(v.Value, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    static PanelFeature WithId(PanelFeature f, string id) =>
        new()
        {
            FeatureId = id,
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
            Path = f.Path,
            Profile = f.Profile,
            Holes = f.Holes,
            ProfileSegments = f.ProfileSegments,
            HoleSegments = f.HoleSegments,
        };

    public static Panel RotatePanel(Panel panel, double deg)
    {
        var (minX, minY, maxX, maxY, _, _) = BBox(panel);
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var rad = deg * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        Point2 Map(Point2 p)
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            return new Point2(cx + dx * c - dy * s, cy + dx * s + dy * c);
        }

        var outline = new Outline
        {
            Points = panel.Outline.Points.Select(Map).ToList(),
            Closed = panel.Outline.Closed,
            Frame = panel.Outline.Frame,
            Segments = CadPath.Map(panel.Outline.Segments, Map),
        };
        var feats = panel.Features.Select(f => MapFeature(f, Map)).ToList();
        return ClonePanel(panel, feats, outline);
    }

    public static Panel ResizeFromEdges(Panel panel, double minX, double minY, double maxX, double maxY)
    {
        var box = BBox(panel);
        var w0 = Math.Max(box.W, 1e-6);
        var h0 = Math.Max(box.H, 1e-6);
        var w1 = Math.Max(maxX - minX, 10);
        var h1 = Math.Max(maxY - minY, 10);
        Point2 Map(Point2 p)
        {
            var u = (p.X - box.MinX) / w0;
            var v = (p.Y - box.MinY) / h0;
            return new Point2(minX + u * w1, minY + v * h1);
        }

        var outline = new Outline
        {
            Points =
            [
                new(minX, minY),
                new(maxX, minY),
                new(maxX, maxY),
                new(minX, maxY),
            ],
            Closed = true,
            Frame = panel.Outline.Frame,
            Segments =
            [
                CadSegment.MakeLine(new(minX, minY), new(maxX, minY)),
                CadSegment.MakeLine(new(maxX, minY), new(maxX, maxY)),
                CadSegment.MakeLine(new(maxX, maxY), new(minX, maxY)),
                CadSegment.MakeLine(new(minX, maxY), new(minX, minY)),
            ],
        };
        var feats = panel.Features.Select(f => MapFeature(f, Map)).ToList();
        return ClonePanel(panel, feats, outline);
    }

    public static Panel AddVerticalHole(Panel panel, double x, double y, double diameterMm = 8, double? depthMm = null)
    {
        var id = NextId(panel, "H");
        var feats = panel.Features.ToList();
        feats.Add(new PanelFeature
        {
            FeatureId = id,
            Kind = "holeVertical",
            FaceId = panel.Side ?? panel.Orientation?.MillingFace,
            Through = (depthMm ?? panel.ThicknessMm) >= panel.ThicknessMm - 0.01,
            X = x,
            Y = y,
            DiameterMm = diameterMm,
            DepthMm = depthMm ?? panel.ThicknessMm,
        });
        return ClonePanel(panel, feats);
    }

    /// <summary>Synthetic id for the board outline on the editor canvas.</summary>
    public const string OutlineId = "__outline__";

    public static bool IsOutlineId(string? id) =>
        string.Equals(id, OutlineId, StringComparison.Ordinal)
        || string.Equals(id, "外框", StringComparison.Ordinal)
        || string.Equals(id, "OUTLINE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, "PROFILE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, "轮廓", StringComparison.Ordinal);

    public static Panel AddVerticalGroove(Panel panel, IReadOnlyList<Point2> path, double widthMm = 6, double depthMm = 8, bool through = false)
    {
        if (path.Count < 2) return panel;
        var id = NextId(panel, "G");
        var feats = panel.Features.ToList();
        feats.Add(new PanelFeature
        {
            FeatureId = id,
            Kind = "grooveVertical",
            Through = through,
            FaceId = panel.Side ?? panel.Orientation?.MillingFace,
            X = path[0].X,
            Y = path[0].Y,
            WidthMm = widthMm,
            DepthMm = depthMm,
            Path = path.ToList(),
        });
        return ClonePanel(panel, feats);
    }

    /// <summary>
    /// Centrelines / rings other features present to TRIM and EXTEND. The outline is always
    /// included; <paramref name="featureIds"/> null means every feature except
    /// <paramref name="excludeId"/>.
    /// </summary>
    public static List<IReadOnlyList<Point2>> CutterGeometry(Panel panel, IReadOnlyCollection<string>? featureIds, string? excludeId)
    {
        var list = new List<IReadOnlyList<Point2>>();
        var ring = panel.Outline.Points;
        if (ring.Count >= 3)
        {
            var closed = ring.ToList();
            closed.Add(ring[0]);
            list.Add(closed);
        }
        foreach (var f in panel.Features)
        {
            if (f.FeatureId == excludeId) continue;
            if (featureIds is not null && !featureIds.Contains(f.FeatureId)) continue;
            var g = FeatureCenterline(f);
            if (g is not null) list.Add(g);
        }
        return list;
    }

    /// <summary>Polyline standing in for a feature: groove path, ring (closed) or circle.</summary>
    public static IReadOnlyList<Point2>? FeatureCenterline(PanelFeature f)
    {
        if (IsGroove(f))
            return f.Path is { Count: >= 2 } ? f.Path : null;
        var ring = f.Path is { Count: >= 3 } ? f.Path : f.Profile is { Count: >= 3 } ? f.Profile : null;
        if (ring is not null)
        {
            var closed = ring.ToList();
            closed.Add(ring[0]);
            return closed;
        }
        if (IsHole(f))
        {
            var r = Math.Max(0, (f.DiameterMm ?? 0) * 0.5);
            if (r <= 0) return null;
            const int n = 48;
            var pts = new List<Point2>(n + 1);
            for (var i = 0; i <= n; i++)
            {
                var a = 2 * Math.PI * i / n;
                pts.Add(new Point2(f.X + r * Math.Cos(a), f.Y + r * Math.Sin(a)));
            }
            return pts;
        }
        return null;
    }

    /// <summary>
    /// TRIM a straight-segment groove at every cutter, removing the piece under
    /// <paramref name="pick"/>. First surviving piece keeps the id; others get <c>{id}_t</c>,
    /// <c>{id}_t2</c>… Returns null with <paramref name="error"/> set when nothing changes.
    /// </summary>
    public static Panel? TrimGroove(Panel panel, string featureId, IReadOnlyCollection<string>? cutterIds, Point2 pick, out string? error)
    {
        error = null;
        var f = panel.Features.FirstOrDefault(x => x.FeatureId == featureId);
        if (f is null || !IsGroove(f) || f.Path is not { Count: >= 2 })
        {
            error = "只能修剪槽";
            return null;
        }
        if (f.ProfileSegments is { Count: > 0 } && f.ProfileSegments.Any(s => !s.IsLine))
        {
            error = "带弧段的槽暂不支持修剪";
            return null;
        }
        var kept = CenterlineOps.Trim(f.Path, CutterGeometry(panel, cutterIds, featureId), pick);
        if (kept is null)
        {
            error = "该槽与切割边没有交点";
            return null;
        }
        var feats = panel.Features.Where(x => x.FeatureId != featureId).ToList();
        var idx = panel.Features.ToList().FindIndex(x => x.FeatureId == featureId);
        var pieces = new List<PanelFeature>();
        for (var k = 0; k < kept.Count; k++)
        {
            var id = k == 0 ? featureId : NextSuffixId(panel, featureId, "_t", k);
            pieces.Add(WithPath(f, id, kept[k]));
        }
        feats.InsertRange(Math.Clamp(idx, 0, feats.Count), pieces);
        return ClonePanel(panel, feats);
    }

    /// <summary>EXTEND the groove end nearest <paramref name="pick"/> until it meets a boundary.</summary>
    public static Panel? ExtendGroove(Panel panel, string featureId, IReadOnlyCollection<string>? boundaryIds, Point2 pick, out string? error)
    {
        error = null;
        var f = panel.Features.FirstOrDefault(x => x.FeatureId == featureId);
        if (f is null || !IsGroove(f) || f.Path is not { Count: >= 2 })
        {
            error = "只能延伸槽";
            return null;
        }
        if (f.ProfileSegments is { Count: > 0 } && f.ProfileSegments.Any(s => !s.IsLine))
        {
            error = "带弧段的槽暂不支持延伸";
            return null;
        }
        var path = CenterlineOps.Extend(f.Path, CutterGeometry(panel, boundaryIds, featureId), pick);
        if (path is null)
        {
            error = "延长方向上没有边界";
            return null;
        }
        var feats = panel.Features.Select(x => x.FeatureId == featureId ? WithPath(f, featureId, path) : x).ToList();
        return ClonePanel(panel, feats);
    }

    /// <summary>
    /// Replace the outer ring. Closing vertex optional. New winding is flipped to match the
    /// original. Cached CAD segments are dropped — CAM rebuilds them.
    /// </summary>
    public static Panel? ReplaceOutline(Panel panel, IReadOnlyList<Point2> ring, out string? error)
    {
        error = null;
        var verts = CenterlineOps.OpenRing(ring);
        if (verts.Count < 3)
        {
            error = "外框至少要 3 个点";
            return null;
        }
        var area = CenterlineOps.SignedArea(verts);
        if (Math.Abs(area) < 1)
        {
            error = "外框面积太小";
            return null;
        }
        var oldSign = Math.Sign(CenterlineOps.SignedArea(panel.Outline.Points));
        if (oldSign != 0 && Math.Sign(area) != oldSign)
            verts.Reverse();
        var outline = new Outline
        {
            Points = verts,
            Closed = true,
            Frame = panel.Outline.Frame,
        };
        return ClonePanel(panel, panel.Features, outline);
    }

    /// <summary>
    /// Open cutter across the outline: must hit twice. Keeps the larger remaining loop
    /// (corner notch or through-cut).
    /// </summary>
    public static Panel? NotchOutline(Panel panel, IReadOnlyList<Point2> cutter, out string? error)
    {
        error = null;
        var hits = CenterlineOps.RingHits(panel.Outline.Points, cutter);
        if (hits.Count < 2)
        {
            error = hits.Count == 0
                ? "外框开链与轮廓不相交"
                : "外框开链只穿过轮廓一次 · 切角/切断需要穿过两次";
            return null;
        }
        if (hits.Count > 2)
        {
            error = $"外框开链与轮廓交了 {hits.Count} 次 · 请画一条只穿过两次的线";
            return null;
        }
        var next = CenterlineOps.NotchRing(panel.Outline.Points, cutter);
        if (next is null || next.Count < 3)
        {
            error = "无法用这条线切开外框";
            return null;
        }
        return ReplaceOutline(panel, next, out error);
    }

    /// <summary>
    /// TRIM the outline: drop the ring piece under <paramref name="pick"/> between cutter hits
    /// and bridge the gap. Outline is never a cutter of itself.
    /// </summary>
    public static Panel? TrimOutline(Panel panel, IReadOnlyCollection<string>? cutterIds, Point2 pick, out string? error)
    {
        error = null;
        var cutters = new List<IReadOnlyList<Point2>>();
        foreach (var f in panel.Features)
        {
            if (cutterIds is not null && !cutterIds.Contains(f.FeatureId)) continue;
            var g = FeatureCenterline(f);
            if (g is not null) cutters.Add(g);
        }
        if (cutters.Count == 0)
        {
            error = "没有可用的切割边";
            return null;
        }
        var next = CenterlineOps.TrimClosedRing(panel.Outline.Points, cutters, pick);
        if (next is null)
        {
            error = "该边与切割边没有两个交点，无法保持闭合外框";
            return null;
        }
        return ReplaceOutline(panel, next, out error);
    }

    /// <summary>
    /// How a groove about to be drawn relates to existing grooves. Worst case wins:
    /// Duplicate &gt; Collinear &gt; Crossing &gt; None. <paramref name="otherId"/> names the culprit.
    /// </summary>
    public static CenterlineOverlap ClassifyGrooveOverlap(Panel panel, IReadOnlyList<Point2> path, out string? otherId)
    {
        otherId = null;
        var worst = CenterlineOverlap.None;
        foreach (var f in panel.Features)
        {
            if (!IsGroove(f) || f.Path is not { Count: >= 2 }) continue;
            var k = CenterlineOps.Classify(path, f.Path);
            if (k > worst)
            {
                worst = k;
                otherId = f.FeatureId;
            }
        }
        return worst;
    }

    static PanelFeature WithPath(PanelFeature f, string id, IReadOnlyList<Point2> path) =>
        new()
        {
            FeatureId = id,
            Kind = f.Kind,
            FaceId = f.FaceId,
            Through = f.Through,
            GroupId = f.GroupId,
            Purpose = f.Purpose,
            SourceRelationshipId = f.SourceRelationshipId,
            X = path[0].X,
            Y = path[0].Y,
            DiameterMm = f.DiameterMm,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
            Path = path.ToList(),
            // Cached strip outline is stale after a path change; CAM rebuilds it from width.
            Profile = null,
            ProfileSegments = null,
            Holes = null,
            HoleSegments = null,
        };

    static string NextSuffixId(Panel panel, string baseId, string suffix, int k)
    {
        var id = k <= 1 ? $"{baseId}{suffix}" : $"{baseId}{suffix}{k}";
        var n = k;
        while (panel.Features.Any(f => f.FeatureId == id))
            id = $"{baseId}{suffix}{++n}";
        return id;
    }

    public static Panel UpdateFeatureParams(
        Panel panel,
        string featureId,
        double? x = null,
        double? y = null,
        double? diameterMm = null,
        double? depthMm = null,
        double? widthMm = null)
    {
        var feats = panel.Features.Select(f =>
        {
            if (f.FeatureId != featureId) return f;
            return new PanelFeature
            {
                FeatureId = f.FeatureId,
                Kind = f.Kind,
                FaceId = f.FaceId,
                Through = f.Through,
                GroupId = f.GroupId,
                Purpose = f.Purpose,
                SourceRelationshipId = f.SourceRelationshipId,
                X = x ?? f.X,
                Y = y ?? f.Y,
                DiameterMm = diameterMm ?? f.DiameterMm,
                DepthMm = depthMm ?? f.DepthMm,
                WidthMm = widthMm ?? f.WidthMm,
                Path = f.Path,
                Profile = f.Profile,
                Holes = f.Holes,
                ProfileSegments = f.ProfileSegments,
                HoleSegments = f.HoleSegments,
            };
        }).ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel RemoveFeature(Panel panel, string featureId)
    {
        var feats = panel.Features.Where(f => f.FeatureId != featureId).ToList();
        return ClonePanel(panel, feats);
    }

    /// <summary>Mirror about panel bbox center. axis "X" flips X; "Y" flips Y.</summary>
    public static Panel Mirror(Panel panel, string axis)
    {
        var ax = axis.Trim().ToUpperInvariant();
        var (minX, minY, maxX, maxY, _, _) = BBox(panel);
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        Point2 Map(Point2 p) => ax switch
        {
            "X" => new Point2(2 * cx - p.X, p.Y),
            "Y" => new Point2(p.X, 2 * cy - p.Y),
            _ => p,
        };

        var outline = new Outline
        {
            Points = panel.Outline.Points.Select(Map).ToList(),
            Closed = panel.Outline.Closed,
            Frame = panel.Outline.Frame,
            Segments = CadPath.Map(panel.Outline.Segments, Map, flipCw: true),
        };
        var feats = panel.Features.Select(f => MapFeature(f, Map, flipCw: true)).ToList();

        var banding = panel.EdgeBanding;
        if (banding is not null)
        {
            banding = ax switch
            {
                "X" => new EdgeBanding
                {
                    Front = banding.Front,
                    Back = banding.Back,
                    Left = banding.Right,
                    Right = banding.Left,
                },
                "Y" => new EdgeBanding
                {
                    Front = banding.Back,
                    Back = banding.Front,
                    Left = banding.Left,
                    Right = banding.Right,
                },
                _ => banding,
            };
        }

        var side = FlipFace(panel.Side);
        var orient = panel.Orientation;
        if (orient is not null)
        {
            orient = new WorkpieceOrientation
            {
                PrimaryFace = FlipFace(orient.PrimaryFace) ?? orient.PrimaryFace,
                MillingFace = FlipFace(orient.MillingFace) ?? orient.MillingFace,
                GrainDirection = orient.GrainDirection,
                AllowedRotations = orient.AllowedRotations,
                AllowMirror = orient.AllowMirror,
                FlipStrategy = ax is "X" or "Y" ? ax.ToLowerInvariant() : orient.FlipStrategy,
            };
        }

        return new Panel
        {
            PanelId = panel.PanelId,
            Name = panel.Name,
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = outline,
            Features = feats,
            Identity = panel.Identity,
            Orientation = orient,
            EdgeBanding = banding,
            EdgeBands = panel.EdgeBands,
            Notes = panel.Notes,
            Side = side ?? panel.Side,
            Faces = panel.Faces,
        };
    }

    /// <summary>Deep-ish copy with a new PanelId; feature IDs get a unique suffix.</summary>
    public static Panel Duplicate(Panel panel, string newPanelId)
    {
        var feats = panel.Features.Select(f => new PanelFeature
        {
            FeatureId = $"{f.FeatureId}_c",
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
            Path = f.Path?.ToList(),
            Profile = f.Profile?.ToList(),
            Holes = f.Holes?.Select(ring => (IReadOnlyList<Point2>)ring.ToList()).ToList(),
            ProfileSegments = f.ProfileSegments?.ToList(),
            HoleSegments = f.HoleSegments?
                .Select(ring => (IReadOnlyList<CadSegment>)ring.ToList()).ToList(),
        }).ToList();
        // ensure unique feature ids within panel
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < feats.Count; i++)
        {
            var id = feats[i].FeatureId;
            var n = 1;
            while (!used.Add(id))
                id = $"{feats[i].FeatureId}{n++}";
            if (id != feats[i].FeatureId)
                feats[i] = new PanelFeature
                {
                    FeatureId = id,
                    Kind = feats[i].Kind,
                    FaceId = feats[i].FaceId,
                    Through = feats[i].Through,
                    GroupId = feats[i].GroupId,
                    Purpose = feats[i].Purpose,
                    SourceRelationshipId = feats[i].SourceRelationshipId,
                    X = feats[i].X,
                    Y = feats[i].Y,
                    DiameterMm = feats[i].DiameterMm,
                    DepthMm = feats[i].DepthMm,
                    WidthMm = feats[i].WidthMm,
                    Path = feats[i].Path,
                    Profile = feats[i].Profile,
                    Holes = feats[i].Holes,
                    ProfileSegments = feats[i].ProfileSegments,
                    HoleSegments = feats[i].HoleSegments,
                };
        }

        WorkpieceIdentity? identity = panel.Identity is null
            ? null
            : new WorkpieceIdentity
            {
                PackageId = panel.Identity.PackageId,
                PackageLabel = panel.Identity.PackageLabel,
                ProjectId = panel.Identity.ProjectId,
                ModuleId = panel.Identity.ModuleId,
                WorkpieceId = newPanelId,
                Role = panel.Identity.Role,
                SourcePath = panel.Identity.SourcePath,
                SourceFormat = panel.Identity.SourceFormat,
            };

        return new Panel
        {
            PanelId = newPanelId,
            Name = panel.Name is null ? null : $"{panel.Name} (copy)",
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = new Outline
            {
                Points = panel.Outline.Points.ToList(),
                Closed = panel.Outline.Closed,
                Frame = panel.Outline.Frame,
                Segments = panel.Outline.Segments?.ToList(),
            },
            Features = feats,
            Identity = identity,
            Orientation = panel.Orientation,
            EdgeBanding = panel.EdgeBanding is null
                ? null
                : new EdgeBanding
                {
                    Front = panel.EdgeBanding.Front,
                    Back = panel.EdgeBanding.Back,
                    Left = panel.EdgeBanding.Left,
                    Right = panel.EdgeBanding.Right,
                },
            EdgeBands = panel.EdgeBands,
            Notes = panel.Notes,
            Side = panel.Side,
            Faces = panel.Faces,
        };
    }

    /// <summary>ASSUMED Day4: shortest edge &lt; 80 mm or area &lt; 0.02 m².</summary>
    public static bool IsSmallPanel(Panel panel, out string reason)
    {
        var (_, _, _, _, w, h) = BBox(panel);
        var shortEdge = Math.Min(w, h);
        var areaM2 = (w * h) / 1_000_000.0;
        if (shortEdge < 80)
        {
            reason = $"最短边 {shortEdge:0.#} mm < 80 mm";
            return true;
        }
        if (areaM2 < 0.02)
        {
            reason = $"面积 {areaM2:0.####} m² < 0.02 m²";
            return true;
        }
        reason = "";
        return false;
    }

    static string? FlipFace(string? face)
    {
        if (string.IsNullOrWhiteSpace(face)) return face;
        return face.Trim().ToUpperInvariant() switch
        {
            "A" => "B",
            "B" => "A",
            _ => face,
        };
    }

    public static bool IsHole(PanelFeature f) =>
        f.Kind.Contains("hole", StringComparison.OrdinalIgnoreCase);

    public static bool IsGroove(PanelFeature f) =>
        f.Kind.Contains("groove", StringComparison.OrdinalIgnoreCase);

    public static bool IsTongueGroove(PanelFeature f)
    {
        if (!IsGroove(f)) return false;
        var blob = $"{f.Purpose} {f.Kind}";
        return blob.Contains("tongue", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("半槽", StringComparison.OrdinalIgnoreCase);
    }

    public static Panel SetFeaturePurpose(Panel panel, string featureId, string? purpose)
    {
        var feats = panel.Features
            .Select(f => f.FeatureId == featureId
                ? CloneFeature(f, purpose: purpose, replacePurpose: true)
                : f)
            .ToList();
        return ClonePanel(panel, feats);
    }

    public static bool IsCutout(PanelFeature f) =>
        f.Kind.Contains("cutout", StringComparison.OrdinalIgnoreCase)
        || (f.Through && f.Kind.Contains("pocket", StringComparison.OrdinalIgnoreCase)
            && f.Path is { Count: >= 3 });

    /// <summary>Blind closed floor (LED channels, cups, etc.) — not a through cutout.</summary>
    public static bool IsPocket(PanelFeature f) =>
        !f.Through
        && f.Kind.Contains("pocket", StringComparison.OrdinalIgnoreCase)
        && (f.Path is { Count: >= 3 } || f.Profile is { Count: >= 3 });

    public static string FeatureDisplayLabel(PanelFeature f)
    {
        var purpose = (f.Purpose ?? "").Trim();
        if (purpose.Length == 0) return "";
        if (purpose.Contains("led", StringComparison.OrdinalIgnoreCase))
            return "LED";
        return purpose;
    }

    static string NextId(Panel panel, string prefix)
    {
        var n = panel.Features.Count + 1;
        string id;
        do { id = $"{prefix}{n++}"; }
        while (panel.Features.Any(f => f.FeatureId == id));
        return id;
    }

    static Panel ClonePanel(Panel panel, IReadOnlyList<PanelFeature> feats, Outline? outline = null) =>
        new()
        {
            PanelId = panel.PanelId,
            Name = panel.Name,
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = outline ?? panel.Outline,
            Features = feats,
            Identity = panel.Identity,
            Orientation = panel.Orientation,
            EdgeBanding = panel.EdgeBanding,
            EdgeBands = panel.EdgeBands,
            Notes = panel.Notes,
            Side = panel.Side,
            Faces = panel.Faces,
        };

    static PanelFeature MapFeature(PanelFeature f, Func<Point2, Point2> map, bool flipCw = false)
    {
        if (IsHole(f) && f.Path is null && f.Profile is null)
        {
            var q = map(new Point2(f.X, f.Y));
            return CloneFeature(f, x: q.X, y: q.Y);
        }
        // X/Y is the anchor for every kind (holes: centre; others: first path point) —
        // map it too so Inspector numbers follow the geometry after MOVE / mirror / rotate.
        var anchor = map(new Point2(f.X, f.Y));
        return CloneFeature(
            f,
            x: anchor.X,
            y: anchor.Y,
            path: f.Path?.Select(map).ToList(),
            profile: f.Profile?.Select(map).ToList(),
            holes: f.Holes?
                .Select(ring => (IReadOnlyList<Point2>)ring.Select(map).ToList())
                .ToList(),
            profileSegments: CadPath.Map(f.ProfileSegments, map, flipCw),
            holeSegments: f.HoleSegments?
                .Select(ring => CadPath.Map(ring, map, flipCw))
                .ToList());
    }

    static PanelFeature CloneFeature(
        PanelFeature f,
        double? x = null,
        double? y = null,
        IReadOnlyList<Point2>? path = null,
        IReadOnlyList<Point2>? profile = null,
        IReadOnlyList<IReadOnlyList<Point2>>? holes = null,
        IReadOnlyList<CadSegment>? profileSegments = null,
        IReadOnlyList<IReadOnlyList<CadSegment>>? holeSegments = null,
        string? purpose = null,
        bool replacePurpose = false) =>
        new()
        {
            FeatureId = f.FeatureId,
            Kind = f.Kind,
            FaceId = f.FaceId,
            Through = f.Through,
            GroupId = f.GroupId,
            Purpose = replacePurpose
                ? (string.IsNullOrWhiteSpace(purpose) ? null : purpose)
                : f.Purpose,
            SourceRelationshipId = f.SourceRelationshipId,
            X = x ?? f.X,
            Y = y ?? f.Y,
            DiameterMm = f.DiameterMm,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
            Path = path ?? f.Path,
            Profile = profile ?? f.Profile,
            Holes = holes ?? f.Holes,
            ProfileSegments = profileSegments ?? f.ProfileSegments,
            HoleSegments = holeSegments ?? f.HoleSegments,
        };
}

/// <summary>Downstream cost of a panel geometry edit.</summary>
public enum EditImpact
{
    /// <summary>Geometry identical.</summary>
    None,
    /// <summary>Holes / grooves / pockets changed — keep nest, recompute CAM.</summary>
    FeaturesOnly,
    /// <summary>Outline or through cutouts changed — nest and parts-in-part are stale.</summary>
    Nest,
}

namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

/// <summary>L3: closed last-pass contours become panels; tool radius is removed.</summary>
public static class NcToPanels
{
    public static IReadOnlyList<Panel> Recover(
        IReadOnlyList<CutOp> ops,
        IReadOnlyDictionary<string, ToolDefinition>? tools = null)
    {
        var catalog = tools ?? ToolCatalog.DefaultMap();
        var contours = ops.Where(o => o.Op == "contour" && o.Path is { Count: >= 3 }).ToList();
        if (contours.Count == 0) return [];

        var outers = new List<CutOp>();
        var inners = new List<CutOp>();
        foreach (var c in contours)
        {
            var inside = contours.Any(o =>
                !ReferenceEquals(o, c)
                && Area(o.Path!) > Area(c.Path!) + 1
                && CentroidInside(c.Path!, o.Path!));
            if (inside) inners.Add(c);
            else outers.Add(c);
        }

        var features = ops.Where(o => o.Op is "drill" or "groove" or "pocket").ToList();
        var panels = new List<Panel>();
        var i = 0;
        foreach (var outer in outers.OrderByDescending(o => Area(o.Path!)))
        {
            i++;
            var radius = RadiusOf(outer.ToolId, catalog);
            var partPath = OffsetClosed(outer, -radius);
            if (partPath.Count < 3)
                partPath = outer.Path!.ToList();

            var minX = partPath.Min(p => p.X);
            var minY = partPath.Min(p => p.Y);
            var local = partPath.Select(p => new Point2(p.X - minX, p.Y - minY)).ToList();
            var th = outer.ThicknessMm is > 0 ? outer.ThicknessMm.Value : 18;

            var owned = new List<PanelFeature>();
            var fid = 0;
            foreach (var inner in inners)
            {
                if (!CentroidInside(inner.Path!, outer.Path!)) continue;
                fid++;
                // The NC loop is the cutter centre running inside the window; the
                // finished window is one tool radius larger on every side.
                var holeRadius = RadiusOf(inner.ToolId, catalog);
                var holePath = OffsetClosed(inner, holeRadius);
                if (holePath.Count < 3)
                    holePath = inner.Path!;
                var pts = holePath.Select(p => new Point2(p.X - minX, p.Y - minY)).ToList();
                owned.Add(new PanelFeature
                {
                    FeatureId = "CUT-" + fid,
                    Kind = "cutout",
                    FaceId = "THROUGH",
                    Through = true,
                    Path = pts,
                    DepthMm = th,
                });
            }
            foreach (var f in features)
            {
                if (!BelongsTo(f, outer.Path!)) continue;
                fid++;
                owned.Add(ToFeature(f, fid, minX, minY, th));
            }

            panels.Add(new Panel
            {
                PanelId = "NC-" + i.ToString("00"),
                Name = "NC-" + i.ToString("00"),
                ThicknessMm = th,
                Quantity = 1,
                Outline = new Outline { Points = local, Closed = true },
                Features = owned,
            });
        }
        return panels;
    }

    /// <summary>
    /// Signed Clipper offset in sheet space. Positive expands, negative shrinks.
    /// FeatureId is cleared so the inner/outer sign flip in CAM does not invert the delta.
    /// </summary>
    static IReadOnlyList<(double X, double Y)> OffsetClosed(CutOp contour, double signedMm)
    {
        if (contour.Path is not { Count: >= 3 }) return contour.Path ?? [];
        if (Math.Abs(signedMm) < 1e-6) return contour.Path;
        var op = contour with { FeatureId = null, Op = "contour" };
        var offset = ContourToolOffset.Apply([op], signedMm);
        return offset[0].Path is { Count: >= 3 } p ? p : contour.Path;
    }

    static double RadiusOf(string? toolId, IReadOnlyDictionary<string, ToolDefinition> catalog)
    {
        if (!string.IsNullOrWhiteSpace(toolId)
            && catalog.TryGetValue(toolId, out var t)
            && t.DiameterMm > 0)
            return t.DiameterMm * 0.5;
        return TroyRecipe.WorkDiameterMm * 0.5;
    }

    static PanelFeature ToFeature(CutOp op, int id, double minX, double minY, double th)
    {
        if (op.Op == "drill")
        {
            var x = (op.SheetX ?? op.X ?? 0) - minX;
            var y = (op.SheetY ?? op.Y ?? 0) - minY;
            return new PanelFeature
            {
                FeatureId = "H" + id,
                Kind = "holeVertical",
                FaceId = op.Through ? "THROUGH" : "A",
                Through = op.Through,
                X = x,
                Y = y,
                DiameterMm = op.DiameterMm ?? 3,
                DepthMm = op.DepthMm ?? th,
            };
        }

        var path = (op.FinishLoop ?? op.Path)?
            .Select(p => new Point2(p.X - minX, p.Y - minY))
            .ToList();
        var kind = op.Op == "pocket" ? "pocket" : "grooveVertical";
        var purpose = op.IsTongue ? "tongue" : null;
        var cx = path is { Count: > 0 } ? path.Average(p => p.X) : 0;
        var cy = path is { Count: > 0 } ? path.Average(p => p.Y) : 0;
        return new PanelFeature
        {
            FeatureId = (op.Op == "pocket" ? "PK" : "G") + id,
            Kind = kind,
            Purpose = purpose,
            FaceId = op.Through ? "THROUGH" : "A",
            Through = op.Through,
            X = cx,
            Y = cy,
            DepthMm = op.DepthMm,
            WidthMm = op.WidthMm,
            Path = path,
        };
    }

    static bool BelongsTo(CutOp op, IReadOnlyList<(double X, double Y)> outer)
    {
        if (op.Op == "drill")
        {
            var x = op.SheetX ?? op.X ?? 0;
            var y = op.SheetY ?? op.Y ?? 0;
            return Contains(outer, x, y);
        }
        var pts = op.FinishLoop ?? op.Path;
        if (pts is not { Count: > 0 }) return false;
        var c = (pts.Average(p => p.X), pts.Average(p => p.Y));
        return Contains(outer, c.Item1, c.Item2);
    }

    static bool CentroidInside(
        IReadOnlyList<(double X, double Y)> inner,
        IReadOnlyList<(double X, double Y)> outer)
    {
        var c = (inner.Average(p => p.X), inner.Average(p => p.Y));
        return Contains(outer, c.Item1, c.Item2);
    }

    internal static bool Contains(IReadOnlyList<(double X, double Y)> poly, double x, double y)
    {
        var n = poly.Count;
        if (n < 3) return false;
        var inside = false;
        var j = n - 1;
        for (var i = 0; i < n; i++)
        {
            var pi = poly[i];
            var pj = poly[j];
            if ((pi.Y > y) != (pj.Y > y)
                && x < (pj.X - pi.X) * (y - pi.Y) / ((pj.Y - pi.Y) + 1e-15) + pi.X)
                inside = !inside;
            j = i;
        }
        return inside;
    }

    static double Area(IReadOnlyList<(double X, double Y)> path)
    {
        var a = 0d;
        for (var i = 0; i < path.Count; i++)
        {
            var p = path[i];
            var q = path[(i + 1) % path.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a) * 0.5;
    }
}

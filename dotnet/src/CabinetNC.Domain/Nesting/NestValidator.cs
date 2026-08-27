namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;
using Clipper2Lib;

public sealed record NestCollision(string PanelIdA, string PanelIdB, int SheetIndex);

/// <summary>AABB fast check + Clipper2 polygon/gap verification.</summary>
public static class NestValidator
{
    const double Scale = 1000;

    public static IReadOnlyList<NestCollision> FindAabbCollisions(
        IReadOnlyList<NestPart> parts,
        IReadOnlyList<NestPlacement> placements,
        double spacingMm = 12,
        IReadOnlySet<(string A, string B)>? ignorePairs = null)
    {
        var byId = parts.ToDictionary(p => p.PanelId, p => p);
        var gap = Math.Max(0, spacingMm);
        var hits = new List<NestCollision>();
        var list = placements.ToList();

        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!byId.TryGetValue(a.PanelId, out var pa)) continue;
            var boxA = PlacementAabb(pa, a);
            for (var j = i + 1; j < list.Count; j++)
            {
                var b = list[j];
                if (a.SheetIndex != b.SheetIndex) continue;
                if (ignorePairs is not null && ignorePairs.Contains((a.PanelId, b.PanelId))) continue;
                if (!byId.TryGetValue(b.PanelId, out var pb)) continue;
                var boxB = PlacementAabb(pb, b);
                if (AabbsConflict(boxA, boxB, gap))
                    hits.Add(new NestCollision(a.PanelId, b.PanelId, a.SheetIndex));
            }
        }
        return hits;
    }

    public static IReadOnlyList<NestCollision> FindPolygonCollisions(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        double spacingMm = 0,
        IReadOnlySet<(string A, string B)>? ignorePairs = null)
    {
        var byId = panels.ToDictionary(p => p.PanelId, p => p);
        var paths = new Dictionary<string, Path64>(StringComparer.Ordinal);
        foreach (var place in placements)
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            paths[place.PanelId] = WorldPath(panel, place);
        }

        var hits = new List<NestCollision>();
        for (var i = 0; i < placements.Count; i++)
        {
            var a = placements[i];
            if (!paths.TryGetValue(a.PanelId, out var pathA)) continue;
            for (var j = i + 1; j < placements.Count; j++)
            {
                var b = placements[j];
                if (a.SheetIndex != b.SheetIndex) continue;
                if (ignorePairs is not null && ignorePairs.Contains((a.PanelId, b.PanelId))) continue;
                if (!paths.TryGetValue(b.PanelId, out var pathB)) continue;
                if (PolygonsConflict(pathA, pathB, spacingMm))
                    hits.Add(new(a.PanelId, b.PanelId, a.SheetIndex));
            }
        }
        return hits;
    }

    /// <summary>True-shape overlap of one moving pose against others on the same sheet.</summary>
    public static bool HasPolygonConflict(
        Panel moving,
        string panelId,
        double ox,
        double oy,
        double rotDeg,
        int sheetIndex,
        IReadOnlyDictionary<string, Panel> byId,
        IEnumerable<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        double spacingMm,
        IReadOnlySet<(string A, string B)>? ignorePairs = null)
    {
        var pathA = WorldPath(moving, new NestPlacement
        {
            PanelId = panelId,
            SheetIndex = sheetIndex,
            OffsetX = ox,
            OffsetY = oy,
            RotationDeg = rotDeg,
        });
        foreach (var op in others)
        {
            if (op.PanelId == panelId || op.SheetIndex != sheetIndex) continue;
            if (ignorePairs is not null && ignorePairs.Contains((panelId, op.PanelId))) continue;
            if (!byId.TryGetValue(op.PanelId, out var other)) continue;
            var pathB = WorldPath(other, new NestPlacement
            {
                PanelId = op.PanelId,
                SheetIndex = op.SheetIndex,
                OffsetX = op.Ox,
                OffsetY = op.Oy,
                RotationDeg = op.Rot,
            });
            if (PolygonsConflict(pathA, pathB, spacingMm))
                return true;
        }
        return false;
    }

    public static (double minX, double minY, double maxX, double maxY) PlacementAabb(NestPart part, NestPlacement place)
    {
        var rot = place.RotationDeg % 180;
        var w = Math.Abs(rot - 90) < 1e-6 || Math.Abs(rot + 90) < 1e-6 ? part.HeightMm : part.WidthMm;
        var h = Math.Abs(rot - 90) < 1e-6 || Math.Abs(rot + 90) < 1e-6 ? part.WidthMm : part.HeightMm;
        return (place.OffsetX, place.OffsetY, place.OffsetX + w, place.OffsetY + h);
    }

    public static bool AabbsConflict(
        (double minX, double minY, double maxX, double maxY) a,
        (double minX, double minY, double maxX, double maxY) b,
        double gapMm)
    {
        var g = Math.Max(0, gapMm);
        return !(a.maxX + g <= b.minX || b.maxX + g <= a.minX || a.maxY + g <= b.minY || b.maxY + g <= a.minY);
    }

    static bool PolygonsConflict(Path64 a, Path64 b, double spacingMm)
    {
        var gap = Math.Max(0, spacingMm);
        Paths64 aa = new() { a };
        Paths64 bb = new() { b };
        if (gap > 0)
        {
            var delta = gap * Scale / 2;
            aa = Clipper.InflatePaths(aa, delta, JoinType.Round, EndType.Polygon);
            bb = Clipper.InflatePaths(bb, delta, JoinType.Round, EndType.Polygon);
        }
        return Clipper.Intersect(aa, bb, FillRule.NonZero)
            .Any(p => Math.Abs(Clipper.Area(p)) > 1);
    }

    static Path64 WorldPath(Panel panel, NestPlacement place)
    {
        var result = new Path64(panel.Outline.Points.Count);
        var bounds = NestTransform.BoundsOf(panel);
        foreach (var p in panel.Outline.Points)
        {
            var (x, y) = NestTransform.ToSheet(
                p.X, p.Y, bounds,
                place.OffsetX, place.OffsetY, place.RotationDeg);
            result.Add(new Point64(
                (long)Math.Round(x * Scale),
                (long)Math.Round(y * Scale)));
        }
        return result;
    }
}

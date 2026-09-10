namespace CabinetNC.Domain.Nesting;

using Clipper2Lib;

/// <summary>
/// Convex decomposition for NFP: ear-clip then merge adjacent triangles
/// when the union stays convex (Hertel–Mehlhorn style).
/// </summary>
static class NfpConvexDecompose
{
    public const int MaxPieces = 12;

    public static List<Path64> Decompose(Path64 path)
    {
        var clean = NfpGeometry.Clean(path);
        if (clean.Count < 3) return [];
        if (NfpGeometry.IsConvex(clean)) return [clean];

        var tris = Triangulate(clean);
        if (tris.Count == 0) return [clean];
        var merged = MergeConvex(tris);
        return merged.Count == 0 ? [clean] : merged;
    }

    static List<Path64> Triangulate(Path64 poly)
    {
        var pts = new List<Point64>(poly.Count);
        foreach (var p in poly)
            pts.Add(p);
        if (pts.Count >= 2 && pts[0] == pts[^1])
            pts.RemoveAt(pts.Count - 1);

        var result = new List<Path64>();
        var guard = 0;
        while (pts.Count > 3 && guard++ < 4096)
        {
            var ear = -1;
            for (var i = 0; i < pts.Count; i++)
            {
                if (IsEar(pts, i))
                {
                    ear = i;
                    break;
                }
            }
            if (ear < 0) break;

            var n = pts.Count;
            result.Add([pts[(ear - 1 + n) % n], pts[ear], pts[(ear + 1) % n]]);
            pts.RemoveAt(ear);
        }

        if (pts.Count >= 3)
            result.Add(new Path64(pts));
        return result;
    }

    static bool IsEar(List<Point64> pts, int i)
    {
        var n = pts.Count;
        var prev = pts[(i - 1 + n) % n];
        var curr = pts[i];
        var next = pts[(i + 1) % n];
        if (Cross(prev, curr, next) <= 0) return false;

        for (var j = 0; j < n; j++)
        {
            if (j == i || j == (i - 1 + n) % n || j == (i + 1) % n) continue;
            if (PointInTriangle(pts[j], prev, curr, next))
                return false;
        }
        return true;
    }

    static bool PointInTriangle(Point64 p, Point64 a, Point64 b, Point64 c)
    {
        var c1 = Cross(a, b, p);
        var c2 = Cross(b, c, p);
        var c3 = Cross(c, a, p);
        return c1 >= 0 && c2 >= 0 && c3 >= 0;
    }

    static List<Path64> MergeConvex(List<Path64> pieces)
    {
        var list = new List<Path64>(pieces.Count);
        foreach (var p in pieces)
        {
            if (p.Count < 3) continue;
            list.Add(NfpGeometry.EnsurePositive(p));
        }

        var changed = true;
        var guard = 0;
        while (changed && guard++ < 256 && list.Count > 1)
        {
            changed = false;
            for (var i = 0; i < list.Count && !changed; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (!TryMerge(list[i], list[j], out var merged)) continue;
                    list.RemoveAt(j);
                    list[i] = merged;
                    changed = true;
                    break;
                }
            }
        }

        if (list.Count > MaxPieces)
            return list.Take(MaxPieces).ToList();
        return list;
    }

    static bool TryMerge(Path64 a, Path64 b, out Path64 merged)
    {
        merged = [];
        for (var i = 0; i < a.Count; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Count];
            for (var j = 0; j < b.Count; j++)
            {
                var b0 = b[j];
                var b1 = b[(j + 1) % b.Count];
                if (a0 != b1 || a1 != b0) continue;

                var walk = new Path64(a.Count + b.Count);
                for (var k = 0; k < a.Count; k++)
                    walk.Add(a[(i + 1 + k) % a.Count]);
                for (var k = 2; k < b.Count; k++)
                    walk.Add(b[(j + k) % b.Count]);
                if (walk.Count < 3) continue;

                var pos = NfpGeometry.EnsurePositive(walk);
                if (!NfpGeometry.IsConvex(pos)) continue;
                merged = pos;
                return true;
            }
        }
        return false;
    }

    static long Cross(Point64 a, Point64 b, Point64 c) =>
        (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
}

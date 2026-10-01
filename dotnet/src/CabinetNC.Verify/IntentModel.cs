namespace CabinetNC.Verify;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Clipper2Lib;

internal enum IntentKind
{
    Hole,
    Groove,
    Pocket,
    Cutout,
    Unknown,
}

/// <summary>One CAD feature expressed as regions in sheet millimetres.</summary>
internal sealed class FeatureIntent
{
    public required string FeatureId { get; init; }
    public required IntentKind Kind { get; init; }
    /// <summary>Region that must be removed (floor / opening). Groove end caps excluded.</summary>
    public required Paths64 Core { get; init; }
    /// <summary>Region where removal is permitted. Superset of <see cref="Core"/>.</summary>
    public required Paths64 Allowed { get; init; }
    public bool Through { get; init; }
    /// <summary>Depth from the board top. Null when the CAD gave none (unverifiable blind feature).</summary>
    public double? DepthMm { get; init; }
    /// <summary>Groove: sheet-space centreline; hole: null.</summary>
    public Path64? Centerline { get; init; }
    public double? WidthMm { get; init; }
    /// <summary>Hole: centre and Ø in sheet space.</summary>
    public (double X, double Y)? HoleCentre { get; init; }
    public double? HoleDiameterMm { get; init; }
    /// <summary>Why the feature could not be verified (warn), or null.</summary>
    public string? Unverifiable { get; init; }

    public bool IsVerifiable => Unverifiable is null && (Through || DepthMm is > 0);
}

internal sealed class PanelIntent
{
    public required string PanelId { get; init; }
    public required int Instance { get; init; }
    public required double ThicknessMm { get; init; }
    /// <summary>Finished outline in sheet space.</summary>
    public required Paths64 Outline { get; init; }
    public required IReadOnlyList<FeatureIntent> Features { get; init; }
    public required NestPlacement Placement { get; init; }

    public string Label => Instance == 0 ? PanelId : $"{PanelId}#{Instance + 1}";
}

/// <summary>Builds <see cref="PanelIntent"/>s from panels + placements. Own feature taxonomy — no CAM helpers.</summary>
internal static class IntentModel
{
    /// <summary>Half-width tolerated along a groove whose CAD width is unknown (one T2 diameter).</summary>
    const double UnknownGrooveHalfWidthMm = 10;

    public static IReadOnlyList<PanelIntent> Build(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        int sheetIndex,
        VerifyTolerances tol)
    {
        var byId = panels.GroupBy(p => p.PanelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new List<PanelIntent>();
        var instance = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var place in placements.Where(p => p.SheetIndex == sheetIndex))
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            if (panel.Outline.Points.Count < 3) continue;
            var n = instance.GetValueOrDefault(place.PanelId);
            instance[place.PanelId] = n + 1;
            result.Add(BuildPanel(panel, place, n, tol));
        }
        return result;
    }

    static PanelIntent BuildPanel(Panel panel, NestPlacement place, int instance, VerifyTolerances tol)
    {
        var bounds = NestTransform.BoundsOf(panel);
        (double X, double Y) Map(Point2 p) =>
            NestTransform.ToSheet(p.X, p.Y, bounds, place.OffsetX, place.OffsetY, place.RotationDeg);

        var outline = Geo.Polygon(panel.Outline.Points.Select(Map));
        var th = panel.ThicknessMm;
        var features = new List<FeatureIntent>();
        foreach (var f in panel.Features)
            features.Add(BuildFeature(f, th, Map, tol));

        return new PanelIntent
        {
            PanelId = panel.PanelId,
            Instance = instance,
            ThicknessMm = th,
            Outline = outline,
            Features = features,
            Placement = place,
        };
    }

    static FeatureIntent BuildFeature(
        PanelFeature f, double th, Func<Point2, (double X, double Y)> map, VerifyTolerances tol)
    {
        var kind = Classify(f);
        var through = f.Through || (f.DepthMm is double d0 && th > 0 && d0 >= th - 0.05);
        double? depth = through ? th : f.DepthMm is > 0 ? f.DepthMm : null;

        switch (kind)
        {
            case IntentKind.Hole:
            {
                var ring = f.Path is { Count: >= 3 } ? f.Path : f.Profile is { Count: >= 3 } ? f.Profile : null;
                var dia = f.DiameterMm is > 1e-9 ? f.DiameterMm.Value : 0;
                Paths64 region;
                if (ring is not null)
                    region = Geo.Polygon(ring.Select(map));
                else if (dia > 0)
                {
                    var (cx, cy) = map(new Point2(f.X, f.Y));
                    region = Geo.Disc(cx, cy, dia / 2, 48);
                }
                else
                    return Unverifiable(f, kind, "孔无直径也无轮廓");
                var centre = map(new Point2(f.X, f.Y));
                if (dia <= 0 && ring is not null)
                    dia = Geo.ShortSpanMm(region);
                // Holes with no depth are through (OpsPlanner: DepthMm ?? thickness).
                if (!through && depth is null)
                {
                    through = true;
                    depth = th;
                }
                return new FeatureIntent
                {
                    FeatureId = f.FeatureId,
                    Kind = kind,
                    Core = region,
                    Allowed = region,
                    Through = through,
                    DepthMm = depth,
                    HoleCentre = centre,
                    HoleDiameterMm = dia,
                    Unverifiable = Geo.ShortSpanMm(region) < tol.SliverMm ? "孔过小（薄带）" : null,
                };
            }

            case IntentKind.Groove:
            {
                if (f.Profile is { Count: >= 3 } profile && f.Path is not { Count: >= 2 })
                {
                    var region = Geo.Polygon(profile.Select(map));
                    return new FeatureIntent
                    {
                        FeatureId = f.FeatureId,
                        Kind = kind,
                        Core = region,
                        Allowed = region,
                        Through = through,
                        DepthMm = depth,
                        WidthMm = Geo.ShortSpanMm(region),
                        Unverifiable = Sliver(region, tol) ?? DepthMissing(through, depth),
                    };
                }
                if (f.Path is not { Count: >= 2 } path)
                    return Unverifiable(f, kind, "槽无中心线也无轮廓");

                var width = f.WidthMm is > 1e-9 ? f.WidthMm.Value
                    : f.Profile is { Count: >= 3 } prof ? ShortSpan(prof) : 0;
                var line = Geo.Path(path.Select(map));
                if (width <= 1e-9)
                {
                    // Cannot judge width; still tolerate a cut along the centreline so it is
                    // not reported as an overcut on bare board.
                    return new FeatureIntent
                    {
                        FeatureId = f.FeatureId,
                        Kind = kind,
                        Core = [],
                        Allowed = Geo.SweepRound(line, UnknownGrooveHalfWidthMm, tol.ArcChordMm),
                        Through = through,
                        DepthMm = depth,
                        Centerline = line,
                        Unverifiable = "槽宽未知（无 widthMm 也无 profile）",
                    };
                }

                if (line.Count == 2 && Length(line) * 1.5 < width)
                {
                    // Axis-swapped export: centreline is the short across-width stroke.
                    return Unverifiable(f, kind, $"槽中心线 {Length(line):0.##} 短于槽宽 {width:0.##}（疑似轴互换）");
                }
                var half = width / 2;
                var allowed = Geo.SweepRound(line, half, tol.ArcChordMm);
                if (f.Profile is { Count: >= 3 } p2)
                    allowed = Geo.Union(allowed, Geo.Polygon(p2.Select(map)));
                var trimmed = TrimEnds(line, half);
                var core = trimmed is { Count: >= 2 } ? Geo.StripButt(trimmed, half) : [];
                return new FeatureIntent
                {
                    FeatureId = f.FeatureId,
                    Kind = kind,
                    Core = core,
                    Allowed = allowed,
                    Through = through,
                    DepthMm = depth,
                    Centerline = trimmed,
                    WidthMm = width,
                    Unverifiable = width < tol.SliverMm ? "槽宽为薄带" : DepthMissing(through, depth),
                };
            }

            case IntentKind.Pocket:
            case IntentKind.Cutout:
            {
                var ring = f.Path is { Count: >= 3 } ? f.Path : f.Profile is { Count: >= 3 } ? f.Profile : null;
                if (ring is null)
                    return Unverifiable(f, kind, "无闭合轮廓");
                var region = Geo.Polygon(ring.Select(map));
                if (f.Holes is { Count: > 0 })
                {
                    foreach (var hole in f.Holes)
                    {
                        if (hole.Count < 3) continue;
                        var island = Geo.Polygon(hole.Select(map));
                        // Trust CAD islands that actually sit inside the pocket.
                        if (Geo.AreaMm2(Geo.Inter(island, region)) >= 0.7 * Geo.AreaMm2(island))
                            region = Geo.Diff(region, island);
                    }
                }
                if (kind == IntentKind.Cutout)
                {
                    through = true;
                    depth = th;
                }
                return new FeatureIntent
                {
                    FeatureId = f.FeatureId,
                    Kind = kind,
                    Core = region,
                    Allowed = region,
                    Through = through,
                    DepthMm = depth,
                    Unverifiable = Sliver(region, tol) ?? DepthMissing(through, depth),
                };
            }

            default:
            {
                if (through && (f.Path is { Count: >= 3 } || f.Profile is { Count: >= 3 }))
                {
                    var ring = f.Path is { Count: >= 3 } ? f.Path! : f.Profile!;
                    var region = Geo.Polygon(ring.Select(map));
                    return new FeatureIntent
                    {
                        FeatureId = f.FeatureId,
                        Kind = IntentKind.Cutout,
                        Core = region,
                        Allowed = region,
                        Through = true,
                        DepthMm = th,
                        Unverifiable = Sliver(region, tol),
                    };
                }
                return Unverifiable(f, kind, $"未知特征类型 {f.Kind}");
            }
        }
    }

    static IntentKind Classify(PanelFeature f)
    {
        var k = f.Kind ?? "";
        if (k.Contains("hole", StringComparison.OrdinalIgnoreCase)) return IntentKind.Hole;
        if (k.Contains("groove", StringComparison.OrdinalIgnoreCase)) return IntentKind.Groove;
        if (k.Contains("pocket", StringComparison.OrdinalIgnoreCase)) return IntentKind.Pocket;
        if (k.Contains("cutout", StringComparison.OrdinalIgnoreCase)) return IntentKind.Cutout;
        return IntentKind.Unknown;
    }

    static FeatureIntent Unverifiable(PanelFeature f, IntentKind kind, string why) => new()
    {
        FeatureId = f.FeatureId,
        Kind = kind,
        Core = [],
        Allowed = [],
        Through = f.Through,
        DepthMm = f.DepthMm,
        Unverifiable = why,
    };

    static string? Sliver(Paths64 region, VerifyTolerances tol) =>
        region.Count == 0 ? "轮廓退化" : Geo.ShortSpanMm(region) < tol.SliverMm ? "薄带（导出残片）" : null;

    static string? DepthMissing(bool through, double? depth) =>
        !through && depth is not > 0 ? "盲特征缺少深度" : null;

    static double ShortSpan(IReadOnlyList<Point2> pts)
    {
        var minX = pts.Min(p => p.X);
        var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxY = pts.Max(p => p.Y);
        return Math.Min(maxX - minX, maxY - minY);
    }

    static double Length(Path64 line)
    {
        var len = 0d;
        for (var i = 1; i < line.Count; i++)
            len += Dist(line[i - 1], line[i]);
        return len / Geo.Scale;
    }

    static double Dist(Point64 a, Point64 b)
    {
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Shorten an open polyline by <paramref name="mm"/> at both ends. Null when too short.</summary>
    static Path64? TrimEnds(Path64 line, double mm)
    {
        var cut = mm * Geo.Scale;
        var pts = line.ToList();
        if (pts.Count < 2) return null;
        var total = 0d;
        for (var i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);
        if (total <= 2 * cut + 1) return null;

        var front = Advance(pts, cut);
        front.Reverse();
        var back = Advance(front, cut);
        back.Reverse();
        return back.Count >= 2 ? new Path64(back) : null;

        static List<Point64> Advance(List<Point64> src, double along)
        {
            var remaining = along;
            var i = 0;
            while (i < src.Count - 1)
            {
                var d = Dist(src[i], src[i + 1]);
                if (d >= remaining)
                {
                    var t = d < 1e-9 ? 0 : remaining / d;
                    var start = new Point64(
                        (long)Math.Round(src[i].X + (src[i + 1].X - src[i].X) * t),
                        (long)Math.Round(src[i].Y + (src[i + 1].Y - src[i].Y) * t));
                    var outPts = new List<Point64> { start };
                    outPts.AddRange(src.Skip(i + 1));
                    return outPts;
                }
                remaining -= d;
                i++;
            }
            return [src[^1]];
        }
    }
}

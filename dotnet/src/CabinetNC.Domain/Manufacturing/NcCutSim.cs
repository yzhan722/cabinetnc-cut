namespace CabinetNC.Domain.Manufacturing;

/// <summary>Time a G-code replay so the export canvas can scrub cutter motion.</summary>
public static class NcCutSim
{
    public const double RapidMmMin = 24000;
    public const double DefaultFeedMmMin = 1000;

    public enum StrokeKind
    {
        Rapid,
        Leave,
        Through,
    }

    public sealed record Pose(
        double X,
        double Y,
        double Z,
        int StrokeIndex,
        double Along,
        bool Rapid,
        int ToolNum,
        double Feed,
        bool Done);

    public static double LengthMm(ToolStroke s)
    {
        var xy = ArcLengthXy(s);
        return Math.Sqrt(xy * xy + s.Dz * s.Dz);
    }

    public static double ArcLengthXy(ToolStroke s)
    {
        if (s.Arc && s.R is double r && r > 1e-6
            && OsaiTroyParser.TryArcSweep(s.X0, s.Y0, s.X1, s.Y1, r, s.Cw, out _, out _, out _, out var sweep))
            return Math.Abs(r * sweep);
        return s.XyLen;
    }

    public static (double X, double Y, double Z) PointAlong(ToolStroke s, double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (s.Arc && s.R is double r && r > 1e-6
            && OsaiTroyParser.TryArcSweep(s.X0, s.Y0, s.X1, s.Y1, r, s.Cw, out var cx, out var cy, out var a0, out var sweep))
        {
            var a = a0 + sweep * t;
            return (cx + Math.Abs(r) * Math.Cos(a), cy + Math.Abs(r) * Math.Sin(a), s.Z0 + s.Dz * t);
        }
        return (s.X0 + s.Dx * t, s.Y0 + s.Dy * t, s.Z0 + s.Dz * t);
    }

    public static double DurationSec(ToolStroke s)
    {
        var len = LengthMm(s);
        if (len < 1e-9) return 0;
        var feed = s.Rapid
            ? RapidMmMin
            : s.Feed > 1 ? s.Feed : DefaultFeedMmMin;
        return len / feed * 60.0;
    }

    public static double TotalSec(IReadOnlyList<ToolStroke> strokes)
    {
        var t = 0d;
        foreach (var s in strokes)
            t += DurationSec(s);
        return t;
    }

    public static StrokeKind KindOf(ToolStroke s, double safeZMm = TroyRecipe.SafeZMm)
    {
        var z = Math.Min(s.Z0, s.Z1);
        if (s.Rapid || z >= safeZMm - 8)
            return StrokeKind.Rapid;
        if (z >= 0.25 && z <= 0.85)
            return StrokeKind.Leave;
        return StrokeKind.Through;
    }

    public static double ToolDiameterMm(int toolNum, IReadOnlyDictionary<int, double>? shop = null)
    {
        if (shop is not null && shop.TryGetValue(toolNum, out var shopDia) && shopDia > 0)
            return shopDia;
        if (ToolCatalog.DefaultMap().TryGetValue("T" + toolNum, out var t) && t.DiameterMm > 0)
            return t.DiameterMm;
        return toolNum == 3 ? 3 : toolNum == 1 ? TroyRecipe.TongueDiameterMm : TroyRecipe.WorkDiameterMm;
    }

    /// <summary>Export-sim stroke width in px: rapids stay thin; cuts are true tool diameter.</summary>
    public static float CutStrokeWidthPx(
        int toolNum,
        float scalePxPerMm,
        bool rapid,
        IReadOnlyDictionary<int, double>? shop = null)
    {
        if (rapid) return 1.15f;
        var dia = ToolDiameterMm(toolNum, shop);
        return Math.Max(1.2f, (float)(dia * Math.Max(0, scalePxPerMm)));
    }

    public static Pose At(IReadOnlyList<ToolStroke> strokes, double tSec)
    {
        if (strokes.Count == 0)
            return new Pose(0, 0, 0, -1, 0, true, 2, 0, true);

        var first = strokes[0];
        if (tSec <= 0)
            return new Pose(first.X0, first.Y0, first.Z0, 0, 0, first.Rapid, first.ToolNum, first.Feed, false);

        var acc = 0d;
        for (var i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            var d = DurationSec(s);
            var last = i == strokes.Count - 1;
            if (tSec > acc + d && !last)
            {
                acc += d;
                continue;
            }

            var along = d > 1e-12 ? Math.Clamp((tSec - acc) / d, 0, 1) : 1;
            if (last && tSec >= acc + d)
                along = 1;
            var p = PointAlong(s, along);
            return new Pose(
                p.X,
                p.Y,
                p.Z,
                i,
                along,
                s.Rapid,
                s.ToolNum,
                s.Feed,
                last && along >= 1);
        }

        var end = strokes[^1];
        return new Pose(end.X1, end.Y1, end.Z1, strokes.Count - 1, 1, end.Rapid, end.ToolNum, end.Feed, true);
    }
}

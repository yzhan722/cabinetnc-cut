namespace CabinetNC.Domain.Manufacturing;

/// <summary>Replay OSAI-Troy motion into cutter-center strokes (L1).</summary>
public sealed class ToolStroke
{
    public int ToolNum { get; init; } = 2;
    public double Rpm { get; init; }
    public bool Rapid { get; init; }
    public bool Arc { get; init; }
    public bool Cw { get; init; }
    public double? R { get; init; }
    public double X0 { get; init; }
    public double Y0 { get; init; }
    public double Z0 { get; init; }
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double Z1 { get; init; }
    public double Feed { get; init; }
    public double Dx => X1 - X0;
    public double Dy => Y1 - Y0;
    public double Dz => Z1 - Z0;
    public double XyLen => Math.Sqrt(Dx * Dx + Dy * Dy);
}

public sealed class OsaiReplay
{
    public IReadOnlyList<OsaiLine> Lines { get; init; } = [];
    public IReadOnlyList<ToolStroke> Strokes { get; init; } = [];
    public double SafeZMm { get; init; } = TroyRecipe.SafeZMm;
}

public static class OsaiTroyParser
{
    public static OsaiReplay Replay(string text) =>
        Replay(OsaiTroyLexer.CutProgram(OsaiTroyLexer.Lex(text)));

    public static OsaiReplay Replay(IReadOnlyList<OsaiLine> cutLines)
    {
        var strokes = new List<ToolStroke>();
        double x = 0, y = 0, z = 0, f = 0, s = 0;
        var tool = 2;
        var motion = 0;
        var maxRapidZ = 0d;

        foreach (var line in cutLines)
        {
            if (line.IsComment || line.Words.Count == 0)
                continue;

            var g = (double?)null;
            var m = (double?)null;
            double? nx = null, ny = null, nz = null, nf = null, nr = null, ns = null;
            int? nt = null;
            foreach (var w in line.Words)
            {
                switch (w.Letter)
                {
                    case 'G': g = w.Number; break;
                    case 'M': m = w.Number; break;
                    case 'X': nx = w.Number; break;
                    case 'Y': ny = w.Number; break;
                    case 'Z': nz = w.Number; break;
                    case 'F': nf = w.Number; break;
                    case 'R': nr = w.Number; break;
                    case 'T': nt = (int)Math.Round(w.Number); break;
                    case 'S': ns = w.Number; break;
                }
            }

            if (m is double mv)
            {
                var mi = (int)Math.Round(mv);
                if (mi == 6)
                {
                    if (nt is int tn && tn > 0) tool = tn;
                }
                else if (mi == 3)
                {
                    if (ns is double sv) s = sv;
                }
                continue;
            }

            if (g is double gv)
            {
                var gi = (int)Math.Round(gv);
                if (gi is 0 or 1 or 2 or 3)
                    motion = gi;
                if (gi is 79 or 80 or 90 or 40 or 17 or 27)
                {
                    if (gi == 79 && nz is double z79)
                        z = z79;
                    continue;
                }
            }

            if (nf is double fv) f = fv;
            if (ns is double rpm) s = rpm;
            if (nt is int tOnly && tOnly > 0 && g is null && m is null)
                tool = tOnly;

            if (nx is null && ny is null && nz is null)
                continue;
            if (motion is not (0 or 1 or 2 or 3))
                continue;

            var x1 = nx ?? x;
            var y1 = ny ?? y;
            var z1 = nz ?? z;
            var rapid = motion == 0;
            if (rapid && z1 > maxRapidZ)
                maxRapidZ = z1;

            if (motion is 2 or 3 && nr is double r && r > 1e-6)
            {
                strokes.Add(new ToolStroke
                {
                    ToolNum = tool,
                    Rpm = s,
                    Rapid = false,
                    Arc = true,
                    Cw = motion == 2,
                    R = r,
                    X0 = x,
                    Y0 = y,
                    Z0 = z,
                    X1 = x1,
                    Y1 = y1,
                    Z1 = z1,
                    Feed = f,
                });
                x = x1;
                y = y1;
                z = z1;
                continue;
            }

            if (Math.Abs(x1 - x) < 1e-7 && Math.Abs(y1 - y) < 1e-7 && Math.Abs(z1 - z) < 1e-7)
                continue;

            strokes.Add(new ToolStroke
            {
                ToolNum = tool,
                Rpm = s,
                Rapid = rapid,
                X0 = x,
                Y0 = y,
                Z0 = z,
                X1 = x1,
                Y1 = y1,
                Z1 = z1,
                Feed = f,
            });
            x = x1;
            y = y1;
            z = z1;
        }

        var safeZ = maxRapidZ >= 15 ? maxRapidZ : TroyRecipe.SafeZMm;
        return new OsaiReplay { Lines = cutLines, Strokes = strokes, SafeZMm = safeZ };
    }

    /// <summary>Sample a G2/G3 for reverse/infer only. Simulation keeps the arc intact.</summary>
    public static IReadOnlyList<(double X, double Y)> TessellateArc(
        double x0, double y0, double x1, double y1, double r, bool cw)
    {
        if (!TryArcSweep(x0, y0, x1, y1, r, cw, out var cx, out var cy, out var a0, out var sweep))
            return [(x1, y1)];

        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 10)), 2, 24);
        var pts = new List<(double X, double Y)>(steps);
        var rr = Math.Abs(r);
        for (var i = 1; i <= steps; i++)
        {
            var a = a0 + sweep * (i / (double)steps);
            pts.Add(i == steps
                ? (x1, y1)
                : (cx + rr * Math.Cos(a), cy + rr * Math.Sin(a)));
        }
        return pts;
    }

    public static bool TryArcSweep(
        double x0, double y0, double x1, double y1, double r, bool cw,
        out double cx, out double cy, out double a0, out double sweep)
    {
        a0 = sweep = 0;
        if (!TryArcCenter(x0, y0, x1, y1, r, cw, out cx, out cy))
            return false;

        a0 = Math.Atan2(y0 - cy, x0 - cx);
        var a1 = Math.Atan2(y1 - cy, x1 - cx);
        sweep = a1 - a0;
        if (cw)
        {
            while (sweep > 0) sweep -= 2 * Math.PI;
            while (sweep < -2 * Math.PI - 1e-9) sweep += 2 * Math.PI;
        }
        else
        {
            while (sweep < 0) sweep += 2 * Math.PI;
            while (sweep > 2 * Math.PI + 1e-9) sweep -= 2 * Math.PI;
        }
        return Math.Abs(sweep) >= 1e-9;
    }

    internal static bool TryArcCenter(
        double x0, double y0, double x1, double y1, double r, bool cw,
        out double cx, out double cy)
    {
        cx = cy = 0;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var d = Math.Sqrt(dx * dx + dy * dy);
        var rr = Math.Abs(r);
        if (d < 1e-9 || d > 2 * rr + 1e-4)
            return false;
        var h2 = rr * rr - (d * 0.5) * (d * 0.5);
        var h = Math.Sqrt(Math.Max(0, h2));
        var mx = (x0 + x1) * 0.5;
        var my = (y0 + y1) * 0.5;
        var ux = -dy / d;
        var uy = dx / d;
        var c1x = mx + ux * h;
        var c1y = my + uy * h;
        var c2x = mx - ux * h;
        var c2y = my - uy * h;
        if (IsCw(c1x, c1y, x0, y0, x1, y1) == cw)
        {
            cx = c1x;
            cy = c1y;
            return true;
        }
        cx = c2x;
        cy = c2y;
        return true;
    }

    static bool IsCw(double cx, double cy, double x0, double y0, double x1, double y1)
    {
        var ax = x0 - cx;
        var ay = y0 - cy;
        var bx = x1 - cx;
        var by = y1 - cy;
        return ax * by - ay * bx < 0;
    }
}

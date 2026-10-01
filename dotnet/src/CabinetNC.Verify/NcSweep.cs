namespace CabinetNC.Verify;

using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;
using Clipper2Lib;

/// <summary>
/// Material actually removed by the emitted programs: every cutting stroke swept by
/// its tool disc, bucketed by Z. Built from <see cref="OsaiTroyParser"/> strokes only —
/// no planner geometry.
/// </summary>
internal sealed class NcSweep
{
    /// <summary>Contiguous cutter-centre polyline at one Z (deepest Z of any ramp inside it).</summary>
    internal sealed class Chain(Path64 line, double z, double radiusMm, int toolNum, int firstLine)
    {
        public Path64 Line { get; } = line;
        public double Z { get; } = z;
        public double RadiusMm { get; } = radiusMm;
        public int ToolNum { get; } = toolNum;
        public int FirstLine { get; } = firstLine;
        Paths64? _sweep;
        public Paths64 Sweep(double arcChordMm) => _sweep ??= Geo.SweepRound(Line, RadiusMm, arcChordMm);
    }

    /// <summary>A vertical (or near-vertical) entry: drill cycle or pocket plunge.</summary>
    internal sealed record Plunge(double X, double Y, double Z, double RadiusMm, int ToolNum, int Line);

    readonly List<Chain> _chains = [];
    readonly List<Plunge> _plunges = [];
    readonly Dictionary<long, Paths64> _cache = [];
    readonly VerifyTolerances _tol;
    readonly ZDatum _datum;

    public IReadOnlyList<Plunge> Plunges => _plunges;
    public IReadOnlyList<Chain> Chains => _chains;
    public int StrokeCount { get; private set; }
    /// <summary>Largest radius of any cutting tool (mills and drills).</summary>
    public double MaxToolRadiusMm { get; private set; }
    /// <summary>Largest radius that ran an XY cutting move — the fillet a milled inside corner can have.</summary>
    public double MaxMillRadiusMm { get; private set; }
    public IReadOnlyCollection<int> UnknownTools => _unknownTools;
    readonly HashSet<int> _unknownTools = [];

    public NcSweep(
        IEnumerable<VerifyProgram> programs,
        IReadOnlyDictionary<string, ToolDefinition> tools,
        ZDatum datum,
        VerifyTolerances tol)
    {
        _tol = tol;
        _datum = datum;
        foreach (var prog in programs)
            Ingest(WithToolWords(prog), tools);
    }

    static readonly System.Text.RegularExpressions.Regex ToolComment =
        new(@"^\s*(?:N\d+\s*)?\(\s*tool\s+T?(\d+)\s*\)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// The generic post has no <c>M6 T</c>; tool changes are <c>(tool T3)</c> comments and the
    /// Sheet×Tool split tells us the program's tool. Turn both into words the parser reads.
    /// </summary>
    static string WithToolWords(VerifyProgram prog)
    {
        var text = ToolComment.Replace(prog.NcText, m => "M6 T" + m.Groups[1].Value);
        if (prog.ToolId is { } id && TryToolNum(id) is int n && n > 0)
            text = "M6 T" + n + "\n" + text;
        return text;
    }

    void Ingest(string text, IReadOnlyDictionary<string, ToolDefinition> tools)
    {
        var replay = OsaiTroyParser.Replay(text);
        StrokeCount += replay.Strokes.Count;

        Path64? line = null;
        var lineZ = 0d;
        var lineR = 0d;
        var lineTool = 0;
        var lineStart = -1;

        void Flush()
        {
            if (line is { Count: >= 2 })
            {
                _chains.Add(new Chain(line, lineZ, lineR, lineTool, lineStart));
                if (lineR > MaxMillRadiusMm) MaxMillRadiusMm = lineR;
            }
            line = null;
        }

        foreach (var s in replay.Strokes)
        {
            if (s.Rapid)
            {
                Flush();
                continue;
            }

            var r = RadiusOf(s.ToolNum, tools);
            if (r <= 0)
            {
                _unknownTools.Add(s.ToolNum);
                Flush();
                continue;
            }
            if (r > MaxToolRadiusMm) MaxToolRadiusMm = r;

            var stationary = s.XyLen < 1e-6 && !s.Arc;
            var zDeep = Math.Min(s.Z0, s.Z1);
            var zChanges = Math.Abs(s.Dz) > 1e-6;

            if (stationary)
            {
                // Plunge / retract at one XY. Retracts (upwards) cut nothing new.
                if (s.Z1 < s.Z0 - 1e-6)
                    _plunges.Add(new Plunge(s.X1, s.Y1, s.Z1, r, s.ToolNum, s.LineIndex));
                Flush();
                continue;
            }

            if (zChanges)
            {
                // Ramp: treat the whole XY run as cut at its deepest Z (conservative for
                // overcut; closed loops come back over the ramp at full depth anyway).
                Flush();
                var ramp = new Path64 { Geo.P(s.X0, s.Y0) };
                ramp.AddRange(SamplePath(s));
                _chains.Add(new Chain(ramp, zDeep, r, s.ToolNum, s.LineIndex));
                if (r > MaxMillRadiusMm) MaxMillRadiusMm = r;
                continue;
            }

            var sameChain = line is not null
                && Math.Abs(lineZ - s.Z0) < 1e-6
                && lineTool == s.ToolNum
                && line[^1] == Geo.P(s.X0, s.Y0);
            if (!sameChain)
            {
                Flush();
                line = new Path64 { Geo.P(s.X0, s.Y0) };
                lineZ = s.Z0;
                lineR = r;
                lineTool = s.ToolNum;
                lineStart = s.LineIndex;
            }
            line!.AddRange(SamplePath(s));
        }
        Flush();
    }

    IEnumerable<Point64> SamplePath(ToolStroke s)
    {
        if (!s.Arc || s.R is not double r || r <= 1e-6
            || !OsaiTroyParser.TryArcSweep(s.X0, s.Y0, s.X1, s.Y1, r, s.Cw, out var cx, out var cy, out var a0, out var sweep))
        {
            yield return Geo.P(s.X1, s.Y1);
            yield break;
        }

        var rr = Math.Abs(r);
        var chord = Math.Min(_tol.ArcChordMm, rr * 0.5);
        var stepAngle = 2 * Math.Acos(Math.Clamp(1 - chord / rr, -1, 1));
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / Math.Max(stepAngle, 1e-3)), 2, 2000);
        for (var i = 1; i <= steps; i++)
        {
            if (i == steps)
            {
                yield return Geo.P(s.X1, s.Y1);
                break;
            }
            var a = a0 + sweep * (i / (double)steps);
            yield return Geo.P(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a));
        }
    }

    static double RadiusOf(int toolNum, IReadOnlyDictionary<string, ToolDefinition> tools)
    {
        if (tools.TryGetValue("T" + toolNum, out var def) && def.DiameterMm > 0)
            return def.DiameterMm / 2;
        foreach (var t in tools.Values)
        {
            if (TryToolNum(t.ToolId) == toolNum && t.DiameterMm > 0)
                return t.DiameterMm / 2;
        }
        return 0;
    }

    static int? TryToolNum(string id)
    {
        var s = id.Trim();
        if (s.Length >= 2 && (s[0] is 'T' or 't') && int.TryParse(s.AsSpan(1), out var n)) return n;
        return int.TryParse(s, out var raw) ? raw : null;
    }

    /// <summary>Program Z that reaches <paramref name="depthFromTopMm"/> in a board of <paramref name="thicknessMm"/>.</summary>
    public double ZFor(double depthFromTopMm, double thicknessMm) =>
        _datum == ZDatum.BoardBottom ? thicknessMm - depthFromTopMm : -depthFromTopMm;

    public double DepthOf(double z, double thicknessMm) =>
        _datum == ZDatum.BoardBottom ? thicknessMm - z : -z;

    /// <summary>Union of everything cut at or below program Z (with Z tolerance).</summary>
    public Paths64 RemovedAtOrBelowZ(double zMax)
    {
        var key = (long)Math.Round((zMax + _tol.ZMm) * 1000);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var limit = zMax + _tol.ZMm;
        var parts = new List<Paths64>();
        foreach (var c in _chains)
        {
            if (c.Z > limit) continue;
            parts.Add(c.Sweep(_tol.ArcChordMm));
        }
        foreach (var p in _plunges)
        {
            if (p.Z > limit) continue;
            parts.Add(Geo.Disc(p.X, p.Y, p.RadiusMm));
        }
        var union = Geo.Union(parts);
        _cache[key] = union;
        return union;
    }

    /// <summary>Removed to at least <paramref name="depthFromTopMm"/> for a board of this thickness.</summary>
    public Paths64 RemovedToDepth(double depthFromTopMm, double thicknessMm) =>
        RemovedAtOrBelowZ(ZFor(depthFromTopMm, thicknessMm));

    /// <summary>Everything the tool touched at any cutting depth below the board top.</summary>
    public Paths64 RemovedAny(double thicknessMm) =>
        RemovedToDepth(_tol.SurfaceDepthMm, thicknessMm);

    /// <summary>Smallest mill radius that cut inside <paramref name="region"/>; null when nothing did.</summary>
    public double? MillRadiusInside(Paths64 region)
    {
        double? best = null;
        if (region.Count == 0) return null;
        var (minX, minY, maxX, maxY) = Geo.Bounds(region);
        foreach (var c in _chains)
        {
            if (best is double b && c.RadiusMm >= b) continue;
            var (cx0, cy0, cx1, cy1) = Geo.Bounds(c.Sweep(_tol.ArcChordMm));
            if (cx1 < minX || cx0 > maxX || cy1 < minY || cy0 > maxY) continue;
            if (Geo.AreaMm2(Geo.Inter(c.Sweep(_tol.ArcChordMm), region)) > 1e-6)
                best = c.RadiusMm;
        }
        return best;
    }

    /// <summary>Deepest cut whose sweep overlaps <paramref name="region"/>, as depth-from-top.</summary>
    public double? MaxDepthInside(Paths64 region, double thicknessMm)
    {
        double? best = null;
        foreach (var c in _chains)
        {
            var d = DepthOf(c.Z, thicknessMm);
            if (best is double b && d <= b) continue;
            if (Geo.AreaMm2(Geo.Inter(c.Sweep(_tol.ArcChordMm), region)) > 1e-6)
                best = d;
        }
        foreach (var p in _plunges)
        {
            var d = DepthOf(p.Z, thicknessMm);
            if (best is double b && d <= b) continue;
            if (Geo.AreaMm2(Geo.Inter(Geo.Disc(p.X, p.Y, p.RadiusMm), region)) > 1e-6)
                best = d;
        }
        return best;
    }
}

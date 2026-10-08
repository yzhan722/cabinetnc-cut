namespace CabinetNC.Domain.Manufacturing;

using System.Globalization;
using System.Text;

/// <summary>
/// 新代开料机。画面仍是 X 向右、Y 向上，零件靠在右下角，外形不翻转。
/// 机床原点在那一角，X 正方向向左，所以程序里 X' = 板宽 − X，Y 不变。
/// 轴反过来、数字也反过来，板上的形状和刀路方向与画面一致。Z0 在台面。贴标不写进 NC。
/// </summary>
public static class SyntecPost
{
    public const string MachineId = "syntec_e4_1330";

    public const double ThroughZMm = -0.10;
    public const double RetractZMm = 38;
    public const double RapidFeed = 12000;
    public const double PlungeFeed = 8000;
    public const double EntryFeed = 3000;
    public const double CutFeed = 12000;
    public const int SpindleRpm = 18000;

    /// <summary>Machine X measured leftward from the right edge. Y is unchanged.</summary>
    public static (double X, double Y) ToMachine(double sheetX, double sheetY, double sheetWidthMm) =>
        (sheetWidthMm - sheetX, sheetY);

    public static string EmitNc(
        IEnumerable<CutOp> ops,
        double sheetWidthMm,
        double sheetThicknessMm)
    {
        var lines = new List<string>
        {
            "G54",
            "T1",
            "M03 S" + SpindleRpm.ToString(CultureInfo.InvariantCulture),
            "G43 H1",
        };

        foreach (var op in CamSafety.OrderSafe(ops.Where(o => o.Placed && o.Enabled && o.Op != "drill")))
        {
            var top = TopZ(op, sheetThicknessMm);
            var cut = CutZ(op, top);
            if (op.Op == "pocket")
            {
                if (op.PathSegments is { Count: > 0 } segments)
                {
                    foreach (var seg in segments)
                        EmitPath(lines, seg, closed: false, inner: false, sheetWidthMm, top, cut);
                }
                if (op.FinishLoop is { Count: >= 3 } finish)
                    EmitPath(lines, finish, closed: true, inner: true, sheetWidthMm, top, cut);
                continue;
            }

            if (op.Path is { Count: >= 2 } path)
            {
                var closed = op.ClosePath && path.Count >= 3;
                var inner = op.Op == "contour" && !string.IsNullOrWhiteSpace(op.FeatureId);
                EmitPath(lines, path, closed, inner, sheetWidthMm, top, cut);
            }
        }

        lines.Add("M05");
        lines.Add("M30");
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static string PlateStem(int ordinal, double thicknessMm) =>
        Math.Max(1, ordinal).ToString("000", CultureInfo.InvariantCulture) + "-" + ThicknessToken(thicknessMm);

    public static string ThicknessToken(double thicknessMm)
    {
        var v = Math.Round(Math.Abs(thicknessMm), 1, MidpointRounding.AwayFromZero);
        if (Math.Abs(v - Math.Round(v)) < 0.001)
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        return v.ToString("0.0", CultureInfo.InvariantCulture);
    }

    static void EmitPath(
        List<string> lines,
        IReadOnlyList<(double X, double Y)> path,
        bool closed,
        bool inner,
        double sheetWidthMm,
        double topZ,
        double cutZ)
    {
        var pts = Round2(path);
        if (pts.Count >= 2 && Dist(pts[0], pts[^1]) < 0.02)
            pts.RemoveAt(pts.Count - 1);
        if (pts.Count == 0) return;
        // Climb is decided on the sheet, then X is rewritten for the machine.
        // Reversing again would climb the other way on the part.
        if (closed && pts.Count >= 3)
            pts = ClimbCut.OrientClosed(pts, inner).ToList();
        pts = pts.Select(p => ToMachine(p.X, p.Y, sheetWidthMm)).ToList();

        var p0 = pts[0];
        lines.Add($"G00 X{Xy(p0.X)} Y{Xy(p0.Y)} F{Feed(RapidFeed)}");
        lines.Add($"G01 Z{Xy(topZ)} F{Feed(PlungeFeed)}");
        lines.Add($"G01 Z{Xy(cutZ)} F{Feed(EntryFeed)}");
        for (var i = 1; i < pts.Count; i++)
        {
            if (Dist(pts[i - 1], pts[i]) < 0.02) continue;
            lines.Add($"G01 X{Xy(pts[i].X)} Y{Xy(pts[i].Y)} F{Feed(CutFeed)}");
        }
        if (closed && pts.Count >= 3 && Dist(pts[^1], p0) >= 0.02)
            lines.Add($"G01 X{Xy(p0.X)} Y{Xy(p0.Y)} F{Feed(CutFeed)}");
        lines.Add($"G00 Z{Xy(RetractZMm)} F{Feed(RapidFeed)}");
    }

    static List<(double X, double Y)> Round2(IReadOnlyList<(double X, double Y)> path) =>
        path.Select(p => (
                Math.Round(p.X, 2, MidpointRounding.AwayFromZero),
                Math.Round(p.Y, 2, MidpointRounding.AwayFromZero)))
            .ToList();

    static double TopZ(CutOp op, double sheetThicknessMm)
    {
        var th = op.ThicknessMm is > 0 ? op.ThicknessMm.Value : sheetThicknessMm;
        return th > 0 ? th : sheetThicknessMm > 0 ? sheetThicknessMm : 18;
    }

    static double CutZ(CutOp op, double topZ)
    {
        if (op.Op is "contour" or "remnant" || op.Through)
            return ThroughZMm;
        var depth = Math.Abs(op.DepthMm ?? topZ);
        if (topZ > 0 && depth >= topZ - 0.05)
            return ThroughZMm;
        var z = topZ - depth;
        return z < ThroughZMm ? ThroughZMm : z;
    }

    static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static string Xy(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    static string Feed(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Machine strokes back into sheet coordinates, so the nest-view simulation
    /// draws the same picture as the layout. Mirroring X reverses arc sense.
    /// </summary>
    public static IReadOnlyList<ToolStroke> ToSheet(IReadOnlyList<ToolStroke> machine, double sheetWidthMm) =>
        machine.Select(s => new ToolStroke
        {
            ToolNum = s.ToolNum,
            Rpm = s.Rpm,
            Rapid = s.Rapid,
            Arc = s.Arc,
            Cw = s.Arc ? !s.Cw : s.Cw,
            R = s.R,
            X0 = sheetWidthMm - s.X0,
            Y0 = s.Y0,
            Z0 = s.Z0,
            X1 = sheetWidthMm - s.X1,
            Y1 = s.Y1,
            Z1 = s.Z1,
            Feed = s.Feed,
            LineIndex = s.LineIndex,
        }).ToList();
}

public sealed class SyntecSheet
{
    public required string NcFileName { get; init; }
    public required string NcText { get; init; }
    public required double WidthMm { get; init; }
    public required double LengthMm { get; init; }
    public required double ThicknessMm { get; init; }
    public required string Color { get; init; }
    public IReadOnlyList<LabelPaste> Labels { get; init; } = [];
}

public sealed record SyntecFile(string RelativePath, string Text, bool Utf16);

public sealed record SyntecBitmap(string RelativePath, LabelPaste Paste);

/// <summary>One job folder: <c>1.xml</c>, <c>cnc/*.nc</c>, <c>label/*.cyc</c> and part bitmaps.</summary>
public static class SyntecBundle
{
    public static (IReadOnlyList<SyntecFile> Files, IReadOnlyList<SyntecBitmap> Bitmaps) Build(
        IReadOnlyList<SyntecSheet> sheets)
    {
        var files = new List<SyntecFile>();
        var bitmaps = new List<SyntecBitmap>();
        var list = new StringBuilder();
        list.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        list.Append("<CycleFile>\r\n");

        foreach (var sheet in sheets)
        {
            var stem = PathStem(sheet.NcFileName);
            files.Add(new SyntecFile("cnc/" + sheet.NcFileName, sheet.NcText, Utf16: false));
            var cycName = "Label_" + stem + ".cyc";
            files.Add(new SyntecFile(
                "label/" + cycName,
                LabelCyc(stem, sheet),
                Utf16: true));

            list.Append("  <Cycle Name=\"Cycle_List\">\r\n");
            Field(list, "PlateID", sheet.NcFileName);
            Field(list, "LabelName", cycName);
            Field(list, "LargeImage", "W" + OrdinalOf(stem) + ".bmp");
            Field(list, "SmallImage", "T" + OrdinalOf(stem) + ".bmp");
            Field(list, "Color", sheet.Color);
            Field(list, "Length", Plain(sheet.LengthMm));
            Field(list, "Width", Plain(sheet.WidthMm));
            Field(list, "Thickness", SyntecPost.ThicknessToken(sheet.ThicknessMm));
            list.Append("  </Cycle>\r\n");

            var n = 0;
            foreach (var paste in sheet.Labels)
            {
                if (string.IsNullOrWhiteSpace(paste.Stem) && string.IsNullOrWhiteSpace(paste.PanelId))
                    continue;
                n++;
                var bmp = stem + "_" + n.ToString("0000", CultureInfo.InvariantCulture) + ".bmp";
                bitmaps.Add(new SyntecBitmap("label/" + bmp, paste));
            }
        }

        list.Append("</CycleFile>\r\n");
        files.Insert(0, new SyntecFile("1.xml", list.ToString(), Utf16: false));
        return (files, bitmaps);
    }

    static string LabelCyc(string stem, SyntecSheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n");
        sb.Append("<CycleFile>\r\n");
        var n = 0;
        foreach (var paste in sheet.Labels)
        {
            if (string.IsNullOrWhiteSpace(paste.Stem) && string.IsNullOrWhiteSpace(paste.PanelId))
                continue;
            n++;
            var (x, y) = SyntecPost.ToMachine(paste.SheetX, paste.SheetY, sheet.WidthMm);
            var bmp = stem + "_" + n.ToString("0000", CultureInfo.InvariantCulture) + ".bmp";
            sb.Append("  <Cycle Name=\"Cycle_Label\">\r\n");
            Field(sb, "LabelName", bmp);
            Field(sb, "X", Coord(x));
            Field(sb, "Y", Coord(y));
            Field(sb, "R", "0");
            sb.Append("  </Cycle>\r\n");
        }
        sb.Append("</CycleFile>\r\n");
        return sb.ToString();
    }

    static void Field(StringBuilder sb, string name, string value)
    {
        sb.Append("    <Field Name=\"").Append(name).Append("\" Value=\"").Append(Esc(value)).Append("\"/>\r\n");
    }

    static string PathStem(string ncFileName)
    {
        var name = ncFileName.Trim();
        if (name.EndsWith(".nc", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];
        return name;
    }

    static string OrdinalOf(string stem)
    {
        var dash = stem.IndexOf('-');
        var head = dash > 0 ? stem[..dash] : stem;
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n.ToString(CultureInfo.InvariantCulture)
            : "1";
    }

    static string Plain(double v)
    {
        var r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
        if (Math.Abs(r - Math.Round(r)) < 0.001)
            return Math.Round(r).ToString("0", CultureInfo.InvariantCulture);
        return r.ToString("0.0", CultureInfo.InvariantCulture);
    }

    static string Coord(double v)
    {
        var r = Math.Round(v, 2, MidpointRounding.AwayFromZero);
        return r.ToString("0.##", CultureInfo.InvariantCulture);
    }

    static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal);
    }
}

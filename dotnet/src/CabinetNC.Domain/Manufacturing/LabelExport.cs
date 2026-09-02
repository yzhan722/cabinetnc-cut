namespace CabinetNC.Domain.Manufacturing;

using System.Globalization;
using System.Text;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

/// <summary>One paste: LS11 stem, sheet-space centre, artwork fields.</summary>
public sealed class LabelPaste
{
    public required string PanelId { get; init; }
    public required string Stem { get; init; }
    public int SheetIndex { get; init; }
    public double SheetX { get; init; }
    public double SheetY { get; init; }
    public string Title { get; init; } = "";
    public string Group { get; init; } = "";
    public string? Material { get; init; }
    public double ThicknessMm { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public bool FitsComfortably { get; init; }
}

/// <summary>OSAI Process 2 (LS11 + M701/M702) and safe bmp stems.</summary>
public static class LabelExport
{
    public const int StemMaxLen = 28;

    public static IReadOnlyList<LabelPaste> Build(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        IReadOnlyDictionary<string, (double X, double Y)>? overrides = null,
        Func<Panel, string>? materialTitle = null)
    {
        var byId = panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<LabelPaste>();
        var n = 0;
        foreach (var place in placements
                     .OrderBy(p => p.SheetIndex)
                     .ThenBy(p => p.PanelId, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            n++;
            var bounds = NestTransform.BoundsOf(panel);
            (double X, double Y)? ov = overrides is not null
                && overrides.TryGetValue(panel.PanelId, out var o)
                ? o
                : null;
            var anchor = LabelAnchorFinder.Find(panel, place.RotationDeg, ov);
            var (sx, sy) = NestTransform.ToSheet(
                anchor.LocalX, anchor.LocalY, bounds,
                place.OffsetX, place.OffsetY, place.RotationDeg);
            var stem = UniqueStem(panel, place.SheetIndex, n, used);
            list.Add(new LabelPaste
            {
                PanelId = panel.PanelId,
                Stem = stem,
                SheetIndex = place.SheetIndex,
                SheetX = sx,
                SheetY = sy,
                Title = string.IsNullOrWhiteSpace(panel.DisplayPartName)
                    ? panel.DisplayTitle
                    : panel.DisplayPartName,
                Group = panel.DisplayGroup,
                Material = materialTitle?.Invoke(panel) ?? panel.MaterialGroupLabel,
                ThicknessMm = panel.ThicknessMm,
                WidthMm = Math.Max(0, bounds.MaxX - bounds.MinX),
                HeightMm = Math.Max(0, bounds.MaxY - bounds.MinY),
                FitsComfortably = anchor.FitsComfortably,
            });
        }
        return list;
    }

    public static string SafeStem(string? raw, int max = StemMaxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '\'' or '"' or '`' or '’' or '‘')
                continue;
            if (char.IsAsciiLetterOrDigit(ch))
                sb.Append(ch);
            else if (ch is '_' or '-')
                sb.Append('_');
            else if (char.IsWhiteSpace(ch) || ch is '.' or '/' or '\\' or '·' or '+' or '&')
            {
                if (sb.Length > 0 && sb[^1] != '_')
                    sb.Append('_');
            }
        }
        var s = sb.ToString().Trim('_');
        if (s.Length > max)
            s = s[..max].Trim('_');
        return s;
    }

    public static string EmitPro2(IReadOnlyList<LabelPaste> pastes)
    {
        var sb = new StringBuilder();
        sb.Append("\"PRO2\"\r\n");
        sb.Append("M50\r\n");
        sb.Append("#@PSS=0\r\n");
        sb.Append("#@PEE=0\r\n");
        sb.Append("G90 G10\r\n");
        sb.Append("M700\r\n");
        sb.Append("G90 G10\r\n");
        sb.Append("M703\r\n");
        sb.Append("(UAO,1)\r\n");
        sb.Append("G90 G0\r\n");
        var i = 1;
        foreach (var p in pastes)
        {
            var st = "ST" + i.ToString("00", CultureInfo.InvariantCulture);
            sb.Append('"').Append(st).Append("\"\r\n");
            sb.Append("(DIS,\"").Append(p.Stem).Append("\")\r\n");
            sb.Append("LS11='").Append(p.Stem).Append("'\r\n");
            sb.Append("M701\r\n");
            sb.Append("(GTO,").Append(st).Append(",E41=0)\r\n");
            sb.Append("G90 G0 V").Append(Fmt(p.SheetY)).Append(" U").Append(Fmt(p.SheetX)).Append("\r\n");
            sb.Append("M702\r\n");
            sb.Append("(GTO,").Append(st).Append(",E42=0)\r\n");
            sb.Append("(DIS,\"\")\r\n");
            i++;
        }
        sb.Append("\"END\"\r\n");
        sb.Append("G90 G00\r\n");
        sb.Append("M704\r\n");
        sb.Append("M02\r\n");
        sb.Append("M30\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Every <c>LS11='stem'</c> the labeler will request, in program order, duplicates kept.
    /// The shop label software resolves the stem to <c>&lt;picture dir&gt;\stem.bmp</c>.
    /// </summary>
    public static IReadOnlyList<string> Ls11Stems(string? anc)
    {
        var stems = new List<string>();
        if (string.IsNullOrEmpty(anc)) return stems;
        foreach (var raw in anc.Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf("LS11=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var rest = line[(i + 5)..].Trim();
            if (rest.Length >= 2 && rest[0] == '\'')
            {
                var end = rest.IndexOf('\'', 1);
                if (end > 1)
                    stems.Add(rest[1..end]);
            }
        }
        return stems;
    }

    /// <summary>
    /// Stems the program asks for that have no bitmap among <paramref name="availableStems"/>
    /// (file names without <c>.bmp</c>, case-insensitive). Non-empty means the machine would
    /// block inside <c>M701</c> waiting for a picture that does not exist — the 2026-08-19 incident.
    /// </summary>
    public static IReadOnlyList<string> MissingBitmaps(string? anc, IEnumerable<string> availableStems)
    {
        var have = new HashSet<string>(availableStems, StringComparer.OrdinalIgnoreCase);
        return Ls11Stems(anc)
            .Where(s => !have.Contains(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string WrapCutWithLabelProcess(string cutNc, string pro2)
    {
        var cut = (cutNc ?? "").TrimStart('\uFEFF', '\r', '\n');
        var sb = new StringBuilder();
        sb.Append(";SELECT PROCESS\r\n");
        sb.Append("(GTO,PRO1,!PROC(0)=1) ;JUMP TO MAIN PROGRAM\r\n");
        sb.Append("(GTO,PRO2,!PROC(0)=2) ;JUMP TO LABEL PASTING\r\n");
        sb.Append(pro2);
        if (!pro2.EndsWith("\r\n", StringComparison.Ordinal))
            sb.Append("\r\n");
        sb.Append("\"PRO1\"\r\n");
        sb.Append(cut);
        if (!cut.EndsWith("\n", StringComparison.Ordinal))
            sb.Append("\r\n");
        return sb.ToString();
    }

    static string UniqueStem(Panel panel, int sheetIndex, int n, HashSet<string> used)
    {
        var group = SafeStem(panel.DisplayGroup);
        var part = SafeStem(string.IsNullOrWhiteSpace(panel.DisplayPartName)
            ? panel.DisplayTitle
            : panel.DisplayPartName);
        var raw = group.Length > 0 && part.Length > 0 &&
                  !group.Equals(part, StringComparison.OrdinalIgnoreCase)
            ? $"{group}_{part}"
            : part.Length > 0 ? part : SafeStem(panel.PanelId);
        if (raw.Length < 2)
            raw = $"{sheetIndex + 1}_{n}";
        if (raw.Length > StemMaxLen)
            raw = raw[..StemMaxLen].Trim('_');
        var stem = raw;
        var k = 2;
        while (!used.Add(stem))
        {
            var suffix = "_" + k.ToString(CultureInfo.InvariantCulture);
            var head = raw.Length + suffix.Length > StemMaxLen
                ? raw[..Math.Max(1, StemMaxLen - suffix.Length)].Trim('_')
                : raw;
            stem = head + suffix;
            k++;
        }
        return stem;
    }

    static string Fmt(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}

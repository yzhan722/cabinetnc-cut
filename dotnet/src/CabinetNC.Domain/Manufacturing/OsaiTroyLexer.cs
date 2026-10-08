namespace CabinetNC.Domain.Manufacturing;

using System.Globalization;
using System.Text;

/// <summary>Tokenize OSAI-Troy .anc / .nc lines (N-words, G/M, labels, parentheticals).</summary>
public readonly record struct OsaiWord(char Letter, double Number);

public sealed class OsaiLine
{
    public int? N { get; init; }
    public required string Raw { get; init; }
    public string? Label { get; init; }
    public string? Paren { get; init; }
    public IReadOnlyList<OsaiWord> Words { get; init; } = [];
    public bool IsComment { get; init; }
    /// <summary>0-based line in the original text (blank lines counted) so a viewer can highlight it.</summary>
    public int SourceLine { get; init; } = -1;
}

public static class OsaiTroyLexer
{
    public static IReadOnlyList<OsaiLine> Lex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lines = new List<OsaiLine>();
        var index = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = ParseLine(raw, index);
            if (line is not null)
                lines.Add(line);
            index++;
        }
        return lines;
    }

    /// <summary>Keep Process 1 (cut). Drop PRO2 label-paste and GTO process jumps.</summary>
    public static IReadOnlyList<OsaiLine> CutProgram(IReadOnlyList<OsaiLine> lines)
    {
        var hasPro1 = lines.Any(l => IsLabel(l, "PRO1"));
        if (!hasPro1)
            return lines.Where(l => !IsLabel(l, "PRO2") && !IsGto(l)).ToList();

        var kept = new List<OsaiLine>();
        var inPro1 = false;
        foreach (var line in lines)
        {
            if (line.Label is not null)
            {
                inPro1 = IsLabel(line, "PRO1");
                continue;
            }
            if (IsGto(line)) continue;
            if (inPro1)
                kept.Add(line);
        }
        return kept;
    }

    static bool IsLabel(OsaiLine line, string name) =>
        line.Label is not null
        && line.Label.Equals(name, StringComparison.OrdinalIgnoreCase);

    static bool IsGto(OsaiLine line) =>
        line.Paren is not null
        && line.Paren.StartsWith("GTO", StringComparison.OrdinalIgnoreCase);

    static OsaiLine? ParseLine(string raw, int sourceLine)
    {
        var t = raw.Trim();
        if (t.Length == 0) return null;
        if (t[0] == ';')
            return new OsaiLine { Raw = raw, IsComment = true, SourceLine = sourceLine };

        if (t[0] == '"')
        {
            var end = t.IndexOf('"', 1);
            var label = end > 1 ? t[1..end] : t.Trim('"');
            return new OsaiLine { Raw = raw, Label = label, SourceLine = sourceLine };
        }

        var n = (int?)null;
        var i = 0;
        if (t[0] is 'N' or 'n')
        {
            i = 1;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            if (i > 1
                && int.TryParse(t.AsSpan(1, i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nv))
                n = nv;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        }

        if (i < t.Length && t[i] == '(')
        {
            var close = t.LastIndexOf(')');
            var body = close > i ? t[(i + 1)..close] : t[(i + 1)..];
            return new OsaiLine { Raw = raw, N = n, Paren = body.Trim(), SourceLine = sourceLine };
        }

        var words = new List<OsaiWord>();
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i]) || t[i] == '/')
            {
                i++;
                continue;
            }
            if (t[i] == ';')
                break;
            if (t[i] == '(')
            {
                var close = t.IndexOf(')', i);
                i = close < 0 ? t.Length : close + 1;
                continue;
            }
            var letter = char.ToUpperInvariant(t[i]);
            if (letter is < 'A' or > 'Z')
            {
                i++;
                continue;
            }
            i++;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            var start = i;
            if (i < t.Length && t[i] is '+' or '-') i++;
            var sawDot = false;
            while (i < t.Length)
            {
                var ch = t[i];
                if (char.IsDigit(ch)) { i++; continue; }
                if (ch == '.' && !sawDot) { sawDot = true; i++; continue; }
                break;
            }
            var num = 0d;
            if (i > start)
                double.TryParse(t.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out num);
            words.Add(new OsaiWord(letter, num));
        }

        return new OsaiLine { Raw = raw, N = n, Words = words, SourceLine = sourceLine };
    }

    public static string WordDump(OsaiLine line)
    {
        var sb = new StringBuilder();
        foreach (var w in line.Words)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(w.Letter);
            sb.Append(w.Number.ToString("0.####", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

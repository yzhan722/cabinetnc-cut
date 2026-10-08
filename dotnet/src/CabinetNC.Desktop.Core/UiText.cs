namespace CabinetNC.Desktop.Core;

/// <summary>
/// Display language. Chinese text stays in the source; English is applied when
/// <see cref="English"/> is on. <see cref="ToEnglish"/> always translates, for paper and labels.
/// </summary>
public static class UiText
{
    static readonly Dictionary<char, (string Zh, string En)[]> Buckets = BuildBuckets();

    static readonly Dictionary<char, string> Punct = new()
    {
        ['，'] = ", ",
        ['。'] = ". ",
        ['：'] = ": ",
        ['；'] = "; ",
        ['？'] = "?",
        ['！'] = "!",
        ['、'] = ", ",
        ['（'] = " (",
        ['）'] = ")",
        ['「'] = " \"",
        ['」'] = "\"",
        ['『'] = " \"",
        ['』'] = "\"",
        ['《'] = "\"",
        ['》'] = "\"",
        ['—'] = " — ",
        ['～'] = "~",
        ['\u3000'] = " ",
    };

    public static bool English { get; private set; }

    public static event Action? Changed;

    public static void SetEnglish(bool english)
    {
        if (English == english) return;
        English = english;
        Changed?.Invoke();
    }

    /// <summary>Chinese when the UI is Chinese; English when the UI is English.</summary>
    public static string T(string? text) =>
        !English || string.IsNullOrEmpty(text) ? text ?? "" : ToEnglish(text);

    /// <summary>English regardless of the UI language. Used for labels and job sheets.</summary>
    public static string ToEnglish(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (UiLexicon.Exact.TryGetValue(text, out var exact)) return exact;
        if (text.Contains('\n') || text.Contains('\r'))
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                lines[i] = TranslateLine(lines[i].TrimEnd('\r'));
            return string.Join("\n", lines);
        }
        return TranslateLine(text);
    }

    static string TranslateLine(string text)
    {
        if (UiLexicon.Exact.TryGetValue(text, out var exact)) return exact;
        if (!HasCjk(text)) return text;
        var parts = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (Buckets.TryGetValue(ch, out var bucket))
            {
                (string Zh, string En)? hit = null;
                foreach (var term in bucket)
                {
                    if (text.AsSpan(i).StartsWith(term.Zh))
                    {
                        hit = term;
                        break;
                    }
                }
                if (hit is { } found)
                {
                    Append(parts, found.En);
                    i += found.Zh.Length;
                    continue;
                }
            }
            if (Punct.TryGetValue(ch, out var punct))
            {
                if (punct == " (" && parts.Count == 0) punct = "(";
                parts.Add(punct);
                i++;
                continue;
            }
            parts.Add(ch.ToString());
            i++;
        }
        var joined = string.Concat(parts);
        while (joined.Contains("  ", StringComparison.Ordinal))
            joined = joined.Replace("  ", " ", StringComparison.Ordinal);
        return joined;
    }

    static void Append(List<string> parts, string en)
    {
        if (en.Length == 0) return;
        if (parts.Count > 0)
        {
            var prev = parts[^1];
            if (prev.Length > 0 && char.IsLetterOrDigit(prev[^1]) && char.IsLetterOrDigit(en[0]))
                parts.Add(" ");
        }
        parts.Add(en);
    }

    public static bool HasCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff') return true;
        }
        return false;
    }

    static Dictionary<char, (string Zh, string En)[]> BuildBuckets()
    {
        var map = new Dictionary<char, List<(string Zh, string En)>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in UiLexicon.Terms)
        {
            if (term.Zh.Length == 0 || !seen.Add(term.Zh)) continue;
            if (!map.TryGetValue(term.Zh[0], out var list))
            {
                list = [];
                map[term.Zh[0]] = list;
            }
            list.Add(term);
        }
        return map.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderByDescending(t => t.Zh.Length).ToArray());
    }
}

namespace CabinetNC.Infrastructure.Diagnostics;

using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Append-only Desktop usage log for offline debugging (mirrors Fusion plugin_usage).
/// Writes:
///   %LocalAppData%/CabinetNC/logs/app_usage.jsonl
///   %LocalAppData%/CabinetNC/logs/app_usage_latest.json
/// and mirrors into the repo <c>logs/</c> folder when running from a source tree
/// so agents can read the same paths as the Fusion plugin.
/// </summary>
public static class UsageLog
{
    const string FileStem = "app_usage";
    const long MaxJsonlBytes = 4L * 1024 * 1024;
    const int MaxListItems = 40;
    const int MaxString = 500;
    const int KeepLinesWhenTrim = 800;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };
    static readonly JsonSerializerOptions PrettyOpts = new()
    {
        WriteIndented = true,
    };

    public static string AppDataLogDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CabinetNC",
            "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string? RepoLogDir()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            {
                var dotnet = Path.Combine(dir.FullName, "dotnet");
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(dotnet) && (Directory.Exists(git) || File.Exists(Path.Combine(dir.FullName, "README.md"))))
                {
                    var logs = Path.Combine(dir.FullName, "logs");
                    Directory.CreateDirectory(logs);
                    return logs;
                }
            }
        }
        catch
        {
            /* ignore */
        }
        return null;
    }

    public static IReadOnlyList<string> LogDirs()
    {
        var dirs = new List<string> { AppDataLogDir() };
        var repo = RepoLogDir();
        if (!string.IsNullOrWhiteSpace(repo) &&
            !dirs.Any(d => string.Equals(d, repo, StringComparison.OrdinalIgnoreCase)))
            dirs.Add(repo!);
        return dirs;
    }

    public static IReadOnlyDictionary<string, string> Paths(string? logDir = null)
    {
        var dir = logDir ?? AppDataLogDir();
        return new Dictionary<string, string>
        {
            ["dir"] = dir,
            ["jsonl"] = Path.Combine(dir, $"{FileStem}.jsonl"),
            ["latest"] = Path.Combine(dir, $"{FileStem}_latest.json"),
        };
    }

    /// <summary>Append one usage event. Never throws into UI.</summary>
    public static JsonObject LogEvent(
        string kind,
        string action = "",
        object? payload = null,
        string? error = null,
        IEnumerable<KeyValuePair<string, object?>>? extra = null)
    {
        var eventObj = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
            ["tLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["kind"] = kind ?? "event",
            ["action"] = action ?? "",
            ["payload"] = ToNode(payload),
            ["error"] = string.IsNullOrWhiteSpace(error) ? null : error,
            ["paths"] = ToNode(Paths()),
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                if (eventObj.ContainsKey(key)) continue;
                eventObj[key] = ToNode(value);
            }
        }

        try
        {
            lock (Gate)
            {
                var line = eventObj.ToJsonString(JsonOpts);
                var pretty = eventObj.ToJsonString(PrettyOpts);
                foreach (var dir in LogDirs())
                {
                    var paths = Paths(dir);
                    File.AppendAllText(paths["jsonl"], line + Environment.NewLine);
                    File.WriteAllText(paths["latest"], pretty);
                    TrimJsonlIfHuge(paths["jsonl"]);
                }
            }
        }
        catch
        {
            /* never break the app for logging */
        }

        return eventObj;
    }

    public static void LogActionStart(string action, object? payload = null) =>
        LogEvent("action_start", action, payload);

    public static void LogActionResult(string action, object? payload = null, string? error = null) =>
        LogEvent(error is null ? "action_result" : "action_error", action, payload, error);

    public static Dictionary<string, object?> SummarizeImport(
        bool ok,
        string sourceLabel,
        PackageImportSummary? summary = null,
        string? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["source"] = sourceLabel,
            ["extra"] = string.IsNullOrWhiteSpace(extra) ? null : extra,
        };
        if (summary is not null)
        {
            payload["schemaName"] = summary.SchemaName;
            payload["version"] = summary.Version;
            payload["jobId"] = summary.JobId;
            payload["units"] = summary.Units;
            payload["panelCount"] = summary.PanelCount;
            payload["sheetCount"] = summary.SheetCount;
            payload["featureCount"] = summary.FeatureCount;
            payload["materialKinds"] = summary.MaterialKinds;
            payload["errorCount"] = summary.ErrorCount;
            payload["warningCount"] = summary.WarningCount;
            payload["errors"] = summary.Errors;
            payload["warnings"] = summary.Warnings;
        }
        return payload;
    }

    /// <summary>Public for tests — same sanitizer used in log payloads.</summary>
    public static object? JsonSafe(object? value) =>
        ToNode(value)?.Deserialize<object?>() ?? value;

    public static JsonNode? ToNode(object? value, int depth = 0)
    {
        if (depth > 8) return "<max-depth>";
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case JsonElement je:
                return JsonNode.Parse(je.GetRawText());
            case bool b:
                return b;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return JsonValue.Create(Convert.ToInt64(value));
            case float or double or decimal:
                return JsonValue.Create(Convert.ToDouble(value));
            case string s:
                return s.Length > MaxString ? s[..MaxString] + "…" : s;
            case DateTime dt:
                return dt.ToString("o");
            case DateTimeOffset dto:
                return dto.ToString("o");
            case Enum e:
                return e.ToString();
        }

        if (value is IDictionary dict)
        {
            var obj = new JsonObject();
            foreach (DictionaryEntry entry in dict)
            {
                var key = Convert.ToString(entry.Key) ?? "";
                if (key is "tempBody" or "body" or "occurrence" or "component" or "entity")
                    continue;
                obj[key] = ToNode(entry.Value, depth + 1);
            }
            return obj;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var arr = new JsonArray();
            var count = 0;
            var total = 0;
            foreach (var item in enumerable)
            {
                total++;
                if (count < MaxListItems)
                {
                    arr.Add(ToNode(item, depth + 1));
                    count++;
                }
            }
            if (total > MaxListItems)
                arr.Add(new JsonObject { ["_truncated"] = total - MaxListItems });
            return arr;
        }

        try
        {
            return JsonSerializer.SerializeToNode(value, value.GetType(), JsonOpts);
        }
        catch
        {
            return new JsonObject
            {
                ["_type"] = value.GetType().Name,
                ["value"] = Truncate(Convert.ToString(value) ?? ""),
            };
        }
    }

    static string Truncate(string s) =>
        s.Length > MaxString ? s[..MaxString] + "…" : s;

    static void TrimJsonlIfHuge(string jsonlPath)
    {
        try
        {
            if (!File.Exists(jsonlPath)) return;
            if (new FileInfo(jsonlPath).Length <= MaxJsonlBytes) return;
            var lines = File.ReadAllLines(jsonlPath);
            var keep = lines.Length > KeepLinesWhenTrim
                ? lines[^KeepLinesWhenTrim..]
                : lines;
            File.WriteAllLines(jsonlPath, keep);
        }
        catch
        {
            /* ignore */
        }
    }
}

/// <summary>Compact import fields for usage log payloads.</summary>
public sealed class PackageImportSummary
{
    public string? SchemaName { get; init; }
    public string? Version { get; init; }
    public string? JobId { get; init; }
    public string? Units { get; init; }
    public int PanelCount { get; init; }
    public int SheetCount { get; init; }
    public int FeatureCount { get; init; }
    public int MaterialKinds { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

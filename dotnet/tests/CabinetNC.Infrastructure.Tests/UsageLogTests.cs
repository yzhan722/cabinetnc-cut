using System.Text.Json.Nodes;
using CabinetNC.Infrastructure.Diagnostics;

namespace CabinetNC.Infrastructure.Tests;

public class UsageLogTests
{
    [Fact]
    public void ToNode_truncates_long_strings_and_lists()
    {
        var longText = new string('x', 600);
        var list = Enumerable.Range(0, 50).Select(i => $"item-{i}").ToList();
        var node = UsageLog.ToNode(new Dictionary<string, object?>
        {
            ["text"] = longText,
            ["items"] = list,
        }) as JsonObject;

        Assert.NotNull(node);
        var text = node!["text"]!.GetValue<string>();
        Assert.EndsWith("…", text);
        Assert.True(text.Length < 600);

        var items = Assert.IsType<JsonArray>(node["items"]);
        Assert.True(items.Count <= 41);
        Assert.Contains(items, x => x is JsonObject o && o.ContainsKey("_truncated"));
    }

    [Fact]
    public void LogEvent_writes_latest_and_jsonl()
    {
        var dir = UsageLog.AppDataLogDir();
        var latest = Path.Combine(dir, "app_usage_latest.json");
        var jsonl = Path.Combine(dir, "app_usage.jsonl");
        var before = File.Exists(jsonl) ? new FileInfo(jsonl).Length : 0L;

        UsageLog.LogActionResult(
            "test.usageLog",
            new Dictionary<string, object?> { ["ok"] = true, ["panelCount"] = 3 });

        Assert.True(File.Exists(latest));
        var text = File.ReadAllText(latest);
        Assert.Contains("test.usageLog", text);
        Assert.Contains("action_result", text);
        Assert.Contains("panelCount", text);
        Assert.Contains("3", text);
        Assert.True(new FileInfo(jsonl).Length > before);
    }
}

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

    [Fact]
    public void LogEvent_merges_session_context()
    {
        UsageLog.SetContextProvider(() => new Dictionary<string, object?>
        {
            ["jobId"] = "ctx-job",
            ["stage"] = "ops",
            ["panelCount"] = 4,
        });
        try
        {
            UsageLog.LogEvent("ui", "test.ctx", new Dictionary<string, object?> { ["ok"] = true });
            var latest = Path.Combine(UsageLog.AppDataLogDir(), "app_usage_latest.json");
            var text = File.ReadAllText(latest);
            Assert.Contains("ctx-job", text);
            Assert.Contains("\"stage\"", text);
            Assert.Contains("panelCount", text);
        }
        finally
        {
            UsageLog.SetContextProvider(null);
        }
    }

    [Fact]
    public void LogEvent_survives_throwing_context_provider()
    {
        UsageLog.SetContextProvider(() => throw new InvalidOperationException("ctx"));
        try
        {
            var ev = UsageLog.LogEvent("ui", "test.ctx.safe");
            Assert.Equal("ui", ev["kind"]!.GetValue<string>());
            Assert.Null(ev["ctx"]);
        }
        finally
        {
            UsageLog.SetContextProvider(null);
        }
    }

    [Fact]
    public void SummarizePreflight_keeps_codes_and_reason()
    {
        var payload = UsageLog.SummarizePreflight(
            ok: false,
            reason: "export",
            issueCodes: ["no_ops", "missing_tool_id"],
            issueCount: 2,
            files: ["S1_T1.nc"]);

        Assert.False((bool)payload["ok"]!);
        Assert.Equal("export", payload["reason"]);
        Assert.Equal(2, payload["issueCount"]);
        var issues = Assert.IsAssignableFrom<IEnumerable<string>>(payload["issues"]);
        Assert.Contains("no_ops", issues);
    }
}

namespace CabinetNC.Desktop.Core;

/// <summary>What the shell knows about the job right now; every field is a plain fact.</summary>
public readonly record struct WorkflowFacts(
    string Stage,
    bool InProduction,
    bool HasPackage,
    bool HasNest,
    bool HasOps,
    bool HasNc,
    bool ManufacturingDirty);

public readonly record struct StepView(string Id, string Label, bool Done, bool Stale, bool Current, string Hint);

/// <summary>Everything the production shell derives from <see cref="WorkflowFacts"/>.</summary>
public sealed record WorkflowView(
    IReadOnlyList<StepView> Steps,
    bool LaterStagesEnabled,
    bool OneClickExportEnabled,
    bool ShowStaleBanner,
    bool ShowNestAwaiting,
    bool ShowOpsAwaiting,
    bool ShowOutAwaiting,
    string OutAwaitingTarget);

/// <summary>
/// The five-step production workflow as rules, not as scattered if-statements. The shell
/// asks once per refresh and applies the answer to tabs, pills, banner and empty states.
/// </summary>
public static class WorkflowRules
{
    public static readonly string[] StageIds = ["load", "stock", "nest", "ops", "out"];

    public static int StageIndex(string stage) => stage switch
    {
        "load" => 0, "stock" => 1, "nest" => 2, "ops" => 3, "out" => 4, _ => 0,
    };

    public static WorkflowView Evaluate(WorkflowFacts f)
    {
        var stale = f.ManufacturingDirty && (f.HasNest || f.HasNc);
        var steps = new List<StepView>
        {
            new("load", "载入", f.HasPackage, false, f.Stage == "load", f.HasPackage ? "方案已载入" : "尚未载入方案"),
            new("stock", "板材", f.HasPackage, false, f.Stage == "stock", f.HasPackage ? "板材参数可用" : "先载入方案"),
            new("nest", "密排", f.HasNest, stale, f.Stage == "nest", stale ? "板件已修改，需要重新密排" : f.HasNest ? "密排完成" : "尚未密排"),
            new("ops", "刀路", f.HasOps, stale, f.Stage == "ops", stale ? "板件已修改，需要重新密排" : f.HasOps ? "刀路已计算" : "尚未计算刀路"),
            new("out", "导出", f.HasNc, stale, f.Stage == "out", stale ? "板件已修改，需要重新密排" : f.HasNc ? "程序文件就绪" : "尚无程序文件"),
        };

        var awaitingNest = f.HasPackage && !f.HasNest;
        return new WorkflowView(
            Steps: steps,
            LaterStagesEnabled: f.HasPackage,
            OneClickExportEnabled: f.HasNest && f.HasNc,
            ShowStaleBanner: f.InProduction && f.HasPackage && f.ManufacturingDirty && (f.HasNest || f.HasNc) && f.Stage != "load",
            ShowNestAwaiting: f.Stage == "nest" && awaitingNest,
            ShowOpsAwaiting: f.Stage == "ops" && awaitingNest,
            ShowOutAwaiting: f.Stage == "out" && f.HasPackage && !f.HasNc,
            OutAwaitingTarget: f.HasNest ? "ops" : "nest");
    }
}

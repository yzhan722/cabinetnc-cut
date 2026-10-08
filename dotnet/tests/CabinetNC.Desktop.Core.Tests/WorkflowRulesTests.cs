using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class WorkflowRulesTests
{
    static WorkflowFacts Facts(string stage = "load", bool pkg = false, bool nest = false, bool ops = false, bool nc = false, bool dirty = false, bool production = true) =>
        new(stage, production, pkg, nest, ops, nc, dirty);

    [Fact]
    public void Without_a_package_only_the_load_step_is_reachable()
    {
        var v = WorkflowRules.Evaluate(Facts());
        Assert.False(v.LaterStagesEnabled);
        Assert.All(v.Steps, s => Assert.False(s.Done));
        Assert.False(v.ShowStaleBanner);
        Assert.False(v.OneClickExportEnabled);
        Assert.False(v.ShowNestAwaiting);
        Assert.False(v.ShowOutAwaiting);
    }

    [Fact]
    public void Fresh_package_marks_load_and_stock_done_and_waits_for_a_nest()
    {
        var v = WorkflowRules.Evaluate(Facts("nest", pkg: true));
        Assert.True(v.LaterStagesEnabled);
        Assert.Equal([true, true, false, false, false], v.Steps.Select(s => s.Done));
        Assert.True(v.ShowNestAwaiting);
        Assert.False(v.ShowOpsAwaiting);
        Assert.Equal("nest", v.Steps.Single(s => s.Current).Id);
    }

    [Fact]
    public void Ops_stage_without_a_nest_shows_the_ops_guidance_not_the_nest_one()
    {
        var v = WorkflowRules.Evaluate(Facts("ops", pkg: true));
        Assert.True(v.ShowOpsAwaiting);
        Assert.False(v.ShowNestAwaiting);
    }

    [Fact]
    public void Export_stage_guidance_points_at_the_first_missing_prerequisite()
    {
        var noNest = WorkflowRules.Evaluate(Facts("out", pkg: true));
        Assert.True(noNest.ShowOutAwaiting);
        Assert.Equal("nest", noNest.OutAwaitingTarget);

        var nestNoOps = WorkflowRules.Evaluate(Facts("out", pkg: true, nest: true));
        Assert.True(nestNoOps.ShowOutAwaiting);
        Assert.Equal("ops", nestNoOps.OutAwaitingTarget);

        var ready = WorkflowRules.Evaluate(Facts("out", pkg: true, nest: true, ops: true, nc: true));
        Assert.False(ready.ShowOutAwaiting);
        Assert.True(ready.OneClickExportEnabled);
    }

    [Fact]
    public void Geometry_edit_after_nest_marks_nest_ops_out_stale_and_shows_the_banner_off_the_load_stage()
    {
        var onNest = WorkflowRules.Evaluate(Facts("nest", pkg: true, nest: true, ops: true, nc: true, dirty: true));
        Assert.Equal([false, false, true, true, true], onNest.Steps.Select(s => s.Stale));
        Assert.True(onNest.ShowStaleBanner);
        Assert.Contains("重新密排", onNest.Steps[2].Hint);

        var onLoad = WorkflowRules.Evaluate(Facts("load", pkg: true, nest: true, dirty: true));
        Assert.False(onLoad.ShowStaleBanner);

        var otherModule = WorkflowRules.Evaluate(Facts("nest", pkg: true, nest: true, dirty: true, production: false));
        Assert.False(otherModule.ShowStaleBanner);
    }

    [Fact]
    public void Dirty_without_any_downstream_result_is_not_stale()
    {
        var v = WorkflowRules.Evaluate(Facts("nest", pkg: true, dirty: true));
        Assert.All(v.Steps, s => Assert.False(s.Stale));
        Assert.False(v.ShowStaleBanner);
    }

    [Fact]
    public void Stage_index_round_trips()
    {
        for (var i = 0; i < WorkflowRules.StageIds.Length; i++)
            Assert.Equal(i, WorkflowRules.StageIndex(WorkflowRules.StageIds[i]));
        Assert.Equal(0, WorkflowRules.StageIndex("bogus"));
    }
}

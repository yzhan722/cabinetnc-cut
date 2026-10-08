using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class OuterProfileOrderTests
{
    static CutOp Box(string id, double x, double y, double w = 80, double h = 60) => new()
    {
        Op = "contour",
        PanelId = id,
        ToolId = "T2",
        Placed = true,
        ClosePath = true,
        Path = [(x, y), (x + w, y), (x + w, y + h), (x, y + h)],
    };

    [Fact]
    public void Fastest_visits_near_panel_before_far_even_if_name_sorts_later()
    {
        var far = Box("A", 800, 0);
        var near = Box("Z", 20, 0);
        var ordered = OuterProfileOrder.Fastest([far, near], 0, 0);
        Assert.Equal(["Z", "A"], ordered.Select(o => o.PanelId).ToList());
    }

    [Fact]
    public void Fastest_picks_nearest_long_edge_vertex()
    {
        var path = Box("P", 100, 100, 200, 80).Path!;
        var arc = OuterProfileOrder.EntryArc(path, 100, 90);
        var pt = OuterProfileOrder.EntryPoint(path, 100, 90);
        Assert.Equal(0, arc, 3);
        Assert.Equal(100, pt.X, 3);
        Assert.Equal(100, pt.Y, 3);

        var fromRight = OuterProfileOrder.EntryPoint(path, 320, 100);
        Assert.Equal(300, fromRight.X, 3);
        Assert.Equal(100, fromRight.Y, 3);
    }

    [Fact]
    public void Safest_cuts_nested_child_before_host()
    {
        var host = Box("HOST", 0, 0, 400, 300);
        var child = Box("CHILD", 40, 40, 80, 60);
        var ordered = OuterProfileOrder.Safest([host, child], 0, 0);
        Assert.Equal("CHILD", ordered[0].PanelId);
        Assert.Equal("HOST", ordered[1].PanelId);
    }

    [Fact]
    public void Safest_prefers_edge_parts_before_center()
    {
        var center = Box("MID", 400, 400, 80, 60);
        var edge = Box("EDGE", 0, 0, 80, 60);
        var ordered = OuterProfileOrder.Safest([center, edge], 200, 200);
        Assert.Equal("EDGE", ordered[0].PanelId);
        Assert.Equal("MID", ordered[1].PanelId);
    }
}

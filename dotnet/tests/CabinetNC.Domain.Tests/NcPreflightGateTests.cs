using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class NcPreflightGateTests
{
    [Fact]
    public void Empty_placed_ops_is_no_ops()
    {
        var report = NcPreflight.Check([], MachineCatalog.Get("nesting_router_6"), 1220, 2440);
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Code == "no_ops");
    }

    [Fact]
    public void Point_outside_sheet_is_out_of_sheet()
    {
        var op = new CutOp
        {
            Op = "drill",
            PanelId = "P1",
            ToolId = "T3",
            Placed = true,
            SheetX = 5000,
            SheetY = 10,
            DiameterMm = 3,
            DepthMm = 12,
        };
        var report = NcPreflight.Check([op], MachineCatalog.Get("nesting_router_6"), 1220, 2440);
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Code == "out_of_sheet");
    }
}

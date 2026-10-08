using CabinetNC.Domain.Nesting;

namespace CabinetNC.Domain.Tests;

public class NestStockOverridesTests
{
    [Fact]
    public void ForGroup_takes_stock_border_spacing_rotation_keeps_grain()
    {
        var global = new NestSettings
        {
            MarginMm = 99,
            ClearanceMm = 99,
            AllowRotation = false,
            GrainLock = true,
            AllowedRotations = [0, 180],
        };
        var stock = new NestSheetSpec { BorderMm = 15, SpacingMm = 12, AllowRotation = true };
        var g = NestStockOverrides.ForGroup(global, stock);
        Assert.Equal(15, g.MarginMm);
        Assert.Equal(12, g.ClearanceMm);
        Assert.True(g.AllowRotation);
        Assert.True(g.GrainLock);
        Assert.Equal(new[] { 0, 180 }, g.AllowedRotations);
    }
}

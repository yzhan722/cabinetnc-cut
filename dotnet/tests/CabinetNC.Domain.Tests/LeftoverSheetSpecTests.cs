using CabinetNC.Domain.Nesting;

namespace CabinetNC.Domain.Tests;

public class LeftoverSheetSpecTests
{
    [Fact]
    public void Partial_leftover_keeps_origin_edges_and_drops_cut_edges()
    {
        var style = new NestSheetSpec
        {
            WidthMm = 1220,
            LengthMm = 2440,
            BorderMm = 15,
            SpacingMm = 12,
            AllowRotation = true,
        };
        var spec = NestSheetSpec.LeftoverAtOrigin(600, 800, 1220, 2440, 15, style);
        Assert.Equal(600, spec.WidthMm);
        Assert.Equal(800, spec.LengthMm);
        Assert.Equal(15, spec.InsetLeftMm);
        Assert.Equal(15, spec.InsetBottomMm);
        Assert.Equal(0, spec.InsetRightMm);
        Assert.Equal(0, spec.InsetTopMm);
        Assert.Equal(12, spec.SpacingMm);
    }

    [Fact]
    public void Full_size_leftover_keeps_all_insets()
    {
        var style = new NestSheetSpec { SpacingMm = 10, AllowRotation = false };
        var spec = NestSheetSpec.LeftoverAtOrigin(1220, 2440, 1220, 2440, 15, style);
        Assert.Equal(15, spec.InsetRightMm);
        Assert.Equal(15, spec.InsetTopMm);
        Assert.False(spec.AllowRotation);
    }
}

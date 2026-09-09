using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class ViewportMathTests
{
    [Fact]
    public void Fit_reserves_the_side_bay_and_padding()
    {
        var noBay = ViewportMath.FitScale(2000, 1200, 1220, 2440, 0, 44);
        var withBay = ViewportMath.FitScale(2000, 1200, 1220, 2440, 340, 44);
        Assert.True(noBay > 0);
        Assert.True(withBay <= noBay);
        // Tall sheet in a wide canvas: height is the limiting dimension for both.
        Assert.Equal((1200 - 88) / 2440f * 0.9f, noBay, 5);
        Assert.Equal(0, ViewportMath.FitScale(2000, 1200, 0, 2440, 0, 44));
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_pointer_fixed()
    {
        const float sheetH = 2440;
        float scale = 0.4f, ox = 44, oy = 44;
        float sx = 500, sy = 300;
        var before = ViewportMath.ScreenToSheet(sx, sy, scale, ox, oy, sheetH);
        var (s2, ox2, oy2) = ViewportMath.ZoomAbout(sx, sy, scale, ox, oy, sheetH, 1.25, fit: 0.4f);
        var after = ViewportMath.ScreenToSheet(sx, sy, s2, ox2, oy2, sheetH);
        Assert.Equal(0.5f, s2, 5);
        Assert.Equal(before.Mx, after.Mx, 3);
        Assert.Equal(before.My, after.My, 3);
    }

    [Fact]
    public void Zoom_is_clamped_relative_to_fit()
    {
        var (tiny, _, _) = ViewportMath.ZoomAbout(0, 0, 1f, 0, 0, 100, 1e-9, fit: 1f);
        var (huge, _, _) = ViewportMath.ZoomAbout(0, 0, 1f, 0, 0, 100, 1e9, fit: 1f);
        Assert.Equal((float)ViewportMath.MinZoomFactor, tiny, 6);
        Assert.Equal((float)ViewportMath.MaxZoomFactor, huge, 6);
    }

    [Fact]
    public void Screen_to_sheet_flips_y_and_uses_origin()
    {
        var (mx, my) = ViewportMath.ScreenToSheet(144, 44, 0.5f, 44, 44, 2440);
        Assert.Equal(200, mx, 6);
        Assert.Equal(2440, my, 6);
        Assert.Equal((0, 0), ViewportMath.ScreenToSheet(1, 1, 0, 0, 0, 10));
        Assert.Equal(156, ViewportMath.ZoomPercent(0.624f, 0.4f), 3);
    }
}

using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class RailScrollTests
{
    [Fact]
    public void Already_visible_keeps_offset()
    {
        Assert.Equal(80, RailScroll.OffsetToReveal(80, 200, 20, 60));
    }

    [Fact]
    public void Above_viewport_scrolls_up()
    {
        Assert.Equal(40, RailScroll.OffsetToReveal(80, 200, -40, 0));
    }

    [Fact]
    public void Below_viewport_scrolls_down()
    {
        Assert.Equal(130, RailScroll.OffsetToReveal(80, 200, 180, 250));
    }

    [Fact]
    public void Taller_than_viewport_pins_top()
    {
        Assert.Equal(30, RailScroll.OffsetToReveal(80, 200, -50, 250));
    }

    [Fact]
    public void Empty_viewport_or_negative_offset_is_safe()
    {
        Assert.Equal(12, RailScroll.OffsetToReveal(12, 0, -40, 10));
        Assert.Equal(0, RailScroll.OffsetToReveal(10, 200, -30, -10));
    }
}

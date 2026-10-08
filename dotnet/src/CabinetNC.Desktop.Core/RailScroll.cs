namespace CabinetNC.Desktop.Core;

/// <summary>Vertical scroll arithmetic for revealing a row in the left-rail list.</summary>
public static class RailScroll
{
    /// <summary>
    /// Next vertical offset that puts <paramref name="itemTop"/>…<paramref name="itemBottom"/>
    /// (viewport coordinates) into view. Unchanged when the row is already visible or the
    /// viewport is empty. A row taller than the viewport is pinned to the top.
    /// </summary>
    public static double OffsetToReveal(double offset, double viewport, double itemTop, double itemBottom)
    {
        if (viewport <= 0) return offset;
        if (itemBottom - itemTop > viewport || itemTop < 0)
            return Math.Max(0, offset + itemTop);
        if (itemBottom > viewport)
            return Math.Max(0, offset + (itemBottom - viewport));
        return offset;
    }
}

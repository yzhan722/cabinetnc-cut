namespace CabinetNC.Desktop.Core;

/// <summary>Sheet viewport arithmetic shared by painting, wheel zoom, pan and the readout.</summary>
public static class ViewportMath
{
    public const double MinZoomFactor = 0.05;
    public const double MaxZoomFactor = 80;

    /// <summary>
    /// Scale (px per mm) that fits the sheet into the canvas beside an optional screen-anchored
    /// side bay, leaving <paramref name="pad"/> pixels around it; 0 when nothing fits.
    /// </summary>
    public static float FitScale(float canvasW, float canvasH, float sheetW, float sheetH, float bayW, float pad)
    {
        if (sheetW <= 0 || sheetH <= 0) return 0;
        var availW = Math.Max(1f, canvasW - bayW - pad);
        var fit = Math.Min(availW / sheetW, (canvasH - 2 * pad) / sheetH) * 0.9f;
        return fit > 0 ? fit : 0;
    }

    /// <summary>
    /// Zoom by <paramref name="factor"/> keeping the sheet point under (sx, sy) fixed on screen.
    /// Y is flipped: sheet origin is bottom-left, screen origin top-left.
    /// </summary>
    public static (float Scale, float Ox, float Oy) ZoomAbout(
        float sx, float sy, float scale, float ox, float oy, float sheetH, double factor, float fit)
    {
        var wx = (sx - ox) / scale;
        var wy = sheetH - (sy - oy) / scale;
        var next = (float)Math.Clamp(scale * factor, fit * MinZoomFactor, fit * MaxZoomFactor);
        return (next, sx - wx * next, sy - (sheetH - wy) * next);
    }

    public static (double Mx, double My) ScreenToSheet(float sx, float sy, float scale, float ox, float oy, float sheetH)
    {
        if (scale <= 0) return (0, 0);
        return ((sx - ox) / scale, sheetH - (sy - oy) / scale);
    }

    public static double ZoomPercent(float scale, float fit) => fit > 0 ? scale / fit * 100 : 100;
}

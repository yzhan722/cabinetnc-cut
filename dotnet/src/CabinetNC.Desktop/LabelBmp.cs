using CabinetNC.Domain.Manufacturing;
using SkiaSharp;

namespace CabinetNC.Desktop;

/// <summary>
/// Shop label BMP: 600×600, 1 bpp, 300 dpi (Zebra ZM400).
/// Artwork ≈ 50.8×50.8 mm on a 60×60 mm die-cut. Top-heavy: big title, small footer.
/// </summary>
static class LabelBmp
{
    public const int WidthPx = 600;
    public const int HeightPx = 600;
    public const int Dpi = 300;
    /// <summary>White if luma ≥ this; otherwise black. Keeps antialiased edges printable.</summary>
    const int WhiteLuma = 160;

    public static byte[] Render(LabelPaste paste)
    {
        using var bmp = new SKBitmap(WidthPx, HeightPx, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using (var border = new SKPaint
        {
            Color = SKColors.Black,
            IsStroke = true,
            StrokeWidth = 4,
            IsAntialias = false,
        })
            canvas.DrawRect(2, 2, WidthPx - 4, HeightPx - 4, border);

        var title = string.IsNullOrWhiteSpace(paste.Title) ? paste.Stem : paste.Title;
        var project = paste.Project ?? "";
        if (project.Equals(title, StringComparison.OrdinalIgnoreCase)
            || project.Equals(paste.Group, StringComparison.OrdinalIgnoreCase))
            project = "";
        var size = paste.ThicknessMm > 0
            ? $"{Fmt(paste.WidthMm)} × {Fmt(paste.HeightMm)} × {Fmt(paste.ThicknessMm)}"
            : $"{Fmt(paste.WidthMm)} × {Fmt(paste.HeightMm)}";
        var stock = paste.Material ?? "";
        var footer = $"S{paste.SheetIndex + 1}  {paste.Stem}";

        const float left = 32;
        const float maxW = WidthPx - 32 - 32;
        const float footerY = 564;
        float y = 72;

        y = DrawWrapped(canvas, title, left, y, maxW, 66, bold: true, maxLines: 2, lineHeight: 74);
        if (!string.IsNullOrWhiteSpace(project))
        {
            y += 8;
            y = DrawWrapped(canvas, project, left, y, maxW, 28, bold: false, maxLines: 2, lineHeight: 34);
        }
        y += 18;
        y = DrawWrapped(canvas, size, left, y, maxW, 42, bold: true, maxLines: 1, lineHeight: 50);
        if (!string.IsNullOrWhiteSpace(stock))
        {
            y += 6;
            DrawWrapped(canvas, stock, left, y, maxW, 30, bold: false, maxLines: 2, lineHeight: 36);
        }

        DrawWrapped(canvas, footer, left, footerY, maxW, 20, bold: false, maxLines: 1, lineHeight: 24);

        return ToBmp1(bmp);
    }

    static float DrawWrapped(
        SKCanvas canvas, string text, float x, float y, float maxW,
        float size, bool bold, int maxLines, float lineHeight)
    {
        if (string.IsNullOrWhiteSpace(text)) return y;
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        using var font = new SKFont(UiTypeface(bold), size);
        foreach (var line in WrapLines(text, maxW, font, maxLines))
        {
            canvas.DrawText(line, x, y, SKTextAlign.Left, font, paint);
            y += lineHeight;
        }
        return y;
    }

    internal static List<string> WrapLines(string text, float maxWidth, SKFont font, int maxLines)
    {
        var lines = new List<string>();
        var rest = text.Trim();
        while (rest.Length > 0 && lines.Count < maxLines)
        {
            var last = lines.Count == maxLines - 1;
            if (font.MeasureText(rest) <= maxWidth)
            {
                lines.Add(rest);
                break;
            }

            var n = rest.Length;
            var extra = last ? "…" : "";
            while (n > 1 && font.MeasureText(rest[..n] + extra) > maxWidth)
                n--;
            if (!last)
            {
                var sp = rest.LastIndexOf(' ', n);
                if (sp >= n / 3) n = sp;
                lines.Add(rest[..n].TrimEnd());
                rest = rest[n..].TrimStart();
            }
            else
            {
                lines.Add((rest[..n] + "…").TrimEnd());
                break;
            }
        }
        return lines;
    }

    static string Fmt(double v) =>
        v.ToString(v >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

    static SKTypeface? _regular;
    static SKTypeface? _bold;

    static SKTypeface UiTypeface(bool bold)
    {
        if (bold)
            return _bold ??= Resolve(true);
        return _regular ??= Resolve(false);
    }

    static SKTypeface Resolve(bool bold)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        foreach (var family in new[]
                 {
                     "Microsoft YaHei UI",
                     "Microsoft YaHei",
                     "微软雅黑",
                     "Noto Sans CJK SC",
                     "Segoe UI",
                 })
        {
            var tf = SKTypeface.FromFamilyName(family, style);
            if (tf is null) continue;
            if (tf.ContainsGlyph('板') || family is "Segoe UI")
                return tf;
            tf.Dispose();
        }
        return SKTypeface.Default;
    }

    /// <summary>Windows 1 bpp BMP: palette 0=black 1=white, rows padded to 4 bytes, bottom-up.</summary>
    internal static byte[] ToBmp1(SKBitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var stride = ((w + 31) / 32) * 4;
        var pixels = stride * h;
        const int header = 14 + 40 + 8;
        var file = header + pixels;
        var bytes = new byte[file];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.TryWriteBytes(bytes.AsSpan(2, 4), file);
        BitConverter.TryWriteBytes(bytes.AsSpan(10, 4), header);
        BitConverter.TryWriteBytes(bytes.AsSpan(14, 4), 40);
        BitConverter.TryWriteBytes(bytes.AsSpan(18, 4), w);
        BitConverter.TryWriteBytes(bytes.AsSpan(22, 4), h);
        BitConverter.TryWriteBytes(bytes.AsSpan(26, 2), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 2), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(34, 4), pixels);
        var ppm = (int)Math.Round(Dpi * 1000.0 / 25.4);
        BitConverter.TryWriteBytes(bytes.AsSpan(38, 4), ppm);
        BitConverter.TryWriteBytes(bytes.AsSpan(42, 4), ppm);
        BitConverter.TryWriteBytes(bytes.AsSpan(46, 4), 2);
        BitConverter.TryWriteBytes(bytes.AsSpan(50, 4), 2);
        // palette: index 0 black, index 1 white
        bytes[54] = 0;
        bytes[55] = 0;
        bytes[56] = 0;
        bytes[58] = 255;
        bytes[59] = 255;
        bytes[60] = 255;

        var src = bmp.Pixels;
        for (var y = 0; y < h; y++)
        {
            var destRow = header + (h - 1 - y) * stride;
            var srcRow = y * w;
            for (var x = 0; x < w; x++)
            {
                var c = src[srcRow + x];
                var luma = (c.Red * 30 + c.Green * 59 + c.Blue * 11) / 100;
                if (luma < WhiteLuma) continue;
                var bit = 7 - (x & 7);
                bytes[destRow + (x >> 3)] |= (byte)(1 << bit);
            }
        }
        return bytes;
    }
}

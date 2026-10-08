namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;

/// <summary>Sheet veneer axis in nest space: length is +Y, width is +X.</summary>
public enum SheetGrainKind
{
    None = 0,
    AlongLength = 1,
    AlongWidth = 2,
}

/// <summary>Align part grain (local X/Y) to sheet grain. 0/180 and 90/270 are equivalent axes.</summary>
public static class GrainAlign
{
    public static string? NormalizePart(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t is "无" or "-" or "none" or "None" or "NONE" or "0") return null;
        var u = t.ToUpperInvariant();
        if (u is "X" or "ALONGX" or "U") return "X";
        if (u is "Y" or "ALONGY" or "V") return "Y";
        if (u.Contains('X', StringComparison.Ordinal) && !u.Contains('Y', StringComparison.Ordinal))
            return "X";
        if (u.Contains('Y', StringComparison.Ordinal)) return "Y";
        return null;
    }

    /// <summary>
    /// Fusion .cnjob: named X/Y, else grainAngleDeg (0 = +X, 90 = +Y), else grainAlongMm vs size.
    /// </summary>
    public static string? FromFusion(
        string? grainDirection,
        double? grainAngleDeg,
        double? grainAlongMm,
        double widthMm,
        double heightMm)
    {
        var named = NormalizePart(grainDirection);
        if (named is not null) return named;

        if (grainAngleDeg is { } angle && double.IsFinite(angle))
        {
            var a = Math.Abs(((angle % 180d) + 180d) % 180d);
            return a is <= 45d or >= 135d ? "X" : "Y";
        }

        if (grainAlongMm is { } along && along > 0 && widthMm > 0 && heightMm > 0)
        {
            var dx = Math.Abs(widthMm - along);
            var dy = Math.Abs(heightMm - along);
            return dy + 1e-6 < dx ? "Y" : "X";
        }

        return null;
    }

    public static bool HasPartGrain(Panel panel) =>
        NormalizePart(panel.GrainDirection ?? panel.Orientation?.GrainDirection) is not null;

    public static string PartKey(Panel panel) =>
        NormalizePart(panel.GrainDirection ?? panel.Orientation?.GrainDirection) ?? "none";

    public static SheetGrainKind ParseSheet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SheetGrainKind.None;
        var u = raw.Trim().ToLowerInvariant();
        if (u is "length" or "alonglength" or "y" or "long") return SheetGrainKind.AlongLength;
        if (u is "width" or "alongwidth" or "x" or "short") return SheetGrainKind.AlongWidth;
        return SheetGrainKind.None;
    }

    public static string SheetKey(SheetGrainKind kind) =>
        kind switch
        {
            SheetGrainKind.AlongLength => "length",
            SheetGrainKind.AlongWidth => "width",
            _ => "none",
        };

    /// <summary>Empty = no grain constraint; caller keeps its usual rotation set.</summary>
    public static IReadOnlyList<double> AlignedRotations(string? partGrain, SheetGrainKind sheet)
    {
        var part = NormalizePart(partGrain);
        if (part is null || sheet == SheetGrainKind.None)
            return [];
        var partAlongX = part == "X";
        var sheetAlongX = sheet == SheetGrainKind.AlongWidth;
        return partAlongX == sheetAlongX ? [0d, 180d] : [90d, 270d];
    }

    /// <summary>World axis of part grain after nest rotation: (1,0)=sheet width, (0,1)=sheet length.</summary>
    public static (double X, double Y)? WorldAxis(string? partGrain, double rotationDeg)
    {
        var part = NormalizePart(partGrain);
        if (part is null) return null;
        var localX = part == "X" ? 1d : 0d;
        var localY = part == "X" ? 0d : 1d;
        var rad = rotationDeg * Math.PI / 180;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var x = localX * c - localY * s;
        var y = localX * s + localY * c;
        if (Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9) return null;
        return (x, y);
    }
}

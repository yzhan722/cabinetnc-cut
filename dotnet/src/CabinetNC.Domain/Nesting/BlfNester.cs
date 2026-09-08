namespace CabinetNC.Domain.Nesting;

public readonly record struct SheetInsets(double Left, double Bottom, double Right, double Top)
{
    public static SheetInsets Uniform(double mm)
    {
        var v = Math.Max(0, mm);
        return new SheetInsets(v, v, v, v);
    }
}

public sealed class NestSheetSpec
{
    public double WidthMm { get; init; } = 1220;
    public double LengthMm { get; init; } = 2440;
    public double BorderMm { get; init; } = 15;
    /// <summary>When set, overrides <see cref="BorderMm"/> on that side (leftover = full-sheet edge projected).</summary>
    public double? InsetLeftMm { get; init; }
    public double? InsetBottomMm { get; init; }
    public double? InsetRightMm { get; init; }
    public double? InsetTopMm { get; init; }
    /// <summary>Part-to-part clearance for this stock kind. Used when packing this material group.</summary>
    public double SpacingMm { get; init; } = 12;
    /// <summary>Allow 90° nest rotation for panels on this stock kind (still subject to grain lock).</summary>
    public bool AllowRotation { get; init; } = true;
    /// <summary>Request part-in-part nesting for this stock kind (engine may still ignore until implemented).</summary>
    public bool AllowPartsInPart { get; init; }
    /// <summary>Keep-out AABBs in sheet space.</summary>
    public IReadOnlyList<NestBlockedRect> Blocked { get; init; } = [];
    public string? Label { get; init; }
    public string? Material { get; init; }
    public double ThicknessMm { get; init; }
    public SheetGrainKind SheetGrain { get; init; } = SheetGrainKind.None;

    public SheetInsets Insets()
    {
        var fallback = Math.Max(0, BorderMm);
        return new SheetInsets(
            Math.Max(0, InsetLeftMm ?? fallback),
            Math.Max(0, InsetBottomMm ?? fallback),
            Math.Max(0, InsetRightMm ?? fallback),
            Math.Max(0, InsetTopMm ?? fallback));
    }

    public (double X, double Y, double W, double H) InnerRect()
    {
        var i = Insets();
        return (
            i.Left,
            i.Bottom,
            Math.Max(0, WidthMm - i.Left - i.Right),
            Math.Max(0, LengthMm - i.Bottom - i.Top));
    }

    public bool FitsLocalSize(double partW, double partH)
    {
        var (_, _, w, h) = InnerRect();
        return partW <= w + 1e-6 && partH <= h + 1e-6;
    }

    public bool ContainsBox(double minX, double minY, double maxX, double maxY, double slack = 0.05)
    {
        var i = Insets();
        return minX >= i.Left - slack
               && minY >= i.Bottom - slack
               && maxX <= WidthMm - i.Right + slack
               && maxY <= LengthMm - i.Top + slack;
    }

    /// <summary>Leftover stuck at full-sheet origin: keep full-sheet edge only on sides that still are the outer edge.</summary>
    public static NestSheetSpec LeftoverAtOrigin(
        double leftoverW,
        double leftoverH,
        double fullW,
        double fullH,
        double edgeMm,
        NestSheetSpec style)
    {
        var edge = Math.Max(0, edgeMm);
        const double tol = 0.5;
        return new NestSheetSpec
        {
            WidthMm = leftoverW,
            LengthMm = leftoverH,
            BorderMm = edge,
            InsetLeftMm = edge,
            InsetBottomMm = edge,
            InsetRightMm = leftoverW >= fullW - tol ? edge : 0,
            InsetTopMm = leftoverH >= fullH - tol ? edge : 0,
            SpacingMm = style.SpacingMm,
            AllowRotation = style.AllowRotation,
            AllowPartsInPart = style.AllowPartsInPart,
            Label = $"leftover {leftoverW:0.#}×{leftoverH:0.#}",
            Material = style.Material,
            ThicknessMm = style.ThicknessMm,
            SheetGrain = style.SheetGrain,
        };
    }
}

public sealed class NestBlockedRect
{
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }
}

public sealed class NestRequest
{
    public required IReadOnlyList<NestPart> Parts { get; init; }
    public double SheetWidthMm { get; init; } = 1220;
    public double SheetLengthMm { get; init; } = 2440;
    public double SpacingMm { get; init; } = 12;
    public double BorderMm { get; init; } = 15;
    public bool AllowRotation { get; init; } = true;
    /// <summary>If set, multi-sheet queue (primary + remnants). Else single sheet from Width/Length.</summary>
    public IReadOnlyList<NestSheetSpec>? Sheets { get; init; }
}

public sealed class NestPart
{
    public required string PanelId { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public bool MayRotate { get; init; } = true;
    public string? Material { get; init; }
    public double ThicknessMm { get; init; }
}

public sealed class NestPlacement
{
    public required string PanelId { get; init; }
    public int SheetIndex { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double RotationDeg { get; init; }
}

public sealed class NestResult
{
    public required string Engine { get; init; }
    public required IReadOnlyList<NestPlacement> Placements { get; init; }
    public int SheetCount { get; init; }
    public IReadOnlyList<string> Unplaced { get; init; } = [];
    public IReadOnlyList<NestUnplacedReason> UnplacedReasons { get; init; } = [];
    public IReadOnlyList<NestGroupReport> GroupReports { get; init; } = [];
    public IReadOnlyList<NestSheetSpec> SheetsUsed { get; init; } = [];
    /// <summary>Children packed into host through-cutout voids (parts-in-part).</summary>
    public IReadOnlyList<PartInPartSlot> PartInPartSlots { get; init; } = [];
}

namespace CabinetNC.Domain.Parts;

using CabinetNC.Domain.Geometry;

public sealed class PanelFeature
{
    public required string FeatureId { get; init; }
    public required string Kind { get; init; }
    /// <summary>Stable manufacturing face: A, B, or THROUGH.</summary>
    public string? FaceId { get; init; }
    public bool Through { get; init; }
    public string? GroupId { get; init; }
    public string? Purpose { get; init; }
    public string? SourceRelationshipId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double? DiameterMm { get; init; }
    public double? DepthMm { get; init; }
    public double? WidthMm { get; init; }
    /// <summary>Groove centreline (or pocket/cutout polyline) in panel-local mm.</summary>
    public IReadOnlyList<Point2>? Path { get; init; }
    /// <summary>Optional closed CAD opening polygon for groove display.</summary>
    public IReadOnlyList<Point2>? Profile { get; init; }
}

public sealed class WorkpieceFace
{
    public required string FaceId { get; init; }
    public string? Role { get; init; }
    public string? FinishId { get; init; }
    public string? FinishName { get; init; }
    public string? MachiningPermission { get; init; }
}

public sealed class Panel
{
    public required string PanelId { get; init; }
    public string? Name { get; init; }
    public string? Material { get; init; }
    public double ThicknessMm { get; init; }
    /// <summary>Snapshot decor / color id (e.g. white_stipple).</summary>
    public string? DecorId { get; init; }
    /// <summary>Snapshot substrate class (e.g. carcass_board).</summary>
    public string? SubstrateId { get; init; }
    /// <summary>Human color name from generator (e.g. White Stipple).</summary>
    public string? ColorName { get; init; }
    /// <summary>DOUBLE_SIDED / SINGLE_SIDED when known.</summary>
    public string? SurfaceMode { get; init; }
    public int Quantity { get; init; } = 1;
    /// <summary>Allowed nest rotations in degrees (woodjob). Null = unconstrained.</summary>
    public IReadOnlyList<int>? AllowedRotations { get; init; }
    public string? GrainDirection { get; init; }
    public required Outline Outline { get; init; }
    public IReadOnlyList<PanelFeature> Features { get; init; } = [];
    public IReadOnlyList<WorkpieceFace> Faces { get; init; } = [];

    /// <summary>Project/Module/Workpiece identity (optional; soft hierarchy).</summary>
    public WorkpieceIdentity? Identity { get; init; }
    /// <summary>Extended orientation (faces / mirror). Grain/rotations also mirrored on panel for compat.</summary>
    public WorkpieceOrientation? Orientation { get; init; }
    public EdgeBanding? EdgeBanding { get; init; }
    public string? Notes { get; init; }
    /// <summary>A / B side placeholder for dual-face CAM.</summary>
    public string? Side { get; init; }

    /// <summary>True if 90° (or 270°) nest rotation is allowed.</summary>
    public bool MayRotate90 =>
        AllowedRotations is null
        || AllowedRotations.Count == 0
        || AllowedRotations.Any(r => Math.Abs(((r % 360) + 360) % 360 - 90) < 1e-6
            || Math.Abs(((r % 360) + 360) % 360 - 270) < 1e-6);

    /// <summary>Primary list title — skip Lay Flat labels; drop <c>@…</c> uniquifier.</summary>
    public string DisplayTitle
    {
        get
        {
            string raw;
            if (!IsLayFlatPlaceholder(Name))
                raw = Name!.Trim();
            else if (!IsLayFlatPlaceholder(PanelId))
                raw = PanelId;
            else
            {
                var source = Identity?.WorkpieceId;
                raw = !string.IsNullOrWhiteSpace(source) && !IsLayFlatPlaceholder(source)
                    ? source!
                    : string.IsNullOrWhiteSpace(Name) ? PanelId : Name!;
            }
            // Normalize legacy "A - B" export labels to Fusion "A-B".
            return StripAtSuffix(raw).Replace(" - ", "-", StringComparison.Ordinal);
        }
    }

    /// <summary>Group key before <c>.</c> or <c>-</c> (Fusion ``assembly-component``).</summary>
    public string DisplayGroup => SplitGroupPart(DisplayTitle).Group;

    /// <summary>Part name after <c>.</c> or <c>-</c> inside the group.</summary>
    public string DisplayPartName => SplitGroupPart(DisplayTitle).Part;

    /// <summary>Stock grouping key — prefer materialId so thicknesses stay separate.</summary>
    public string MaterialGroupKey =>
        string.IsNullOrWhiteSpace(Material) ? $"t{Fmt(ThicknessMm)}" : Material.Trim();

    /// <summary>Shop stock label e.g. <c>Carcass_White Stipple_DS · 15mm</c>.</summary>
    public string MaterialGroupLabel
    {
        get
        {
            var role = ResolveRoleTitle();
            var decor = ResolveDecorTitle();
            var surface = ResolveSurfaceToken();
            var baseLabel = $"{role}_{decor}_{surface}";
            return ThicknessMm > 0 ? $"{baseLabel} · {Fmt(ThicknessMm)}mm" : baseLabel;
        }
    }

    /// <summary>Size / qty / feature summary for list rows.</summary>
    public string DisplayDetail
    {
        get
        {
            var pts = Outline.Points;
            var size = "";
            if (pts is { Count: >= 2 })
            {
                var w = pts.Max(p => p.X) - pts.Min(p => p.X);
                var h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
                size = $"{Fmt(w)}×{Fmt(h)}×{Fmt(ThicknessMm)}";
            }
            var qty = Quantity > 1 ? $" ×{Quantity}" : "";
            var feats = Features.Count > 0 ? $" · {Features.Count}特征" : " · 无特征";
            return string.IsNullOrEmpty(size) ? $"{Material ?? "板件"}{qty}{feats}" : $"{size}{qty}{feats}";
        }
    }

    public override string ToString() =>
        string.IsNullOrEmpty(DisplayDetail) ? DisplayTitle : $"{DisplayTitle}  {DisplayDetail}";

    /// <summary>True when a label is the Lay Flat work-zone container, not a CAD part name.</summary>
    public static bool IsLayFlatPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var t = value.Trim();
        if (t.Equals("LAY_FLAT", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("LAY_FLAT:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("LAY_FLAT (", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("LAY_FLAT -", StringComparison.OrdinalIgnoreCase)) return true;
        // e.g. "LAY_FLAT:1 - LAY_FLAT"
        if (t.Contains("LAY_FLAT", StringComparison.OrdinalIgnoreCase)
            && t.Replace(" ", "", StringComparison.Ordinal)
                .StartsWith("LAY_FLAT:", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Drop export uniquifier after first <c>@</c> (e.g. <c>@layflat-1-3</c>).</summary>
    public static string StripAtSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? "";
        var t = value.Trim();
        var at = t.IndexOf('@');
        return at < 0 ? t : t[..at];
    }

    /// <summary>
    /// Split shop title into group/part. Prefer <c>.</c> (panelId style), else
    /// first <c>-</c> (Fusion LAY_FLAT body style). Skip date-like hyphen splits.
    /// </summary>
    public static (string Group, string Part) SplitGroupPart(string? title)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "" : title.Trim();
        if (t.Length == 0) return ("其他", "");

        var dot = t.IndexOf('.');
        if (dot > 0 && dot < t.Length - 1)
            return (t[..dot], t[(dot + 1)..]);

        var dash = t.IndexOf('-');
        if (dash > 0 && dash < t.Length - 1)
        {
            var group = t[..dash];
            var part = t[(dash + 1)..];
            // Avoid ``…_2026-07-07T…`` date fragments.
            if (char.IsDigit(group[^1]) && char.IsDigit(part[0]))
                return ("其他", t);
            return (group, part);
        }

        return ("其他", t);
    }

    string ResolveRoleTitle()
    {
        var raw = Identity?.Role
            ?? Notes
            ?? SubstrateId
            ?? Material
            ?? "";
        var token = raw.Trim().ToLowerInvariant();
        if (token.Contains("door", StringComparison.Ordinal)) return "Door";
        if (token.Contains("carcass", StringComparison.Ordinal)) return "Carcass";
        if (token.Contains("partition", StringComparison.Ordinal)) return "Partition";
        if (token.StartsWith("door", StringComparison.Ordinal)) return "Door";
        if (token.StartsWith("carcass", StringComparison.Ordinal)) return "Carcass";
        if (string.IsNullOrWhiteSpace(token)) return "Board";
        // substrate like carcass_board → Carcass
        var head = token.Split('_', '-', '.')[0];
        return char.ToUpperInvariant(head[0]) + head[1..];
    }

    string ResolveDecorTitle()
    {
        if (!string.IsNullOrWhiteSpace(ColorName))
            return ColorName.Trim();
        var raw = DecorId ?? "";
        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(Material))
        {
            // materialId: carcass-white-stipple-15 → white-stipple
            var parts = Material!.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3
                && double.TryParse(parts[^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                raw = string.Join("-", parts.Skip(1).Take(parts.Length - 2));
            else if (parts.Length >= 2)
                raw = string.Join("-", parts.Skip(1));
        }
        return HumanizeToken(raw);
    }

    string ResolveSurfaceToken()
    {
        var mode = (SurfaceMode ?? "").Trim().ToUpperInvariant();
        if (mode.Contains("DOUBLE", StringComparison.Ordinal)) return "DS";
        if (mode.Contains("SINGLE", StringComparison.Ordinal)) return "SS";
        var role = ResolveRoleTitle();
        if (role.Equals("Carcass", StringComparison.OrdinalIgnoreCase)
            || role.Equals("Partition", StringComparison.OrdinalIgnoreCase))
            return "DS";
        if (role.Equals("Door", StringComparison.OrdinalIgnoreCase))
            return "SS";
        return "SS";
    }

    static string HumanizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unassigned";
        var parts = raw.Trim().Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", parts.Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    static string Fmt(double v) =>
        Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.#");
}

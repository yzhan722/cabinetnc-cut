namespace CabinetNC.Domain.Manufacturing;

public sealed record CutOp
{
    public required string Op { get; init; } // contour | drill | groove | pocket
    public required string PanelId { get; init; }
    public string? FeatureId { get; init; }
    public bool Placed { get; init; }
    public int SheetIndex { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double RotationDeg { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? SheetX { get; init; }
    public double? SheetY { get; init; }
    public double? DiameterMm { get; init; }
    public double? DepthMm { get; init; }
    public double? WidthMm { get; init; }
    public double? StepdownMm { get; init; }
    public IReadOnlyList<(double X, double Y)>? Path { get; init; }
    /// <summary>Disjoint path segments for pocket clear (scan strokes). Prefer over flat Path.</summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>>? PathSegments { get; init; }
    /// <summary>Optional closed finish loop for pocket onion-skin boundary.</summary>
    public IReadOnlyList<(double X, double Y)>? FinishLoop { get; init; }
    /// <summary>When true (contours), emitter closes to first point; pockets use false.</summary>
    public bool ClosePath { get; init; } = true;
    /// <summary>Pocket inset cleared empty — tool cannot fit (must fail preflight).</summary>
    public bool PocketTooSmallForTool { get; init; }
    public Nesting.LocalBounds? PanelBounds { get; init; }
    /// <summary>Bound tool — required for export (Day 7).</summary>
    public string? ToolId { get; init; }
    /// <summary>A | B face.</summary>
    public string? Side { get; init; }
    public int SequenceGroup { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Tongue-receiving half groove — T1. All other grooves are T2.</summary>
    public bool IsTongue { get; init; }
    /// <summary>Panel thickness (mm). Needed when Z0 is the board bottom.</summary>
    public double? ThicknessMm { get; init; }
    /// <summary>Through feature — last Z uses through overshoot, not blind depth.</summary>
    public bool Through { get; init; }
    /// <summary>Exact CAD tool-centre loop when Fusion exported line/arc entities.</summary>
    public IReadOnlyList<Geometry.CadSegment>? CadPath { get; init; }
}

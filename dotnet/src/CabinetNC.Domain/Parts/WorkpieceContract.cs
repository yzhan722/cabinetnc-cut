namespace CabinetNC.Domain.Parts;

/// <summary>
/// Manufacturing identity for a flat runtime panel (maps Project/Module/Workpiece without forcing UI tree yet).
/// </summary>
public sealed class WorkpieceIdentity
{
    public string? ProjectId { get; init; }
    public string? ModuleId { get; init; }
    public string? WorkpieceId { get; init; }
    /// <summary>Shop role from snapshot identity (carcass / door / …).</summary>
    public string? Role { get; init; }
    public string? SourcePath { get; init; }
    public string? SourceFormat { get; init; }
}

/// <summary>Face / grain / nest policy for a workpiece.</summary>
public sealed class WorkpieceOrientation
{
    /// <summary>A or B primary face label (placeholder until Day 11).</summary>
    public string? PrimaryFace { get; init; }
    /// <summary>Face currently milled / face-up in nest.</summary>
    public string? MillingFace { get; init; }
    public string? GrainDirection { get; init; }
    public IReadOnlyList<int>? AllowedRotations { get; init; }
    public bool AllowMirror { get; init; }
    /// <summary>Flip strategy placeholder: none | x | y.</summary>
    public string? FlipStrategy { get; init; }
}

public sealed class EdgeBanding
{
    public string? Front { get; init; }
    public string? Back { get; init; }
    public string? Left { get; init; }
    public string? Right { get; init; }
}

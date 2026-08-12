namespace CabinetNC.Infrastructure.Projects;

/// <summary>Persisted cutting-station project (SQLite row).</summary>
public sealed class ProjectDocument
{
    public required string Name { get; init; }
    public required string PackageJson { get; init; }
    public string? SourceSnapshotJson { get; init; }
    public string MachineId { get; init; } = "osai_e4_1325";
    public string? NestPlacementsJson { get; init; }
    public string? NcText { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class NestPlacementDto
{
    public string PanelId { get; set; } = "";
    public int SheetIndex { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double RotationDeg { get; set; }
}

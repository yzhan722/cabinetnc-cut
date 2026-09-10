namespace CabinetNC.FusionPackage;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class CnJobManifest
{
    public string Format { get; init; } = "";
    public string SchemaVersion { get; init; } = "";
    public string Payload { get; init; } = "snapshot.json";
}

public sealed class ManufacturingSnapshot
{
    public const string SchemaName = "cabinetnc.manufacturing-snapshot";
    public const string CurrentVersion = "1.0.0";

    public string Schema { get; init; } = "";
    public string SchemaVersion { get; init; } = "";
    public string JobId { get; init; } = "";
    public string Units { get; init; } = "mm";
    public DateTimeOffset? ExportedAt { get; init; }
    public JsonElement? Source { get; init; }
    public List<SnapshotMaterial> Materials { get; init; } = [];
    public List<SnapshotWorkpiece> Workpieces { get; init; } = [];
    public List<JsonElement> Relationships { get; init; } = [];
    public List<SnapshotDiagnostic> Diagnostics { get; init; } = [];
}

public sealed class SnapshotMaterial
{
    public string MaterialId { get; init; } = "";
    public string? SubstrateId { get; init; }
    public string? DisplayName { get; init; }
    public double? ThicknessMm { get; init; }
    public string? DecorId { get; init; }
    public string? ColorName { get; init; }
    public string? SurfaceMode { get; init; }
}

public sealed class SnapshotWorkpiece
{
    public string WorkpieceId { get; init; } = "";
    public string? PanelId { get; init; }
    public string? Name { get; init; }
    public int Quantity { get; init; } = 1;
    public SnapshotIdentity Identity { get; init; } = new();
    public SnapshotMaterialRef Material { get; init; } = new();
    public SnapshotGeometry Geometry { get; init; } = new();
    public List<SnapshotFace> Faces { get; init; } = [];
    public List<SnapshotFeature> Features { get; init; } = [];
    public SnapshotManufacturing? Manufacturing { get; init; }
    /// <summary>Optional part grain from Fusion: X / Y (panel-local).</summary>
    public string? GrainDirection { get; init; }
    public JsonElement? Provenance { get; init; }
}

public sealed class SnapshotIdentity
{
    public string? ProjectId { get; init; }
    public string? ModuleId { get; init; }
    public string? Role { get; init; }
}

public sealed class SnapshotMaterialRef
{
    public string MaterialId { get; init; } = "";
    public double ThicknessMm { get; init; }
    public string? SubstrateId { get; init; }
    public string? DecorId { get; init; }
    public string? DisplayName { get; init; }
    public string? ColorName { get; init; }
    public string? SurfaceMode { get; init; }
    /// <summary>Fusion: edge length the grain follows (mm).</summary>
    public double? GrainAlongMm { get; init; }
    /// <summary>Fusion flattened angle: 0 = along +X, 90 = along +Y.</summary>
    public double? GrainAngleDeg { get; init; }
}

public sealed class SnapshotGeometry
{
    public string Quality { get; init; } = "";
    public double? ToleranceMm { get; init; }
    public SnapshotProfile OuterProfile { get; init; } = new();
    public List<SnapshotProfile> InnerProfiles { get; init; } = [];
    public List<List<double>> NestingPolygon { get; init; } = [];
}

public sealed class SnapshotProfile
{
    public bool Closed { get; init; } = true;
    public List<List<double>> Points { get; init; } = [];
    public List<SnapshotSegment> Segments { get; init; } = [];
}

public sealed class SnapshotSegment
{
    public string Type { get; init; } = "line";
    public List<double> Start { get; init; } = [];
    public List<double> End { get; init; } = [];
    public List<double>? Center { get; init; }
    public double? RadiusMm { get; init; }
    public bool Cw { get; init; }
}

public sealed class SnapshotFace
{
    public string FaceId { get; init; } = "";
    public string? Role { get; init; }
    public SnapshotFinish? Finish { get; init; }
    public string? MachiningPermission { get; init; }
}

public sealed class SnapshotFinish
{
    public string FinishId { get; init; } = "";
    public string? FinishName { get; init; }
}

public sealed class SnapshotFeature
{
    public string FeatureId { get; init; } = "";
    public string? GroupId { get; init; }
    public string Kind { get; init; } = "";
    public string SourceFace { get; init; } = "";
    public SnapshotFeatureGeometry Geometry { get; init; } = new();
    public double? DepthMm { get; init; }
    public bool Through { get; init; }
    /// <summary>True when Fusion extract saw Arc3D/Circle3D edges on the opening.</summary>
    public bool HasArc { get; init; }
    public SnapshotFeatureIntent? Intent { get; init; }
}

public sealed class SnapshotFeatureGeometry
{
    public List<double>? Center { get; init; }
    public double? DiameterMm { get; init; }
    public List<List<double>>? Centerline { get; init; }
    public double? WidthMm { get; init; }
    public SnapshotProfile? Profile { get; init; }
    public List<SnapshotProfile>? Holes { get; init; }
}

public sealed class SnapshotFeatureIntent
{
    public string? Purpose { get; init; }
    public string? OperationType { get; init; }
    public string? SourceRelationshipId { get; init; }
}

public sealed class SnapshotManufacturing
{
    public string Mode { get; init; } = "singleSide";
    public string? MachiningFace { get; init; }
    public string? GrainDirection { get; init; }
}

public sealed class SnapshotDiagnostic
{
    public string Severity { get; init; } = "";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? EntityId { get; init; }
}

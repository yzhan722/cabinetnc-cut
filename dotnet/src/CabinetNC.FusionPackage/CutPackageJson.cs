namespace CabinetNC.FusionPackage;

using System.Text.Json;
using System.Text.Json.Serialization;
using CabinetNC.Domain;
using CabinetNC.Domain.Parts;

/// <summary>Serialize runtime CutPackage to flat JSON for project.db / legacy round-trip.</summary>
public static class CutPackageJson
{
    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(CutPackage pkg)
    {
        var dto = new
        {
            // This serializer is the editable flat runtime projection. The immutable
            // manufacturing snapshot is persisted separately.
            schema = CutPackage.Schema,
            schemaVersion = CutPackage.SchemaVersion,
            jobId = pkg.JobId,
            units = pkg.Units,
            sheets = pkg.Sheets.Select(s => new
            {
                sheetId = s.SheetId,
                material = s.Material,
                thicknessMm = s.ThicknessMm,
                widthMm = s.WidthMm,
                lengthMm = s.LengthMm,
                heightMm = s.LengthMm,
                marginMm = s.MarginMm,
                kerfMm = s.KerfMm,
                partClearanceMm = s.PartClearanceMm,
            }),
            panels = pkg.Panels.Select(p => new
            {
                panelId = p.PanelId,
                name = p.Name,
                material = p.Material,
                thicknessMm = p.ThicknessMm,
                quantity = p.Quantity,
                grainDirection = p.GrainDirection,
                allowedRotations = p.AllowedRotations,
                packageId = p.Identity?.PackageId,
                packageLabel = p.Identity?.PackageLabel,
                projectId = p.Identity?.ProjectId,
                moduleId = p.Identity?.ModuleId,
                workpieceId = p.Identity?.WorkpieceId ?? p.PanelId,
                side = p.Side,
                notes = p.Notes,
                orientation = p.Orientation is null ? null : new
                {
                    primaryFace = p.Orientation.PrimaryFace,
                    millingFace = p.Orientation.MillingFace,
                    grainDirection = p.Orientation.GrainDirection ?? p.GrainDirection,
                    allowedRotations = p.Orientation.AllowedRotations ?? p.AllowedRotations,
                    allowMirror = p.Orientation.AllowMirror,
                    flipStrategy = p.Orientation.FlipStrategy,
                },
                edgeBanding = p.EdgeBanding is null ? null : new
                {
                    front = p.EdgeBanding.Front,
                    back = p.EdgeBanding.Back,
                    left = p.EdgeBanding.Left,
                    right = p.EdgeBanding.Right,
                },
                faces = p.Faces.Select(f => new
                {
                    faceId = f.FaceId,
                    role = f.Role,
                    finishId = f.FinishId,
                    finishName = f.FinishName,
                    machiningPermission = f.MachiningPermission,
                }),
                outline = new
                {
                    points = p.Outline.Points.Select(pt => new[] { pt.X, pt.Y }),
                    closed = p.Outline.Closed,
                    frame = p.Outline.Frame,
                },
                features = p.Features.Select(FeatureDto),
            }),
        };
        return JsonSerializer.Serialize(dto, Opts);
    }

    static object FeatureDto(PanelFeature f) => new
    {
        featureId = f.FeatureId,
        kind = f.Kind,
        faceId = f.FaceId,
        through = f.Through,
        groupId = f.GroupId,
        purpose = f.Purpose,
        sourceRelationshipId = f.SourceRelationshipId,
        x = f.X,
        y = f.Y,
        diameterMm = f.DiameterMm,
        depthMm = f.DepthMm,
        widthMm = f.WidthMm,
        path = f.Path?.Select(pt => new[] { pt.X, pt.Y }),
        profile = f.Profile?.Select(pt => new[] { pt.X, pt.Y }),
        holes = f.Holes?.Select(ring => ring.Select(pt => new[] { pt.X, pt.Y })),
    };
}

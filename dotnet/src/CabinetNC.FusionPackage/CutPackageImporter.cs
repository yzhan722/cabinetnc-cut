namespace CabinetNC.FusionPackage;

using System.Text.Json;
using System.Text.Json.Serialization;
using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Materials;
using CabinetNC.Domain.Parts;

public sealed record ValidationIssue(string Code, string Path, string Message);

public sealed class PackageImportResult
{
    public required bool Ok { get; init; }
    public CutPackage? Package { get; init; }
    public ManufacturingSnapshot? Snapshot { get; init; }
    /// <summary>Exact immutable snapshot payload retained independently from the flat runtime projection.</summary>
    public string? SourceSnapshotJson { get; init; }
    public IReadOnlyList<ValidationIssue> Errors { get; init; } = [];
    public IReadOnlyList<ValidationIssue> Warnings { get; init; } = [];
}

/// <summary>Import cabinetnc.cut-package JSON (product contract from Vite repo).</summary>
public static class CutPackageImporter
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PackageImportResult FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    public static PackageImportResult FromFile(string path) =>
        FromJson(File.ReadAllText(path));

    public static PackageImportResult FromElement(JsonElement root)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail("root", "$", "JSON root must be an object");
        }

        var pkgEl = root;
        if (TryGetString(root, "schema") == "cabinetnc.cut-project")
        {
            if (!root.TryGetProperty("package", out pkgEl) || pkgEl.ValueKind != JsonValueKind.Object)
                return Fail("project_package", "$.package", "cut-project missing package object");
            warnings.Add(new("unwrapped_project", "$", "imported cut-project package body"));
        }

        var schema = TryGetString(pkgEl, "schema");
        if (string.IsNullOrEmpty(schema))
        {
            if (!pkgEl.TryGetProperty("panels", out var panelsProbe) || panelsProbe.ValueKind != JsonValueKind.Array || panelsProbe.GetArrayLength() == 0)
                return Fail("schema", "$.schema", $"schema must be \"{CutPackage.Schema}\"");
            warnings.Add(new("schema_injected", "$.schema", $"missing schema — treated as \"{CutPackage.Schema}\""));
            schema = CutPackage.Schema;
        }
        else if (schema != CutPackage.Schema)
        {
            errors.Add(new("schema", "$.schema", $"schema must be \"{CutPackage.Schema}\" (got {schema})"));
        }

        var version = pkgEl.TryGetProperty("schemaVersion", out var verEl) && verEl.TryGetInt32(out var v) ? v : CutPackage.SchemaVersion;
        if (version != CutPackage.SchemaVersion)
            warnings.Add(new("schemaVersion", "$.schemaVersion", $"schemaVersion {version} — viewer targets v{CutPackage.SchemaVersion}"));

        var sheets = new List<SheetStock>();
        if (pkgEl.TryGetProperty("sheets", out var sheetsEl) && sheetsEl.ValueKind == JsonValueKind.Array)
        {
            var si = 0;
            foreach (var s in sheetsEl.EnumerateArray())
            {
                sheets.Add(new SheetStock
                {
                    SheetId = TryGetString(s, "sheetId") ?? $"S{si}",
                    Material = TryGetString(s, "material") ?? TryGetString(s, "materialId"),
                    ThicknessMm = GetDouble(s, "thicknessMm"),
                    WidthMm = GetDouble(s, "widthMm"),
                    LengthMm = TryGetDouble(s, "lengthMm") ?? GetDouble(s, "heightMm"),
                    MarginMm = GetDouble(s, "marginMm"),
                    KerfMm = GetDouble(s, "kerfMm"),
                    PartClearanceMm = GetDouble(s, "partClearanceMm"),
                });
                si++;
            }
        }

        var panels = new List<Panel>();
        if (!pkgEl.TryGetProperty("panels", out var panelsEl) || panelsEl.ValueKind != JsonValueKind.Array || panelsEl.GetArrayLength() == 0)
        {
            errors.Add(new("panels_empty", "$.panels", "panels[] is empty — need at least one panel"));
        }
        else
        {
            var i = 0;
            foreach (var p in panelsEl.EnumerateArray())
            {
                var path = $"$.panels[{i}]";
                var panelId = TryGetString(p, "panelId") ?? $"P{i}";
                if (!p.TryGetProperty("outline", out var outlineEl))
                {
                    errors.Add(new("outline", $"{path}.outline", "missing outline"));
                    i++;
                    continue;
                }

                var points = ReadPoints(outlineEl, $"{path}.outline", errors);
                if (points.Count < 3)
                {
                    errors.Add(new("outline.points", $"{path}.outline.points", "need at least 3 points"));
                    i++;
                    continue;
                }

                var features = new List<PanelFeature>();
                if (p.TryGetProperty("features", out var feats) && feats.ValueKind == JsonValueKind.Array)
                {
                    var fi = 0;
                    foreach (var f in feats.EnumerateArray())
                    {
                        IReadOnlyList<Point2>? featPath = ReadFeaturePoints(f, "path", minCount: 2);
                        IReadOnlyList<Point2>? featProfile = ReadFeaturePoints(f, "profile", minCount: 3);

                        double fx = GetDouble(f, "x"), fy = GetDouble(f, "y");
                        if (f.TryGetProperty("center", out var center) && center.ValueKind == JsonValueKind.Array && center.GetArrayLength() >= 2)
                        {
                            fx = center[0].GetDouble();
                            fy = center[1].GetDouble();
                        }

                        var rawKind = TryGetString(f, "kind") ?? TryGetString(f, "featureType") ?? "unknown";
                        features.Add(new PanelFeature
                        {
                            FeatureId = TryGetString(f, "featureId") ?? TryGetString(f, "id") ?? $"F{fi}",
                            Kind = WoodJobImporter.MapFeatureKind(rawKind),
                            FaceId = TryGetString(f, "faceId") ?? TryGetString(f, "sourceFace") ?? TryGetString(f, "fromFace"),
                            Through = f.TryGetProperty("through", out var throughEl) && throughEl.ValueKind == JsonValueKind.True,
                            GroupId = TryGetString(f, "groupId"),
                            Purpose = TryGetString(f, "purpose"),
                            SourceRelationshipId = TryGetString(f, "sourceRelationshipId"),
                            X = fx,
                            Y = fy,
                            DiameterMm = TryGetDouble(f, "diameterMm"),
                            DepthMm = TryGetDouble(f, "depthMm"),
                            WidthMm = TryGetDouble(f, "widthMm"),
                            Path = featPath,
                            Profile = featProfile,
                        });
                        fi++;
                    }
                }

                IReadOnlyList<int>? rotations = null;
                if (p.TryGetProperty("allowedRotations", out var ar) && ar.ValueKind == JsonValueKind.Array)
                {
                    var rs = new List<int>();
                    foreach (var r in ar.EnumerateArray())
                        if (r.TryGetInt32(out var deg)) rs.Add(deg);
                    if (rs.Count > 0) rotations = rs;
                }
                else if (p.TryGetProperty("orientation", out var ori) && ori.ValueKind == JsonValueKind.Object
                         && ori.TryGetProperty("allowedRotations", out var ar2) && ar2.ValueKind == JsonValueKind.Array)
                {
                    var rs = new List<int>();
                    foreach (var r in ar2.EnumerateArray())
                        if (r.TryGetInt32(out var deg)) rs.Add(deg);
                    if (rs.Count > 0) rotations = rs;
                }

                var grain = TryGetString(p, "grainDirection");
                if (grain is null && p.TryGetProperty("orientation", out var ori2) && ori2.ValueKind == JsonValueKind.Object)
                    grain = TryGetString(ori2, "grainDirection");

                string? primaryFace = null;
                string? millingFace = null;
                var allowMirror = false;
                string? flipStrategy = null;
                if (p.TryGetProperty("orientation", out var oriFace) && oriFace.ValueKind == JsonValueKind.Object)
                {
                    primaryFace = TryGetString(oriFace, "primaryFace") ?? TryGetString(oriFace, "faceUp");
                    millingFace = TryGetString(oriFace, "millingFace") ?? TryGetString(oriFace, "millingSurface")
                        ?? TryGetString(oriFace, "fromFace");
                    allowMirror = oriFace.TryGetProperty("allowMirror", out var am) && am.ValueKind == JsonValueKind.True;
                    flipStrategy = TryGetString(oriFace, "flipStrategy");
                }

                EdgeBanding? banding = null;
                if (p.TryGetProperty("edgeBanding", out var eb) && eb.ValueKind == JsonValueKind.Object)
                {
                    banding = new EdgeBanding
                    {
                        Front = TryGetString(eb, "front"),
                        Back = TryGetString(eb, "back"),
                        Left = TryGetString(eb, "left"),
                        Right = TryGetString(eb, "right"),
                    };
                }

                var faces = new List<WorkpieceFace>();
                if (p.TryGetProperty("faces", out var facesEl) && facesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var face in facesEl.EnumerateArray())
                    {
                        var faceId = TryGetString(face, "faceId");
                        if (string.IsNullOrWhiteSpace(faceId)) continue;
                        faces.Add(new WorkpieceFace
                        {
                            FaceId = faceId,
                            Role = TryGetString(face, "role"),
                            FinishId = TryGetString(face, "finishId"),
                            FinishName = TryGetString(face, "finishName"),
                            MachiningPermission = TryGetString(face, "machiningPermission"),
                        });
                    }
                }

                var thickness = GetDouble(p, "thicknessMm");
                if (thickness <= 0)
                    errors.Add(new("thickness", $"{path}.thicknessMm", "thicknessMm must be > 0"));

                panels.Add(new Panel
                {
                    PanelId = panelId,
                    Name = TryGetString(p, "name"),
                    Material = TryGetString(p, "material") ?? TryGetString(p, "materialId"),
                    ThicknessMm = thickness,
                    Quantity = p.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) ? Math.Max(1, q) : 1,
                    AllowedRotations = rotations,
                    GrainDirection = grain,
                    Outline = new Outline
                    {
                        Points = points,
                        Closed = !outlineEl.TryGetProperty("closed", out var c) || c.ValueKind != JsonValueKind.False,
                        Frame = TryGetString(outlineEl, "frame") ?? "panelLocal",
                    },
                    Features = features,
                    Faces = faces,
                    Identity = new WorkpieceIdentity
                    {
                        ProjectId = TryGetString(p, "projectId"),
                        ModuleId = TryGetString(p, "moduleId"),
                        WorkpieceId = TryGetString(p, "workpieceId") ?? panelId,
                        SourceFormat = CutPackage.Schema,
                    },
                    Orientation = new WorkpieceOrientation
                    {
                        PrimaryFace = primaryFace,
                        MillingFace = millingFace,
                        GrainDirection = grain,
                        AllowedRotations = rotations,
                        AllowMirror = allowMirror,
                        FlipStrategy = flipStrategy,
                    },
                    EdgeBanding = banding,
                    Notes = TryGetString(p, "notes"),
                    Side = TryGetString(p, "side") ?? primaryFace ?? millingFace,
                });
                i++;
            }
        }

        if (errors.Count > 0)
            return new PackageImportResult { Ok = false, Errors = errors, Warnings = warnings };

        return new PackageImportResult
        {
            Ok = true,
            Package = new CutPackage
            {
                SchemaName = schema!,
                Version = version,
                JobId = TryGetString(pkgEl, "jobId"),
                Units = TryGetString(pkgEl, "units") ?? "mm",
                Sheets = sheets,
                Panels = panels,
            },
            Errors = errors,
            Warnings = warnings,
        };
    }

    static List<Point2> ReadPoints(JsonElement outline, string path, List<ValidationIssue> errors)
    {
        var pts = new List<Point2>();
        if (!outline.TryGetProperty("points", out var pointsEl) || pointsEl.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new("outline.points", $"{path}.points", "outline.points must be an array"));
            return pts;
        }

        var i = 0;
        foreach (var pt in pointsEl.EnumerateArray())
        {
            if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
            {
                pts.Add(new Point2(pt[0].GetDouble(), pt[1].GetDouble()));
            }
            else if (pt.ValueKind == JsonValueKind.Object)
            {
                pts.Add(new Point2(GetDouble(pt, "x"), GetDouble(pt, "y")));
            }
            else
            {
                errors.Add(new("outline.points", $"{path}.points[{i}]", "point must be [x,y] or {x,y}"));
            }
            i++;
        }
        return pts;
    }

    static IReadOnlyList<Point2>? ReadFeaturePoints(JsonElement feature, string property, int minCount)
    {
        if (!feature.TryGetProperty(property, out var pathEl) || pathEl.ValueKind != JsonValueKind.Array)
            return null;
        var pts = new List<Point2>();
        foreach (var pt in pathEl.EnumerateArray())
        {
            if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
                pts.Add(new Point2(pt[0].GetDouble(), pt[1].GetDouble()));
            else if (pt.ValueKind == JsonValueKind.Object)
                pts.Add(new Point2(GetDouble(pt, "x"), GetDouble(pt, "y")));
        }
        return pts.Count >= minCount ? pts : null;
    }

    static PackageImportResult Fail(string code, string path, string msg) =>
        new() { Ok = false, Errors = [new ValidationIssue(code, path, msg)] };

    static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static double GetDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetDouble(out var d) ? d : 0;

    static double? TryGetDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetDouble(out var d) ? d : null;
}

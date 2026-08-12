namespace CabinetNC.FusionPackage;

using System.IO.Compression;
using System.Text.Json;
using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

public static class ManufacturingSnapshotImporter
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Production kinds projected into CutPackage / OpsPlanner.</summary>
    static readonly HashSet<string> SupportedKinds =
    [
        "bore",
        "groove",
        "pocket",
        "throughProfile",
    ];

    /// <summary>
    /// Schema-reserved / non-production kinds. Soft-skipped so a Fusion job still loads;
    /// source snapshot JSON retains the original features for shop-side review.
    /// </summary>
    static readonly HashSet<string> SoftSkipKinds =
    [
        "counterbore",
        "countersink",
        "edgeRabbet",
        "custom",
    ];

    public static PackageImportResult FromPath(string path)
    {
        if (!File.Exists(path))
            return Fail("path", path, "snapshot file does not exist");

        if (path.EndsWith(".cnjob", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return FromArchive(path);

        return FromJson(File.ReadAllText(path));
    }

    public static PackageImportResult FromArchive(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifestEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
                return Fail("manifest", "manifest.json", ".cnjob is missing manifest.json");

            var manifestJson = ReadEntry(manifestEntry);
            var manifest = JsonSerializer.Deserialize<CnJobManifest>(manifestJson, JsonOpts);
            if (manifest is null || manifest.Format != ManufacturingSnapshot.SchemaName)
                return Fail("format", "manifest.format",
                    $"expected \"{ManufacturingSnapshot.SchemaName}\"");
            if (!IsSupportedVersion(manifest.SchemaVersion))
                return Fail("schemaVersion", "manifest.schemaVersion",
                    $"unsupported snapshot version {manifest.SchemaVersion}");

            var prefix = manifestEntry.FullName[..^"manifest.json".Length];
            var payloadName = string.IsNullOrWhiteSpace(manifest.Payload) ? "snapshot.json" : manifest.Payload;
            var payloadEntry = zip.GetEntry(prefix + payloadName)
                ?? zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/" + payloadName, StringComparison.OrdinalIgnoreCase)
                    || e.FullName.Equals(payloadName, StringComparison.OrdinalIgnoreCase));
            if (payloadEntry is null)
                return Fail("payload", payloadName, $".cnjob is missing {payloadName}");

            return FromJson(ReadEntry(payloadEntry));
        }
        catch (InvalidDataException ex)
        {
            return Fail("archive", path, ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail("json", path, ex.Message);
        }
    }

    public static PackageImportResult FromJson(string json)
    {
        ManufacturingSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ManufacturingSnapshot>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            return Fail("json", "$", ex.Message);
        }

        if (snapshot is null)
            return Fail("root", "$", "snapshot root must be an object");

        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        if (snapshot.Schema != ManufacturingSnapshot.SchemaName)
            errors.Add(new("schema", "$.schema",
                $"expected \"{ManufacturingSnapshot.SchemaName}\" (got {snapshot.Schema})"));
        if (!IsSupportedVersion(snapshot.SchemaVersion))
            errors.Add(new("schemaVersion", "$.schemaVersion",
                $"unsupported snapshot version {snapshot.SchemaVersion}"));
        if (!string.Equals(snapshot.Units, "mm", StringComparison.OrdinalIgnoreCase))
            errors.Add(new("units", "$.units", "manufacturing snapshot units must be mm"));
        if (string.IsNullOrWhiteSpace(snapshot.JobId))
            errors.Add(new("jobId", "$.jobId", "jobId is required"));
        if (snapshot.Workpieces.Count == 0)
            errors.Add(new("workpieces_empty", "$.workpieces", "need at least one workpiece"));

        var panelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var panels = new List<Panel>();
        for (var i = 0; i < snapshot.Workpieces.Count; i++)
        {
            var workpiece = snapshot.Workpieces[i];
            var path = $"$.workpieces[{i}]";
            var panel = ConvertWorkpiece(workpiece, path, errors, warnings);
            if (panel is null) continue;
            if (!panelIds.Add(panel.PanelId))
            {
                // CAD exports often reuse module.BodyN; keep the workpiece and
                // remap the runtime PanelId so the job list still loads.
                var remapped = NextUniquePanelId(panel.PanelId, panelIds);
                warnings.Add(new(
                    "panelId_uniquified",
                    $"{path}.panelId",
                    $"duplicate panelId {panel.PanelId} remapped to {remapped}"));
                panel = ClonePanelWithId(panel, remapped);
                panelIds.Add(remapped);
            }
            panels.Add(panel);
        }

        foreach (var diagnostic in snapshot.Diagnostics.Where(d =>
                     d.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new(
                string.IsNullOrWhiteSpace(diagnostic.Code) ? "source_diagnostic" : diagnostic.Code,
                diagnostic.EntityId ?? "$.diagnostics",
                diagnostic.Message));
        }

        if (errors.Count > 0)
            return new PackageImportResult
            {
                Ok = false,
                Snapshot = snapshot,
                SourceSnapshotJson = json,
                Errors = errors,
                Warnings = warnings,
            };

        return new PackageImportResult
        {
            Ok = true,
            Snapshot = snapshot,
            SourceSnapshotJson = json,
            Package = new CutPackage
            {
                SchemaName = ManufacturingSnapshot.SchemaName,
                Version = 1,
                JobId = snapshot.JobId,
                Units = "mm",
                Sheets = [],
                Panels = panels,
            },
            Errors = errors,
            Warnings = warnings,
        };
    }

    static Panel? ConvertWorkpiece(
        SnapshotWorkpiece workpiece,
        string path,
        List<ValidationIssue> errors,
        List<ValidationIssue> warnings)
    {
        var initialErrorCount = errors.Count;
        var panelId = string.IsNullOrWhiteSpace(workpiece.PanelId)
            ? workpiece.WorkpieceId
            : workpiece.PanelId;
        if (string.IsNullOrWhiteSpace(workpiece.WorkpieceId))
            errors.Add(new("workpieceId", $"{path}.workpieceId", "workpieceId is required"));
        if (string.IsNullOrWhiteSpace(panelId))
            errors.Add(new("panelId", $"{path}.panelId", "panelId or workpieceId is required"));
        if (string.IsNullOrWhiteSpace(workpiece.Material.MaterialId))
            errors.Add(new("materialId", $"{path}.material.materialId", "materialId is required"));
        if (workpiece.Material.ThicknessMm <= 0)
            errors.Add(new("thickness", $"{path}.material.thicknessMm", "thicknessMm must be > 0"));

        var quality = workpiece.Geometry.Quality;
        if (quality is not ("exact" or "tessellated"))
            errors.Add(new("geometry_quality", $"{path}.geometry.quality",
                $"production geometry must be exact or tessellated (got {quality})"));

        var outline = ReadPoints(
            workpiece.Geometry.NestingPolygon.Count >= 3
                ? workpiece.Geometry.NestingPolygon
                : workpiece.Geometry.OuterProfile.Points,
            $"{path}.geometry.outerProfile.points",
            errors,
            minCount: 3);

        var features = new List<PanelFeature>();
        var featureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blindFaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var fi = 0; fi < workpiece.Features.Count; fi++)
        {
            var feature = workpiece.Features[fi];
            var fpath = $"{path}.features[{fi}]";
            if (string.IsNullOrWhiteSpace(feature.FeatureId))
            {
                errors.Add(new("featureId", $"{fpath}.featureId", "featureId is required"));
                continue;
            }
            if (!featureIds.Add(feature.FeatureId))
            {
                errors.Add(new("featureId_duplicate", $"{fpath}.featureId",
                    $"duplicate featureId {feature.FeatureId}"));
                continue;
            }
            var kind = (feature.Kind ?? "").Trim();
            if (!SupportedKinds.Contains(kind))
            {
                var code = SoftSkipKinds.Contains(kind)
                    ? "feature_kind_skipped"
                    : "feature_kind_unsupported";
                warnings.Add(new(code, $"{fpath}.kind",
                    string.IsNullOrEmpty(kind)
                        ? "feature kind missing — skipped"
                        : $"feature kind {kind} skipped (not projected for production NC)"));
                continue;
            }

            var isThrough = feature.Through || kind == "throughProfile";
            var sourceFace = feature.SourceFace.Trim().ToUpperInvariant();
            if (!isThrough && sourceFace is not ("A" or "B"))
            {
                errors.Add(new("feature_face", $"{fpath}.sourceFace",
                    "blind feature sourceFace must be A or B"));
                continue;
            }
            if (!isThrough) blindFaces.Add(sourceFace);
            if (!isThrough && (!feature.DepthMm.HasValue || feature.DepthMm <= 0))
                errors.Add(new("feature_depth", $"{fpath}.depthMm",
                    "blind feature depthMm must be > 0"));
            if (!isThrough && feature.DepthMm > workpiece.Material.ThicknessMm + 0.01)
                errors.Add(new("feature_depth", $"{fpath}.depthMm",
                    "blind feature depth exceeds workpiece thickness"));

            var panelFeature = ConvertFeature(feature, fpath, sourceFace, isThrough, errors);
            if (panelFeature is not null && !IsDuplicateThroughFeature(panelFeature, features))
                features.Add(panelFeature);
        }

        AppendInnerProfileCutouts(workpiece, path, features, featureIds, warnings);

        if (blindFaces.Count > 1)
            errors.Add(new("double_side_unsupported", $"{path}.features",
                "blind features exist on both A and B; CabinetNC supports single-side machining only"));

        var machiningFace = blindFaces.Count == 1 ? blindFaces.Single() : "A";
        if (workpiece.Manufacturing is { } declared)
        {
            if (!declared.Mode.Equals("singleSide", StringComparison.OrdinalIgnoreCase))
                errors.Add(new("manufacturing_mode", $"{path}.manufacturing.mode",
                    "only singleSide manufacturing is supported"));
            var declaredFace = declared.MachiningFace?.Trim().ToUpperInvariant();
            if (blindFaces.Count == 1 && declaredFace is ("A" or "B") && declaredFace != machiningFace)
                errors.Add(new("machining_face_mismatch", $"{path}.manufacturing.machiningFace",
                    $"declared face {declaredFace} conflicts with feature face {machiningFace}"));
            if (blindFaces.Count == 0 && declaredFace is ("A" or "B"))
                machiningFace = declaredFace;
        }

        var faces = workpiece.Faces.Select(f => new WorkpieceFace
        {
            FaceId = f.FaceId.Trim().ToUpperInvariant(),
            Role = f.Role,
            FinishId = f.Finish?.FinishId,
            FinishName = f.Finish?.FinishName,
            MachiningPermission = f.MachiningPermission,
        }).ToList();
        if (faces.Count == 0)
            warnings.Add(new("faces_missing", $"{path}.faces",
                "no A/B finish metadata supplied"));

        // Runtime projection always mills Snapshot A (no dual-face / flip NC).
        if (machiningFace == "B")
        {
            faces = RemapFacesSwapAb(faces);
            features = RemapFeaturesSwapAb(features);
            machiningFace = "A";
            warnings.Add(new("machining_face_normalized", path,
                "machining face remapped to Snapshot A for single-side runtime"));
        }
        else
        {
            machiningFace = "A";
        }

        faces = EnsureMachiningFaceAllowed(faces, warnings, path);

        if (errors.Count > initialErrorCount) return null;

        return new Panel
        {
            PanelId = panelId!,
            // Lay Flat export used to stamp the work-zone container as name;
            // prefer panelId (source@layflat-…) for shop lists until re-export.
            Name = Panel.IsLayFlatPlaceholder(workpiece.Name) ? null : workpiece.Name,
            Material = workpiece.Material.MaterialId,
            ThicknessMm = workpiece.Material.ThicknessMm,
            DecorId = NullIfEmpty(workpiece.Material.DecorId),
            SubstrateId = NullIfEmpty(workpiece.Material.SubstrateId),
            ColorName = NullIfEmpty(workpiece.Material.ColorName),
            SurfaceMode = NullIfEmpty(workpiece.Material.SurfaceMode),
            Quantity = Math.Max(1, workpiece.Quantity),
            Outline = new Outline
            {
                Points = outline,
                Closed = true,
                Frame = "panelLocal",
            },
            Features = features,
            Faces = faces,
            Identity = new WorkpieceIdentity
            {
                ProjectId = workpiece.Identity.ProjectId,
                ModuleId = workpiece.Identity.ModuleId,
                WorkpieceId = workpiece.WorkpieceId,
                Role = NullIfEmpty(workpiece.Identity.Role),
                SourceFormat = ManufacturingSnapshot.SchemaName,
            },
            Orientation = new WorkpieceOrientation
            {
                PrimaryFace = "A",
                MillingFace = "A",
                AllowMirror = false,
            },
            Side = "A",
            Notes = NullIfEmpty(workpiece.Identity.Role),
        };
    }

    static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Fusion currently emits through cutouts as features; if <c>innerProfiles</c>
    /// is populated, project each closed ring as a throughCutout so nesting/CAM see them.
    /// </summary>
    static void AppendInnerProfileCutouts(
        SnapshotWorkpiece workpiece,
        string path,
        List<PanelFeature> features,
        HashSet<string> featureIds,
        List<ValidationIssue> warnings)
    {
        var inners = workpiece.Geometry.InnerProfiles;
        if (inners.Count == 0) return;

        for (var i = 0; i < inners.Count; i++)
        {
            var profile = inners[i];
            var ipath = $"{path}.geometry.innerProfiles[{i}]";
            var localErrors = new List<ValidationIssue>();
            var points = ReadPoints(profile.Points, $"{ipath}.points", localErrors, minCount: 3);
            if (localErrors.Count > 0 || points.Count < 3)
            {
                warnings.Add(new("inner_profile_skipped", ipath,
                    "innerProfile skipped — need at least 3 valid points"));
                continue;
            }

            var featureId = NextUniqueFeatureId($"inner-{i + 1}", featureIds);
            featureIds.Add(featureId);
            features.Add(new PanelFeature
            {
                FeatureId = featureId,
                Kind = "throughCutout",
                FaceId = "THROUGH",
                Through = true,
                Purpose = "innerProfile",
                X = points[0].X,
                Y = points[0].Y,
                Path = points,
            });
            warnings.Add(new("inner_profile_projected", ipath,
                $"innerProfile projected as throughCutout {featureId}"));
        }
    }

    static string NextUniqueFeatureId(string baseId, HashSet<string> used)
    {
        var root = string.IsNullOrWhiteSpace(baseId) ? "inner" : baseId.Trim();
        if (!used.Contains(root)) return root;
        var suffix = 2;
        while (true)
        {
            var candidate = $"{root}__{suffix}";
            if (!used.Contains(candidate)) return candidate;
            suffix++;
        }
    }

    static List<WorkpieceFace> RemapFacesSwapAb(IReadOnlyList<WorkpieceFace> faces) =>
        faces.Select(f => new WorkpieceFace
        {
            FaceId = f.FaceId.Equals("A", StringComparison.OrdinalIgnoreCase) ? "B"
                : f.FaceId.Equals("B", StringComparison.OrdinalIgnoreCase) ? "A"
                : f.FaceId,
            Role = f.Role,
            FinishId = f.FinishId,
            FinishName = f.FinishName,
            MachiningPermission = f.MachiningPermission,
        }).ToList();

    static List<WorkpieceFace> EnsureMachiningFaceAllowed(
        List<WorkpieceFace> faces,
        List<ValidationIssue> warnings,
        string path)
    {
        var updated = new List<WorkpieceFace>(faces.Count);
        foreach (var face in faces)
        {
            if (face.FaceId.Equals("A", StringComparison.OrdinalIgnoreCase)
                && face.MachiningPermission?.Equals("NOT_ALLOWED", StringComparison.OrdinalIgnoreCase) == true)
            {
                warnings.Add(new("machining_permission_upgraded", $"{path}.faces",
                    "face A permission upgraded from NOT_ALLOWED because it is the machining face"));
                updated.Add(new WorkpieceFace
                {
                    FaceId = face.FaceId,
                    Role = face.Role,
                    FinishId = face.FinishId,
                    FinishName = face.FinishName,
                    MachiningPermission = "PRIMARY",
                });
            }
            else
            {
                updated.Add(face);
            }
        }
        return updated;
    }

    static List<PanelFeature> RemapFeaturesSwapAb(IReadOnlyList<PanelFeature> features) =>
        features.Select(f =>
        {
            var face = f.FaceId?.Trim().ToUpperInvariant();
            var remapped = face switch
            {
                "A" => "B",
                "B" => "A",
                _ => f.FaceId,
            };
            return new PanelFeature
            {
                FeatureId = f.FeatureId,
                Kind = f.Kind,
                FaceId = remapped,
                Through = f.Through,
                GroupId = f.GroupId,
                Purpose = f.Purpose,
                SourceRelationshipId = f.SourceRelationshipId,
                X = f.X,
                Y = f.Y,
                DiameterMm = f.DiameterMm,
                DepthMm = f.DepthMm,
                WidthMm = f.WidthMm,
                Path = f.Path,
                Profile = f.Profile,
            };
        }).ToList();

    static PanelFeature? ConvertFeature(
        SnapshotFeature feature,
        string path,
        string sourceFace,
        bool through,
        List<ValidationIssue> errors)
    {
        // Legacy Fusion exports stamped lock cutouts as groove+through; treat as cutouts.
        var asThroughCutout = through && feature.Kind is "groove" or "throughProfile";

        var kind = feature.Kind switch
        {
            "bore" => "holeVertical",
            "groove" => asThroughCutout ? "throughCutout" : "grooveVertical",
            "pocket" => "pocket",
            "throughProfile" => "throughCutout",
            _ => feature.Kind,
        };

        var x = 0d;
        var y = 0d;
        IReadOnlyList<Point2>? featurePath = null;
        IReadOnlyList<Point2>? profile = null;
        if (feature.Kind == "bore")
        {
            if (feature.Geometry.Center is not { Count: >= 2 }
                || !feature.Geometry.DiameterMm.HasValue
                || feature.Geometry.DiameterMm <= 0)
            {
                errors.Add(new("bore_geometry", $"{path}.geometry",
                    "bore requires center and diameterMm > 0"));
                return null;
            }
            x = feature.Geometry.Center[0];
            y = feature.Geometry.Center[1];
        }
        else if (asThroughCutout)
        {
            var ring = ReadOptionalProfile(feature, path);
            if (ring.Count < 3)
            {
                var centerlineErrors = new List<ValidationIssue>();
                var centerline = ReadPoints(feature.Geometry.Centerline ?? [],
                    $"{path}.geometry.centerline", centerlineErrors, minCount: 2);
                var width = feature.Geometry.WidthMm ?? 0;
                ring = GrooveGeometry.OutlineFromCenterline(centerline, width).ToList();
                if (ring.Count < 3)
                {
                    errors.Add(new("through_cutout_geometry", $"{path}.geometry",
                        "through cutout requires a closed profile (or centerline+width)"));
                    return null;
                }
            }
            featurePath = ring;
            profile = ring;
            x = ring[0].X;
            y = ring[0].Y;
        }
        else if (feature.Kind == "groove")
        {
            featurePath = ReadPoints(feature.Geometry.Centerline ?? [], $"{path}.geometry.centerline",
                errors, minCount: 2);
            if (feature.Geometry.WidthMm is null or <= 0)
                errors.Add(new("groove_width", $"{path}.geometry.widthMm",
                    "groove widthMm must be > 0"));
            if (featurePath.Count > 0)
            {
                x = featurePath[0].X;
                y = featurePath[0].Y;
            }
            if (feature.Geometry.Profile?.Points is { Count: > 0 })
            {
                var profileErrors = new List<ValidationIssue>();
                var ring = ReadPoints(feature.Geometry.Profile.Points, $"{path}.geometry.profile.points",
                    profileErrors, minCount: 3);
                if (profileErrors.Count == 0 && ring.Count >= 3)
                    profile = ring;
            }
        }
        else
        {
            featurePath = ReadPoints(feature.Geometry.Profile?.Points ?? [],
                $"{path}.geometry.profile.points", errors, minCount: 3);
            if (featurePath.Count > 0)
            {
                x = featurePath[0].X;
                y = featurePath[0].Y;
            }
        }

        return new PanelFeature
        {
            FeatureId = feature.FeatureId,
            Kind = kind,
            FaceId = through ? "THROUGH" : sourceFace,
            Through = through || asThroughCutout,
            GroupId = feature.GroupId,
            Purpose = feature.Intent?.Purpose,
            SourceRelationshipId = feature.Intent?.SourceRelationshipId,
            X = x,
            Y = y,
            DiameterMm = feature.Geometry.DiameterMm,
            DepthMm = feature.DepthMm,
            WidthMm = asThroughCutout ? null : feature.Geometry.WidthMm,
            Path = featurePath,
            Profile = profile,
        };
    }

    static List<Point2> ReadOptionalProfile(SnapshotFeature feature, string path)
    {
        if (feature.Geometry.Profile?.Points is not { Count: > 0 })
            return [];
        var soft = new List<ValidationIssue>();
        var ring = ReadPoints(feature.Geometry.Profile.Points, $"{path}.geometry.profile.points",
            soft, minCount: 3);
        // Profile is optional for legacy groove+through; don't hard-fail here.
        return soft.Count == 0 && ring.Count >= 3 ? ring : [];
    }

    static bool IsDuplicateThroughFeature(PanelFeature candidate, List<PanelFeature> existing)
    {
        if (!candidate.Through) return false;
        var ring = candidate.Profile ?? candidate.Path;
        if (ring is not { Count: >= 3 }) return false;
        var (cx, cy, area) = FeatureCentroidArea(ring);
        foreach (var other in existing)
        {
            if (!other.Through) continue;
            var otherRing = other.Profile ?? other.Path;
            if (otherRing is not { Count: >= 3 }) continue;
            var (ox, oy, oArea) = FeatureCentroidArea(otherRing);
            if (Math.Abs(cx - ox) <= 0.5
                && Math.Abs(cy - oy) <= 0.5
                && Math.Abs(area - oArea) <= Math.Max(0.5, area * 0.05))
                return true;
        }
        return false;
    }

    static (double Cx, double Cy, double Area) FeatureCentroidArea(IReadOnlyList<Point2> ring)
    {
        var cx = ring.Average(p => p.X);
        var cy = ring.Average(p => p.Y);
        var area = 0d;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return (cx, cy, Math.Abs(area) * 0.5);
    }

    static List<Point2> ReadPoints(
        IReadOnlyList<List<double>> raw,
        string path,
        List<ValidationIssue> errors,
        int minCount)
    {
        var points = new List<Point2>();
        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i].Count < 2
                || !double.IsFinite(raw[i][0])
                || !double.IsFinite(raw[i][1]))
            {
                errors.Add(new("point", $"{path}[{i}]", "point must contain two finite numbers"));
                continue;
            }
            points.Add(new Point2(raw[i][0], raw[i][1]));
        }
        if (points.Count < minCount)
            errors.Add(new("points", path, $"need at least {minCount} valid points"));
        return points;
    }

    static bool IsSupportedVersion(string version) =>
        Version.TryParse(version, out var parsed) && parsed.Major == 1;

    static string NextUniquePanelId(string baseId, HashSet<string> used)
    {
        var root = string.IsNullOrWhiteSpace(baseId) ? "panel" : baseId.Trim();
        var suffix = 2;
        while (true)
        {
            var candidate = $"{root}__{suffix}";
            if (!used.Contains(candidate))
                return candidate;
            suffix++;
        }
    }

    static Panel ClonePanelWithId(Panel panel, string panelId) =>
        new()
        {
            PanelId = panelId,
            Name = panel.Name,
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = panel.Outline,
            Features = panel.Features,
            Faces = panel.Faces,
            Identity = panel.Identity is null
                ? null
                : new WorkpieceIdentity
                {
                    ProjectId = panel.Identity.ProjectId,
                    ModuleId = panel.Identity.ModuleId,
                    // Keep CAD workpieceId stable; only runtime PanelId is uniquified.
                    WorkpieceId = panel.Identity.WorkpieceId,
                    Role = panel.Identity.Role,
                    SourceFormat = panel.Identity.SourceFormat,
                },
            Orientation = panel.Orientation,
            EdgeBanding = panel.EdgeBanding,
            Notes = panel.Notes,
            Side = panel.Side,
        };

    static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static PackageImportResult Fail(string code, string path, string message) =>
        new()
        {
            Ok = false,
            Errors = [new ValidationIssue(code, path, message)],
        };
}

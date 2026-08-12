namespace CabinetNC.FusionPackage;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Materials;
using CabinetNC.Domain.Parts;

/// <summary>Import cabinetnc.woodjob (folder or .zip) into runtime CutPackage.</summary>
public static class WoodJobImporter
{
    public static bool LooksLikeWoodJobDir(string dir) =>
        Directory.Exists(dir) && File.Exists(Path.Combine(dir, "manifest.json"));

    public static bool LooksLikeWoodJobZip(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;
        // Require woodjob format — a manufacturing-snapshot zip also has manifest.json.
        var format = PackageImporter.PeekZipManifestFormat(path);
        return string.Equals(format, CutPackage.WoodJobFormat, StringComparison.Ordinal);
    }

    public static PackageImportResult FromPath(string path)
    {
        if (Directory.Exists(path))
            return FromDirectory(path);
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return FromZip(path);
        return Fail("path", path, "woodjob expects a folder or .zip containing manifest.json");
    }

    public static PackageImportResult FromZip(string zipPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cabinetnc-woodjob-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            ZipFile.ExtractToDirectory(zipPath, tmp);
            // some zips nest one root folder
            var root = tmp;
            if (!File.Exists(Path.Combine(tmp, "manifest.json")))
            {
                var sub = Directory.GetDirectories(tmp);
                if (sub.Length == 1 && File.Exists(Path.Combine(sub[0], "manifest.json")))
                    root = sub[0];
            }
            return FromDirectory(root);
        }
        catch (Exception ex)
        {
            return Fail("zip", zipPath, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* ponytail: best-effort temp cleanup */ }
        }
    }

    public static PackageImportResult FromDirectory(string dir)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
            return Fail("manifest", "manifest.json", "missing manifest.json");

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = manifestDoc.RootElement;
        var format = TryGetString(manifest, "format");
        if (format != CutPackage.WoodJobFormat)
            errors.Add(new("format", "manifest.format", $"expected \"{CutPackage.WoodJobFormat}\" (got {format})"));

        if (manifest.TryGetProperty("encryption", out var enc)
            && TryGetString(enc, "mode") is { } mode
            && !string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new("encryption", "manifest.encryption.mode", $"encrypted woodjob not supported yet (mode={mode})"));
        }

        var schemaVersion = manifest.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var svn)
            ? svn
            : CutPackage.WoodJobSchemaVersion;
        if (schemaVersion != CutPackage.WoodJobSchemaVersion)
            warnings.Add(new("schemaVersion", "manifest.schemaVersion",
                $"schemaVersion {schemaVersion} — importer targets v{CutPackage.WoodJobSchemaVersion}"));

        VerifyChecksums(dir, warnings, errors);

        var materialsPath = Path.Combine(dir, "materials.json");
        var sheetsPath = Path.Combine(dir, "sheets.json");
        var partsPath = Path.Combine(dir, "parts.json");
        if (!File.Exists(partsPath))
            return Fail("parts", "parts.json", "missing parts.json");

        var matThickness = new Dictionary<string, double>(StringComparer.Ordinal);
        if (File.Exists(materialsPath))
        {
            using var matDoc = JsonDocument.Parse(File.ReadAllText(materialsPath));
            if (matDoc.RootElement.TryGetProperty("materials", out var mats) && mats.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mats.EnumerateArray())
                {
                    var id = TryGetString(m, "materialId");
                    if (!string.IsNullOrEmpty(id))
                        matThickness[id] = GetDouble(m, "thicknessMm");
                }
            }
        }

        var sheets = new List<SheetStock>();
        if (File.Exists(sheetsPath))
        {
            using var sheetDoc = JsonDocument.Parse(File.ReadAllText(sheetsPath));
            var listEl = sheetDoc.RootElement.TryGetProperty("sheetTypes", out var st) ? st
                : sheetDoc.RootElement.TryGetProperty("sheets", out var sh) ? sh
                : default;
            if (listEl.ValueKind == JsonValueKind.Array)
            {
                var si = 0;
                foreach (var s in listEl.EnumerateArray())
                {
                    var materialId = TryGetString(s, "materialId");
                    var thickness = materialId is not null && matThickness.TryGetValue(materialId, out var t) ? t : 0;

                    sheets.Add(new SheetStock
                    {
                        SheetId = TryGetString(s, "sheetId") ?? $"S{si}",
                        Material = materialId,
                        ThicknessMm = thickness,
                        WidthMm = GetDouble(s, "widthMm"),
                        LengthMm = TryGetDouble(s, "heightMm") ?? GetDouble(s, "lengthMm"),
                        MarginMm = GetDouble(s, "marginMm"),
                        KerfMm = GetDouble(s, "kerfMm"),
                        PartClearanceMm = GetDouble(s, "partClearanceMm"),
                        DefectRegions = ReadDefects(s),
                    });
                    si++;
                }
            }
        }

        if (File.Exists(Path.Combine(dir, "relationships.json")))
            warnings.Add(new("relationships", "relationships.json", "relationships loaded as metadata-only (not used in nest/CAM yet)"));

        using var partsDoc = JsonDocument.Parse(File.ReadAllText(partsPath));
        if (!partsDoc.RootElement.TryGetProperty("parts", out var partsEl) || partsEl.ValueKind != JsonValueKind.Array
            || partsEl.GetArrayLength() == 0)
        {
            errors.Add(new("parts_empty", "parts.parts", "parts[] is empty — need at least one part"));
        }

        var panels = new List<Panel>();
        if (partsEl.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var p in partsEl.EnumerateArray())
            {
                var path = $"parts[{i}]";
                var panelId = TryGetString(p, "panelId") ?? $"P{i}";
                var materialId = TryGetString(p, "materialId");
                var thickness = GetDouble(p, "thicknessMm");
                if (thickness <= 0 && materialId is not null && matThickness.TryGetValue(materialId, out var tMm))
                    thickness = tMm;

                if (!p.TryGetProperty("geometry", out var geom) || geom.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new("geometry", $"{path}.geometry", "missing geometry"));
                    i++;
                    continue;
                }

                var points = ReadOutlinePoints(geom, $"{path}.geometry", errors, warnings);
                if (points.Count < 3)
                {
                    errors.Add(new("outline", $"{path}.geometry", "need ≥3 outline points (nestingPolygon or edges)"));
                    i++;
                    continue;
                }

                var innerById = new Dictionary<string, IReadOnlyList<Point2>>(StringComparer.Ordinal);
                if (geom.TryGetProperty("innerContours", out var inners) && inners.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ic in inners.EnumerateArray())
                    {
                        var id = TryGetString(ic, "id");
                        if (id is null) continue;
                        var poly = ReadPointArray(ic, "polygon");
                        if (poly.Count >= 3) innerById[id] = poly;
                    }
                }

                var features = ReadFeatures(p, path, innerById, warnings);
                IReadOnlyList<int>? rotations = null;
                string? grain = null;
                if (p.TryGetProperty("orientation", out var ori) && ori.ValueKind == JsonValueKind.Object)
                {
                    grain = TryGetString(ori, "grainDirection");
                    if (ori.TryGetProperty("allowedRotations", out var ar) && ar.ValueKind == JsonValueKind.Array)
                    {
                        var rs = new List<int>();
                        foreach (var r in ar.EnumerateArray())
                            if (r.TryGetInt32(out var deg)) rs.Add(deg);
                        if (rs.Count > 0) rotations = rs;
                    }
                }

                var qty = p.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) ? Math.Max(1, q) : 1;

                string? primaryFace = null;
                string? millingFace = null;
                var allowMirror = false;
                string? flipStrategy = null;
                if (p.TryGetProperty("orientation", out var oriFull) && oriFull.ValueKind == JsonValueKind.Object)
                {
                    primaryFace = TryGetString(oriFull, "primaryFace") ?? TryGetString(oriFull, "faceUp");
                    millingFace = TryGetString(oriFull, "millingFace") ?? TryGetString(oriFull, "millingSurface")
                        ?? TryGetString(oriFull, "fromFace");
                    allowMirror = oriFull.TryGetProperty("allowMirror", out var am) && am.ValueKind == JsonValueKind.True;
                    flipStrategy = TryGetString(oriFull, "flipStrategy");
                    grain ??= TryGetString(oriFull, "grainDirection");
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

                if (thickness <= 0)
                    errors.Add(new("thickness", $"{path}.thicknessMm", "thicknessMm must be > 0 (set on part or materials.json)"));

                var projectId = TryGetString(p, "projectId");
                var moduleId = TryGetString(p, "moduleId");
                var workpieceId = TryGetString(p, "workpieceId") ?? panelId;
                var side = TryGetString(p, "side") ?? primaryFace ?? millingFace;
                var notes = TryGetString(p, "notes");

                panels.Add(new Panel
                {
                    PanelId = panelId,
                    Name = TryGetString(p, "name"),
                    Material = materialId,
                    ThicknessMm = thickness,
                    Quantity = qty,
                    AllowedRotations = rotations,
                    GrainDirection = grain,
                    Outline = new Outline { Points = points, Closed = true, Frame = "panelLocal" },
                    Features = features,
                    Identity = new WorkpieceIdentity
                    {
                        ProjectId = projectId,
                        ModuleId = moduleId,
                        WorkpieceId = workpieceId,
                        SourcePath = partsPath,
                        SourceFormat = CutPackage.WoodJobFormat,
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
                    Notes = notes,
                    Side = side,
                });
                i++;
            }
        }

        string? jobId = TryGetString(manifest, "jobId");
        var jobPath = Path.Combine(dir, "job.json");
        if (File.Exists(jobPath))
        {
            using var jobDoc = JsonDocument.Parse(File.ReadAllText(jobPath));
            jobId ??= TryGetString(jobDoc.RootElement, "jobId");
        }

        if (errors.Count > 0)
            return new PackageImportResult { Ok = false, Errors = errors, Warnings = warnings };

        return new PackageImportResult
        {
            Ok = true,
            Package = new CutPackage
            {
                SchemaName = CutPackage.WoodJobFormat,
                Version = schemaVersion,
                JobId = jobId,
                Units = TryGetString(manifest, "coordinateUnit") ?? "mm",
                Sheets = sheets,
                Panels = panels,
            },
            Errors = errors,
            Warnings = warnings,
        };
    }

    static void VerifyChecksums(string dir, List<ValidationIssue> warnings, List<ValidationIssue> errors)
    {
        var path = Path.Combine(dir, "checksums.json");
        if (!File.Exists(path))
        {
            warnings.Add(new("checksums", "checksums.json", "missing checksums.json — skipped integrity check"));
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in files.EnumerateObject())
        {
            var filePath = Path.Combine(dir, prop.Name);
            if (!File.Exists(filePath))
            {
                errors.Add(new("checksum_missing", prop.Name, $"checksum lists missing file {prop.Name}"));
                continue;
            }
            var expected = prop.Value.GetString();
            if (string.IsNullOrEmpty(expected)) continue;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                errors.Add(new("checksum", prop.Name, $"SHA-256 mismatch for {prop.Name}"));
        }
    }

    static List<Point2> ReadOutlinePoints(JsonElement geom, string path, List<ValidationIssue> errors, List<ValidationIssue> warnings)
    {
        var nest = ReadPointArray(geom, "nestingPolygon");
        if (nest.Count >= 3) return nest;

        if (geom.TryGetProperty("outerContour", out var outer)
            && outer.TryGetProperty("edges", out var edges)
            && edges.ValueKind == JsonValueKind.Array)
        {
            warnings.Add(new("tessellate", $"{path}.outerContour", "no nestingPolygon — tessellating edges"));
            return TessellateEdges(edges);
        }

        errors.Add(new("outline", path, "missing nestingPolygon and outerContour.edges"));
        return [];
    }

    static List<Point2> TessellateEdges(JsonElement edges, int arcSegments = 12)
    {
        var pts = new List<Point2>();
        void AddUnique(Point2 p)
        {
            if (pts.Count == 0 || Dist2(pts[^1], p) > 1e-8) pts.Add(p);
        }

        foreach (var e in edges.EnumerateArray())
        {
            var type = TryGetString(e, "type") ?? "line";
            var start = ReadXy(e, "start");
            var end = ReadXy(e, "end");
            if (start is null || end is null) continue;
            AddUnique(start.Value);

            if (type.Equals("arc", StringComparison.OrdinalIgnoreCase))
            {
                var center = ReadXy(e, "center");
                if (center is null) { AddUnique(end.Value); continue; }
                var cw = e.TryGetProperty("clockwise", out var c) && c.ValueKind == JsonValueKind.True;
                var a0 = Math.Atan2(start.Value.Y - center.Value.Y, start.Value.X - center.Value.X);
                var a1 = Math.Atan2(end.Value.Y - center.Value.Y, end.Value.X - center.Value.X);
                var sweep = a1 - a0;
                if (cw)
                {
                    while (sweep > 0) sweep -= 2 * Math.PI;
                    if (Math.Abs(sweep) < 1e-9) sweep = -2 * Math.PI;
                }
                else
                {
                    while (sweep < 0) sweep += 2 * Math.PI;
                    if (Math.Abs(sweep) < 1e-9) sweep = 2 * Math.PI;
                }
                var r = Math.Sqrt(Dist2(start.Value, center.Value));
                for (var s = 1; s < arcSegments; s++)
                {
                    var t = (double)s / arcSegments;
                    var a = a0 + sweep * t;
                    AddUnique(new Point2(center.Value.X + r * Math.Cos(a), center.Value.Y + r * Math.Sin(a)));
                }
            }

            AddUnique(end.Value);
        }

        if (pts.Count >= 2 && Dist2(pts[0], pts[^1]) < 1e-6)
            pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    static List<PanelFeature> ReadFeatures(
        JsonElement part,
        string path,
        Dictionary<string, IReadOnlyList<Point2>> innerById,
        List<ValidationIssue> warnings)
    {
        var features = new List<PanelFeature>();
        if (!part.TryGetProperty("features", out var feats) || feats.ValueKind != JsonValueKind.Array)
            return features;

        var fi = 0;
        foreach (var f in feats.EnumerateArray())
        {
            var fpath = $"{path}.features[{fi}]";
            var rawType = TryGetString(f, "featureType") ?? TryGetString(f, "kind") ?? "unknown";
            var kind = MapFeatureKind(rawType);
            double x = 0, y = 0;
            IReadOnlyList<Point2>? featPath = null;

            if (f.TryGetProperty("center", out var center) && center.ValueKind == JsonValueKind.Array && center.GetArrayLength() >= 2)
            {
                x = center[0].GetDouble();
                y = center[1].GetDouble();
            }
            else
            {
                x = GetDouble(f, "x");
                y = GetDouble(f, "y");
            }

            if (f.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.Array)
            {
                var pts = ReadPointArrayElement(pathEl);
                if (pts.Count >= 2) featPath = pts;
            }

            var geoRef = TryGetString(f, "geometryRef");
            if (geoRef is not null)
            {
                if (innerById.TryGetValue(geoRef, out var poly))
                    featPath = poly;
                else
                    warnings.Add(new("geometryRef", fpath, $"geometryRef '{geoRef}' not found in innerContours"));
            }

            if (kind.Contains("pocket", StringComparison.OrdinalIgnoreCase) && featPath is null)
                warnings.Add(new("pocket", fpath, "pocket without path — ignored for CAM until geometry provided"));

            features.Add(new PanelFeature
            {
                FeatureId = TryGetString(f, "featureId") ?? TryGetString(f, "id") ?? $"F{fi}",
                Kind = kind,
                FaceId = TryGetString(f, "faceId") ?? TryGetString(f, "sourceFace") ?? TryGetString(f, "fromFace"),
                Through = (f.TryGetProperty("through", out var throughEl) && throughEl.ValueKind == JsonValueKind.True)
                    || string.Equals(TryGetString(f, "cutType"), "FULL", StringComparison.OrdinalIgnoreCase),
                GroupId = TryGetString(f, "groupId"),
                Purpose = TryGetString(f, "purpose"),
                SourceRelationshipId = TryGetString(f, "sourceRelationshipId"),
                X = x,
                Y = y,
                DiameterMm = TryGetDouble(f, "diameterMm"),
                DepthMm = TryGetDouble(f, "depthMm"),
                WidthMm = TryGetDouble(f, "widthMm"),
                Path = featPath,
            });
            fi++;
        }
        return features;
    }

    /// <summary>Map woodjob featureType → runtime kind used by Ops/edit.</summary>
    public static string MapFeatureKind(string featureType) => featureType switch
    {
        "drill" => "holeVertical",
        "groove" => "grooveVertical",
        "throughCutout" => "throughCutout",
        "pocket" => "pocket",
        _ => featureType,
    };

    static List<Point2> ReadPointArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return ReadPointArrayElement(el);
    }

    static List<Point2> ReadPointArrayElement(JsonElement el)
    {
        var pts = new List<Point2>();
        foreach (var pt in el.EnumerateArray())
        {
            if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
                pts.Add(new Point2(pt[0].GetDouble(), pt[1].GetDouble()));
            else if (pt.ValueKind == JsonValueKind.Object)
                pts.Add(new Point2(GetDouble(pt, "x"), GetDouble(pt, "y")));
        }
        return pts;
    }

    static Point2? ReadXy(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2)
            return null;
        return new Point2(p[0].GetDouble(), p[1].GetDouble());
    }

    static double Dist2(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    static List<DefectRegion> ReadDefects(JsonElement sheet)
    {
        var list = new List<DefectRegion>();
        if (!sheet.TryGetProperty("defectRegions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var d in arr.EnumerateArray())
        {
            var id = TryGetString(d, "id") ?? $"DEF-{list.Count + 1}";
            if (!d.TryGetProperty("polygon", out var poly) || poly.ValueKind != JsonValueKind.Array)
                continue;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var any = false;
            foreach (var pt in poly.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                var x = pt[0].GetDouble();
                var y = pt[1].GetDouble();
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                any = true;
            }
            if (any)
                list.Add(new DefectRegion { Id = id, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY });
        }
        return list;
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

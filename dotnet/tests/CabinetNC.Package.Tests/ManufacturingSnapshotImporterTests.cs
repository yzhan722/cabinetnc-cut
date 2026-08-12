using System.IO.Compression;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.FusionPackage;

namespace CabinetNC.Package.Tests;

public class ManufacturingSnapshotImporterTests
{
    [Fact]
    public void Imports_fusion_layflat_contract_with_all_supported_features()
    {
        var json = SnapshotJson(
            """
            [
              {"featureId":"H1","kind":"bore","sourceFace":"A","geometry":{"center":[20,30],"diameterMm":5},"depthMm":12,"through":false},
              {"featureId":"G1","kind":"groove","sourceFace":"A","geometry":{"centerline":[[10,15],[90,15]],"widthMm":6},"depthMm":8,"through":false},
              {"featureId":"P1","kind":"pocket","sourceFace":"A","geometry":{"profile":{"closed":true,"points":[[30,20],[60,20],[60,40],[30,40]]}},"depthMm":5,"through":false},
              {"featureId":"T1","kind":"throughProfile","sourceFace":"THROUGH","geometry":{"profile":{"closed":true,"points":[[70,20],[85,20],[85,35],[70,35]]}},"through":true}
            ]
            """);

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        var panel = Assert.Single(result.Package!.Panels);
        Assert.Equal(4, panel.Features.Count);
        Assert.Contains(panel.Features, f => f.Kind == "holeVertical");
        Assert.Contains(panel.Features, f => f.Kind == "grooveVertical");
        Assert.Contains(panel.Features, f => f.Kind == "pocket");
        Assert.Contains(panel.Features, f => f.Kind == "throughCutout" && f.Through);

        var ops = OpsPlanner.FeaturesToOps(result.Package.Panels);
        Assert.Contains(ops, op => op.Op == "drill" && op.FeatureId == "H1");
        Assert.Contains(ops, op => op.Op == "groove" && op.FeatureId == "G1");
        Assert.Contains(ops, op => op.Op == "pocket" && op.FeatureId == "P1");
        Assert.Contains(ops, op => op.Op == "contour" && op.FeatureId == "T1");
    }

    [Fact]
    public void Imports_single_side_snapshot_and_preserves_source()
    {
        var json = SnapshotJson(
            """
            [
              {
                "featureId":"H1",
                "kind":"bore",
                "sourceFace":"A",
                "geometry":{"center":[20,30],"diameterMm":5},
                "depthMm":12,
                "through":false,
                "intent":{"purpose":"connector","sourceRelationshipId":"REL-1"}
              },
              {
                "featureId":"G1",
                "kind":"groove",
                "sourceFace":"A",
                "geometry":{"centerline":[[10,15],[90,15]],"widthMm":6},
                "depthMm":8,
                "through":false
              }
            ]
            """);

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal(json, result.SourceSnapshotJson);
        Assert.NotNull(result.Snapshot);
        var panel = Assert.Single(result.Package!.Panels);
        Assert.Equal("cabinetnc.manufacturing-snapshot", result.Package.SchemaName);
        Assert.Equal("A", panel.Side);
        Assert.Equal("A", panel.Orientation!.MillingFace);
        Assert.Equal(2, panel.Faces.Count);
        var hole = panel.Features.Single(f => f.FeatureId == "H1");
        Assert.Equal("holeVertical", hole.Kind);
        Assert.Equal("A", hole.FaceId);
        Assert.Equal("connector", hole.Purpose);
        Assert.Equal("REL-1", hole.SourceRelationshipId);
    }

    [Fact]
    public void Remaps_blind_B_features_to_snapshot_A()
    {
        var json = SnapshotJson(
            """
            [
              {"featureId":"H1","kind":"bore","sourceFace":"B","geometry":{"center":[20,30],"diameterMm":5},"depthMm":12,"through":false}
            ]
            """,
            machiningFace: "B");

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        var panel = Assert.Single(result.Package!.Panels);
        Assert.Equal("A", panel.Side);
        Assert.Equal("A", panel.Orientation!.MillingFace);
        Assert.Equal("A", panel.Features[0].FaceId);
        Assert.Contains(result.Warnings, w => w.Code == "machining_face_normalized");
        // Original sample: A=PRIMARY, B=ALLOWED → after swap machining face carries ALLOWED onto A.
        Assert.Equal("ALLOWED", panel.Faces.Single(f => f.FaceId == "A").MachiningPermission);
        Assert.Equal("PRIMARY", panel.Faces.Single(f => f.FaceId == "B").MachiningPermission);
    }

    [Fact]
    public void Rejects_blind_features_on_both_faces()
    {
        var json = SnapshotJson(
            """
            [
              {"featureId":"A1","kind":"bore","sourceFace":"A","geometry":{"center":[10,10],"diameterMm":5},"depthMm":5,"through":false},
              {"featureId":"B1","kind":"bore","sourceFace":"B","geometry":{"center":[20,10],"diameterMm":5},"depthMm":5,"through":false}
            ]
            """);

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Code == "double_side_unsupported");
    }

    [Fact]
    public void Rejects_bbox_fallback_as_production_geometry()
    {
        var json = SnapshotJson("[]", geometryQuality: "bboxFallback");

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Code == "geometry_quality");
    }

    [Fact]
    public void Remaps_duplicate_panel_ids_with_warning()
    {
        var json = """
        {
          "schema":"cabinetnc.manufacturing-snapshot",
          "schemaVersion":"1.0.0",
          "jobId":"DUP-TEST",
          "units":"mm",
          "workpieces":[
            {
              "workpieceId":"manual.Body1",
              "panelId":"manual.Body1",
              "name":"Side A",
              "material":{"materialId":"PB-WHITE-18","thicknessMm":18},
              "geometry":{
                "quality":"tessellated",
                "outerProfile":{"closed":true,"points":[[0,0],[100,0],[100,50],[0,50]]},
                "nestingPolygon":[[0,0],[100,0],[100,50],[0,50]]
              },
              "faces":[
                {"faceId":"A","machiningPermission":"PRIMARY"},
                {"faceId":"B","machiningPermission":"NOT_ALLOWED"}
              ],
              "features":[],
              "manufacturing":{"mode":"singleSide","machiningFace":"A"}
            },
            {
              "workpieceId":"manual.Body1",
              "panelId":"manual.Body1",
              "name":"Side B",
              "material":{"materialId":"PB-WHITE-18","thicknessMm":18},
              "geometry":{
                "quality":"tessellated",
                "outerProfile":{"closed":true,"points":[[0,0],[80,0],[80,40],[0,40]]},
                "nestingPolygon":[[0,0],[80,0],[80,40],[0,40]]
              },
              "faces":[
                {"faceId":"A","machiningPermission":"PRIMARY"},
                {"faceId":"B","machiningPermission":"NOT_ALLOWED"}
              ],
              "features":[],
              "manufacturing":{"mode":"singleSide","machiningFace":"A"}
            }
          ]
        }
        """;

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal(2, result.Package!.Panels.Count);
        Assert.Equal("manual.Body1", result.Package.Panels[0].PanelId);
        Assert.Equal("manual.Body1__2", result.Package.Panels[1].PanelId);
        Assert.Contains(result.Warnings, w => w.Code == "panelId_uniquified");
    }

    [Fact]
    public void Package_router_opens_cnjob_archive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "demo.cnjob");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "manifest.json",
                    """{"format":"cabinetnc.manufacturing-snapshot","schemaVersion":"1.0.0","payload":"snapshot.json"}""");
                WriteEntry(zip, "snapshot.json", SnapshotJson("[]"));
            }

            var result = PackageImporter.FromPath(path);

            Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
            Assert.Equal("SNAPSHOT-TEST", result.Package!.JobId);
            Assert.NotNull(result.SourceSnapshotJson);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Package_router_opens_snapshot_zip_by_manifest_format()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-snapshot-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "demo.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "manifest.json",
                    """{"format":"cabinetnc.manufacturing-snapshot","schemaVersion":"1.0.0","payload":"snapshot.json"}""");
                WriteEntry(zip, "snapshot.json", SnapshotJson("[]"));
            }

            Assert.Equal(
                ManufacturingSnapshot.SchemaName,
                PackageImporter.PeekZipManifestFormat(path));
            Assert.False(WoodJobImporter.LooksLikeWoodJobZip(path));

            var result = PackageImporter.FromPath(path);
            Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
            Assert.Equal("cabinetnc.manufacturing-snapshot", result.Package!.SchemaName);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Soft_skips_reserved_feature_kinds_with_warning()
    {
        var json = SnapshotJson(
            """
            [
              {"featureId":"H1","kind":"bore","sourceFace":"A","geometry":{"center":[20,30],"diameterMm":5},"depthMm":12,"through":false},
              {"featureId":"C1","kind":"custom","sourceFace":"A","geometry":{"profile":{"closed":true,"points":[[1,1],[2,1],[2,2]]}},"depthMm":3,"through":false},
              {"featureId":"X1","kind":"counterbore","sourceFace":"A","geometry":{"center":[40,40],"diameterMm":8},"depthMm":4,"through":false}
            ]
            """);

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        var panel = Assert.Single(result.Package!.Panels);
        Assert.Single(panel.Features);
        Assert.Equal("H1", panel.Features[0].FeatureId);
        Assert.Contains(result.Warnings, w => w.Code == "feature_kind_skipped");
        Assert.DoesNotContain(result.Errors, e => e.Code == "feature_kind_unsupported");
    }

    [Fact]
    public void Projects_inner_profiles_as_through_cutouts()
    {
        var json = """
        {
          "schema":"cabinetnc.manufacturing-snapshot",
          "schemaVersion":"1.0.0",
          "jobId":"INNER-TEST",
          "units":"mm",
          "workpieces":[
            {
              "workpieceId":"WP1",
              "panelId":"P1",
              "material":{"materialId":"PB-WHITE-18","thicknessMm":18},
              "geometry":{
                "quality":"tessellated",
                "outerProfile":{"closed":true,"points":[[0,0],[100,0],[100,50],[0,50]]},
                "innerProfiles":[
                  {"closed":true,"points":[[20,20],[40,20],[40,35],[20,35]]}
                ],
                "nestingPolygon":[[0,0],[100,0],[100,50],[0,50]]
              },
              "faces":[
                {"faceId":"A","machiningPermission":"PRIMARY"},
                {"faceId":"B","machiningPermission":"NOT_ALLOWED"}
              ],
              "features":[],
              "manufacturing":{"mode":"singleSide","machiningFace":"A"}
            }
          ]
        }
        """;

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        var panel = Assert.Single(result.Package!.Panels);
        var cut = Assert.Single(panel.Features);
        Assert.Equal("throughCutout", cut.Kind);
        Assert.True(cut.Through);
        Assert.Equal("innerProfile", cut.Purpose);
        Assert.Contains(result.Warnings, w => w.Code == "inner_profile_projected");
    }

    [Fact]
    public void Legacy_through_groove_imports_as_throughCutout_and_dedupes()
    {
        var json = SnapshotJson(
            """
            [
              {
                "featureId":"FEAT-03",
                "kind":"groove",
                "sourceFace":"THROUGH",
                "geometry":{
                  "centerline":[[20,42.5],[75,42.5]],
                  "widthMm":15.5,
                  "profile":{"closed":true,"points":[[20,34.75],[75,34.75],[75,50.25],[20,50.25]]}
                },
                "depthMm":16,
                "through":true
              },
              {
                "featureId":"FEAT-04",
                "kind":"groove",
                "sourceFace":"THROUGH",
                "geometry":{
                  "centerline":[[20,42.5],[75,42.5]],
                  "widthMm":15.5,
                  "profile":{"closed":true,"points":[[20,50.25],[75,50.25],[75,34.75],[20,34.75]]}
                },
                "depthMm":16,
                "through":true
              }
            ]
            """);

        var result = ManufacturingSnapshotImporter.FromJson(json);

        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        var panel = Assert.Single(result.Package!.Panels);
        var cut = Assert.Single(panel.Features);
        Assert.Equal("throughCutout", cut.Kind);
        Assert.True(cut.Through);
        Assert.Equal("THROUGH", cut.FaceId);
        Assert.True(cut.Path is { Count: >= 3 });

        var ops = OpsPlanner.FeaturesToOps(result.Package.Panels);
        Assert.Contains(ops, op => op.Op == "contour" && op.FeatureId == cut.FeatureId);
        Assert.DoesNotContain(ops, op => op.Op == "groove" && op.FeatureId == cut.FeatureId);
    }

    [Fact]
    public void Remap_keeps_original_workpiece_id()
    {
        var json = """
        {
          "schema":"cabinetnc.manufacturing-snapshot",
          "schemaVersion":"1.0.0",
          "jobId":"DUP-WP",
          "units":"mm",
          "workpieces":[
            {
              "workpieceId":"manual.Body1",
              "panelId":"manual.Body1",
              "material":{"materialId":"PB-WHITE-18","thicknessMm":18},
              "geometry":{
                "quality":"tessellated",
                "outerProfile":{"closed":true,"points":[[0,0],[100,0],[100,50],[0,50]]},
                "nestingPolygon":[[0,0],[100,0],[100,50],[0,50]]
              },
              "faces":[{"faceId":"A","machiningPermission":"PRIMARY"},{"faceId":"B","machiningPermission":"NOT_ALLOWED"}],
              "features":[],
              "manufacturing":{"mode":"singleSide","machiningFace":"A"}
            },
            {
              "workpieceId":"manual.Body1",
              "panelId":"manual.Body1",
              "material":{"materialId":"PB-WHITE-18","thicknessMm":18},
              "geometry":{
                "quality":"tessellated",
                "outerProfile":{"closed":true,"points":[[0,0],[80,0],[80,40],[0,40]]},
                "nestingPolygon":[[0,0],[80,0],[80,40],[0,40]]
              },
              "faces":[{"faceId":"A","machiningPermission":"PRIMARY"},{"faceId":"B","machiningPermission":"NOT_ALLOWED"}],
              "features":[],
              "manufacturing":{"mode":"singleSide","machiningFace":"A"}
            }
          ]
        }
        """;

        var result = ManufacturingSnapshotImporter.FromJson(json);
        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal("manual.Body1__2", result.Package!.Panels[1].PanelId);
        Assert.Equal("manual.Body1", result.Package.Panels[1].Identity!.WorkpieceId);
    }

    [Fact]
    public void Imports_material_meta_into_shop_MaterialGroupLabel()
    {
        var json = """
        {
          "schema":"cabinetnc.manufacturing-snapshot",
          "schemaVersion":"1.0.0",
          "jobId":"MAT-GROUP",
          "units":"mm",
          "workpieces":[
            {
              "workpieceId":"WP1",
              "panelId":"Kitchen.S1",
              "name":"Kitchen.S1",
              "identity":{"projectId":"PR1","moduleId":"Kitchen","role":"carcass"},
              "material":{
                "materialId":"carcass-white_stipple-15",
                "thicknessMm":15,
                "substrateId":"carcass_board",
                "decorId":"white_stipple",
                "colorName":"White Stipple",
                "surfaceMode":"DOUBLE_SIDED"
              },
              "geometry":{
                "quality":"tessellated",
                "toleranceMm":0.1,
                "outerProfile":{"closed":true,"points":[[0,0],[100,0],[100,50],[0,50]]},
                "nestingPolygon":[[0,0],[100,0],[100,50],[0,50]]
              },
              "faces":[
                {"faceId":"A","machiningPermission":"PRIMARY"},
                {"faceId":"B","machiningPermission":"ALLOWED"}
              ],
              "features":[],
              "manufacturing":{"mode":"singleSide","machiningFace":"A"}
            }
          ]
        }
        """;

        var result = ManufacturingSnapshotImporter.FromJson(json);
        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        var panel = Assert.Single(result.Package!.Panels);
        Assert.Equal("carcass", panel.Identity!.Role);
        Assert.Equal("white_stipple", panel.DecorId);
        Assert.Equal("White Stipple", panel.ColorName);
        Assert.Equal("DOUBLE_SIDED", panel.SurfaceMode);
        Assert.Equal("Carcass_White Stipple_DS · 15mm", panel.MaterialGroupLabel);
        Assert.Equal("Kitchen", panel.DisplayGroup);
    }

    [Fact]
    public void Imports_legacy_material_without_surfaceMode_with_stable_group_label()
    {
        var json = """
        {
          "schema":"cabinetnc.manufacturing-snapshot",
          "schemaVersion":"1.0.0",
          "jobId":"MAT-LEGACY",
          "units":"mm",
          "workpieces":[
            {
              "workpieceId":"WP1",
              "panelId":"Door.D1",
              "name":"Door.D1",
              "identity":{"role":"door"},
              "material":{
                "materialId":"door-metallic_white-18",
                "thicknessMm":18,
                "decorId":"metallic_white"
              },
              "geometry":{
                "quality":"tessellated",
                "toleranceMm":0.1,
                "outerProfile":{"closed":true,"points":[[0,0],[100,0],[100,50],[0,50]]},
                "nestingPolygon":[[0,0],[100,0],[100,50],[0,50]]
              },
              "faces":[
                {"faceId":"A","machiningPermission":"PRIMARY"},
                {"faceId":"B","machiningPermission":"ALLOWED"}
              ],
              "features":[],
              "manufacturing":{"mode":"singleSide","machiningFace":"A"}
            }
          ]
        }
        """;

        var result = ManufacturingSnapshotImporter.FromJson(json);
        Assert.True(result.Ok, string.Join("; ", result.Errors.Select(e => e.Message)));
        var panel = Assert.Single(result.Package!.Panels);
        Assert.Equal("door", panel.Identity!.Role);
        Assert.Null(panel.SurfaceMode);
        Assert.Equal("Door_Metallic White_SS · 18mm", panel.MaterialGroupLabel);
    }

    static string SnapshotJson(
        string featuresJson,
        string geometryQuality = "tessellated",
        string machiningFace = "A") =>
        $$"""
        {
          "schema":"cabinetnc.manufacturing-snapshot",
          "schemaVersion":"1.0.0",
          "jobId":"SNAPSHOT-TEST",
          "units":"mm",
          "workpieces":[
            {
              "workpieceId":"WP1",
              "panelId":"P1",
              "name":"Side",
              "identity":{"projectId":"PR1","moduleId":"M1","role":"left_side"},
              "material":{"materialId":"PB-WHITE-18","thicknessMm":18},
              "geometry":{
                "quality":"{{geometryQuality}}",
                "toleranceMm":0.1,
                "outerProfile":{"closed":true,"points":[[0,0],[100,0],[100,50],[0,50]]},
                "nestingPolygon":[[0,0],[100,0],[100,50],[0,50]]
              },
              "faces":[
                {"faceId":"A","finish":{"finishId":"white","finishName":"White"},"machiningPermission":"PRIMARY"},
                {"faceId":"B","finish":{"finishId":"white","finishName":"White"},"machiningPermission":"ALLOWED"}
              ],
              "features":{{featuresJson}},
              "manufacturing":{"mode":"singleSide","machiningFace":"{{machiningFace}}"}
            }
          ]
        }
        """;

    static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}

using CabinetNC.Infrastructure.Projects;
using CabinetNC.Infrastructure.Library;
using CabinetNC.FusionPackage;

namespace CabinetNC.Infrastructure.Tests;

public class SqliteProjectStoreTests
{
    [Fact]
    public void Round_trips_package_and_nest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = SqliteProjectStore.DbPathForFolder(dir);
            var store = new SqliteProjectStore();
            var pkgJson = """
                {"schema":"cabinetnc.cut-package","schemaVersion":1,"panels":[{"panelId":"P1","thicknessMm":18,"outline":{"points":[[0,0],[10,0],[10,10],[0,10]],"closed":true},"features":[]}]}
                """;
            var sourceSnapshotJson = """{"schema":"cabinetnc.manufacturing-snapshot","schemaVersion":"1.0.0"}""";
            var nest = SqliteProjectStore.SerializeNest([
                new NestPlacementDto { PanelId = "P1", SheetIndex = 0, OffsetX = 15, OffsetY = 20, RotationDeg = 0 },
            ]);
            store.Save(db, new ProjectDocument
            {
                Name = "demo",
                PackageJson = pkgJson,
                SourceSnapshotJson = sourceSnapshotJson,
                MachineId = "osai_e4_1325",
                NestPlacementsJson = nest,
                NcText = "G21\nM2\n",
            });

            var loaded = store.Load(db);
            Assert.NotNull(loaded);
            Assert.Equal("demo", loaded!.Name);
            Assert.Equal("osai_e4_1325", loaded.MachineId);
            Assert.Equal(sourceSnapshotJson, loaded.SourceSnapshotJson);
            Assert.Contains("G21", loaded.NcText);
            var places = SqliteProjectStore.DeserializeNest(loaded.NestPlacementsJson);
            Assert.Single(places);
            Assert.Equal(15, places[0].OffsetX);

            var imported = SqliteProjectStore.ImportPackage(loaded);
            Assert.True(imported.Ok, string.Join("; ", imported.Errors.Select(e => e.Message)));
            Assert.Equal("P1", imported.Package!.Panels[0].PanelId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Round_trips_workshop_library()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");
        try
        {
            var library = WorkshopLibraryStore.CreateDefault();
            library.Remnants.Add(new LibRemnant
            {
                Id = "REM-1",
                Material = "oak",
                WidthMm = 600,
                LengthMm = 400,
                ThicknessMm = 18,
                UseInNest = true,
            });
            library.Nest.SpacingMm = 9;

            WorkshopLibraryStore.Save(library, path);
            var loaded = WorkshopLibraryStore.Load(path);

            Assert.Equal(9, loaded.Nest.SpacingMm);
            var remnant = Assert.Single(loaded.Remnants);
            Assert.Equal("oak", remnant.Material);
            Assert.True(remnant.UseInNest);
            Assert.False(string.IsNullOrWhiteSpace(loaded.SavedAt));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}


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
    public void Round_trips_session_cam_bridges_and_ops()
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
            var session = new ProjectSessionState
            {
                Stage = "ops",
                ActiveNestSheet = 1,
                Cam = new ProjectCamSettings
                {
                    TongueFeed = 8500,
                    ProfFirstFeed = 11000,
                    HomeXyAtEnd = true,
                    ProfFirstRamp45 = true,
                },
                Bridges =
                [
                    new BridgeDto
                    {
                        Id = "b1",
                        PanelId = "P1",
                        SheetIndex = 0,
                        ArcLengthMm = 40,
                        X = 12.3456,
                        Y = 8.1,
                        WidthMm = 5,
                    },
                ],
                Ops =
                [
                    ProjectSessionCodec.FromOp(new CabinetNC.Domain.Manufacturing.CutOp
                    {
                        Op = "contour",
                        PanelId = "P1",
                        ToolId = "T2",
                        Placed = true,
                        SheetIndex = 0,
                        Path = [(0.12346, 1.5), (10.2591, 1.5)],
                        ThicknessMm = 15,
                        Through = true,
                    }),
                ],
                StockKinds =
                [
                    new StockKindDto
                    {
                        MaterialId = "oak",
                        Label = "橡木",
                        ThicknessMm = 15,
                        WidthMm = 1220,
                        LengthMm = 2440,
                        SpacingMm = 12,
                        BorderMm = 15,
                    },
                ],
            };
            store.Save(db, new ProjectDocument
            {
                Name = "job",
                PackageJson = pkgJson,
                MachineId = "osai_e4_1325",
                SessionJson = ProjectSessionCodec.Serialize(session),
            });

            var loaded = store.Load(db);
            Assert.NotNull(loaded);
            var round = ProjectSessionCodec.Deserialize(loaded!.SessionJson);
            Assert.NotNull(round);
            Assert.Equal("ops", round!.Stage);
            Assert.Equal(1, round.ActiveNestSheet);
            Assert.Equal(8500, round.Cam.TongueFeed);
            Assert.True(round.Cam.ProfFirstRamp45);
            Assert.True(round.Cam.HomeXyAtEnd);
            var bridge = Assert.Single(round.Bridges);
            Assert.Equal(12.3456, bridge.X);
            var op = ProjectSessionCodec.ToOp(Assert.Single(round.Ops));
            Assert.Equal("contour", op.Op);
            Assert.Equal(0.12346, op.Path![0].X);
            Assert.Equal("oak", Assert.Single(round.StockKinds).MaterialId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Round_trips_holding_pip_label_anchors_and_leftover_stock()
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
            var session = new ProjectSessionState
            {
                Stage = "nest",
                Holding =
                [
                    new HeldPartDto
                    {
                        PanelId = "H1",
                        Material = "oak",
                        ThicknessMm = 18,
                        RotationDeg = 90,
                        WidthMm = 120,
                        HeightMm = 80,
                    },
                ],
                PartInPart =
                [
                    new PartInPartDto
                    {
                        HostPanelId = "P1",
                        ChildPanelId = "C1",
                        FeatureId = "CUT1",
                        SheetIndex = 0,
                        Enabled = true,
                    },
                ],
                LabelAnchors =
                [
                    new LabelAnchorDto { PanelId = "P1", LocalX = 12.5, LocalY = 8.25 },
                ],
                StockKinds =
                [
                    new StockKindDto
                    {
                        MaterialId = "oak",
                        ThicknessMm = 18,
                        WidthMm = 1220,
                        LengthMm = 2440,
                        UseLeftoverPieces = true,
                        LeftoverXMm = 600,
                        LeftoverYMm = 800,
                    },
                ],
            };
            store.Save(db, new ProjectDocument
            {
                Name = "hold",
                PackageJson = pkgJson,
                MachineId = "osai_e4_1325",
                SessionJson = ProjectSessionCodec.Serialize(session),
            });

            var loaded = store.Load(db);
            var round = ProjectSessionCodec.Deserialize(loaded!.SessionJson);
            Assert.NotNull(round);
            var held = Assert.Single(round!.Holding);
            Assert.Equal("H1", held.PanelId);
            Assert.Equal(90, held.RotationDeg);
            var pip = Assert.Single(round.PartInPart);
            Assert.Equal("C1", pip.ChildPanelId);
            Assert.Equal("CUT1", pip.FeatureId);
            var anchor = Assert.Single(round.LabelAnchors);
            Assert.Equal(12.5, anchor.LocalX);
            var stock = Assert.Single(round.StockKinds);
            Assert.True(stock.UseLeftoverPieces);
            Assert.Equal(600, stock.LeftoverXMm);
            Assert.Equal(800, stock.LeftoverYMm);
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


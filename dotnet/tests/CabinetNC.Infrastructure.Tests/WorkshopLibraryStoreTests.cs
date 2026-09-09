using CabinetNC.Infrastructure.Library;

namespace CabinetNC.Infrastructure.Tests;

public class WorkshopLibraryStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cabinetnc-libstore-" + Guid.NewGuid().ToString("N"));
    string LibPath => Path.Combine(_dir, "library.json");

    public WorkshopLibraryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    static WorkshopLibrary WithRemnant(string id)
    {
        var lib = WorkshopLibraryStore.CreateDefault();
        lib.Remnants.Add(new LibRemnant { Id = id, WidthMm = 400, LengthMm = 600 });
        return lib;
    }

    [Fact]
    public void First_load_without_a_file_is_fresh_defaults()
    {
        var lib = WorkshopLibraryStore.Load(LibPath, out var status);
        Assert.Equal(LibraryLoadStatus.Fresh, status);
        Assert.NotEmpty(lib.Tools);
        Assert.False(File.Exists(LibPath));
    }

    [Fact]
    public void Save_keeps_the_previous_good_file_as_backup_and_leaves_no_temp()
    {
        WorkshopLibraryStore.Save(WithRemnant("R1"), LibPath);
        WorkshopLibraryStore.Save(WithRemnant("R2"), LibPath);

        Assert.True(File.Exists(LibPath));
        Assert.True(File.Exists(WorkshopLibraryStore.BackupPath(LibPath)));
        Assert.False(File.Exists(LibPath + ".tmp"));
        Assert.Contains("\"R2\"", File.ReadAllText(LibPath));
        Assert.Contains("\"R1\"", File.ReadAllText(WorkshopLibraryStore.BackupPath(LibPath)));

        var lib = WorkshopLibraryStore.Load(LibPath, out var status);
        Assert.Equal(LibraryLoadStatus.Loaded, status);
        Assert.Equal("R2", lib.Remnants.Single().Id);
    }

    [Fact]
    public void Truncated_main_file_recovers_from_the_backup_instead_of_wiping_the_shop()
    {
        WorkshopLibraryStore.Save(WithRemnant("R1"), LibPath);
        WorkshopLibraryStore.Save(WithRemnant("R2"), LibPath);
        // Simulate a power cut mid-write.
        File.WriteAllText(LibPath, File.ReadAllText(LibPath)[..40]);

        var lib = WorkshopLibraryStore.Load(LibPath, out var status);
        Assert.Equal(LibraryLoadStatus.RecoveredFromBackup, status);
        Assert.Equal("R1", lib.Remnants.Single().Id);
    }

    [Fact]
    public void Corrupt_main_file_without_backup_is_reported_not_hidden()
    {
        File.WriteAllText(LibPath, "{ not json");
        var lib = WorkshopLibraryStore.Load(LibPath, out var status);
        Assert.Equal(LibraryLoadStatus.Corrupt, status);
        Assert.Empty(lib.Remnants);
    }

    [Fact]
    public void Wrong_schema_counts_as_unreadable()
    {
        File.WriteAllText(LibPath, "{ \"schemaName\": \"something.else\" }");
        WorkshopLibraryStore.Load(LibPath, out var status);
        Assert.Equal(LibraryLoadStatus.Corrupt, status);
    }
}

namespace CabinetNC.Infrastructure.Library;

using System.Text.Json;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;

public enum LibraryLoadStatus
{
    /// <summary>No library file yet — factory defaults.</summary>
    Fresh,
    Loaded,
    /// <summary>Main file unreadable; the previous good copy was used.</summary>
    RecoveredFromBackup,
    /// <summary>Main file unreadable and no usable backup — factory defaults, data lost.</summary>
    Corrupt,
}

public static class WorkshopLibraryStore
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// <c>%LocalAppData%\CabinetNC\library.json</c>, unless <c>OMNICAM_LIBRARY_PATH</c> points
    /// elsewhere (UI smoke and test rigs must never touch the operator's real library).
    /// </summary>
    public static string DefaultPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("OMNICAM_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var full = Path.GetFullPath(overridePath.Trim());
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return full;
        }
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CabinetNC");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "library.json");
    }

    public static string BackupPath(string path) => path + ".bak";

    public static WorkshopLibrary Load(string? path = null) => Load(path, out _);

    /// <summary>
    /// Loads the library, falling back to the previous good copy (<c>library.json.bak</c>) when
    /// the main file is missing its schema or does not parse — a truncated file after a power
    /// cut must not silently wipe the shop's remnants, materials and labeler settings.
    /// </summary>
    public static WorkshopLibrary Load(string? path, out LibraryLoadStatus status)
    {
        path ??= DefaultPath();
        var main = TryRead(path);
        if (main is not null)
        {
            status = LibraryLoadStatus.Loaded;
            return EnsureDefaults(main);
        }
        var mainExisted = File.Exists(path);
        var backup = TryRead(BackupPath(path));
        if (backup is not null)
        {
            status = LibraryLoadStatus.RecoveredFromBackup;
            return EnsureDefaults(backup);
        }
        status = mainExisted ? LibraryLoadStatus.Corrupt : LibraryLoadStatus.Fresh;
        return EnsureDefaults(CreateDefault());
    }

    static WorkshopLibrary? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<WorkshopLibrary>(File.ReadAllText(path), JsonOpts);
            return doc is not null && doc.SchemaName == WorkshopLibrary.Schema ? doc : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Atomic save: serialize to <c>.tmp</c>, then swap it in while the previous good file
    /// becomes <c>.bak</c>. A crash at any point leaves either the old or the new complete file.
    /// </summary>
    public static void Save(WorkshopLibrary lib, string? path = null)
    {
        path ??= DefaultPath();
        lib.SchemaName = WorkshopLibrary.Schema;
        lib.Version = WorkshopLibrary.SchemaVersion;
        lib.SavedAt = DateTimeOffset.UtcNow.ToString("o");
        var json = JsonSerializer.Serialize(lib, JsonOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, BackupPath(path), ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public static WorkshopLibrary CreateDefault()
    {
        var lib = new WorkshopLibrary
        {
            Materials =
            [
                new() { Id = "mat_oak", Name = "oak", ThicknessMm = 18, DensityHint = "板式" },
                new() { Id = "mat_mdf", Name = "mdf", ThicknessMm = 18, DensityHint = "板式" },
                new() { Id = "mat_ply", Name = "plywood", ThicknessMm = 15, DensityHint = "多层" },
            ],
            Tools =
            [
                ..ToolCatalog.DefaultPresets.Select(t => new LibTool
                {
                    Id = t.ToolId,
                    Name = t.Name,
                    DiameterMm = t.DiameterMm,
                    FeedXyMmMin = t.FeedXyMmMin,
                    FeedZMmMin = t.FeedZMmMin,
                    SpindleRpm = t.SpindleRpm,
                }),
                ..MachineCatalog.All.Select(p => new LibTool
                {
                    Id = "tool_" + p.Id,
                    Name = p.Name,
                    MachineId = p.Id,
                    DiameterMm = p.ToolDiameterMm,
                    FeedXyMmMin = p.FeedXyMmMin,
                    FeedZMmMin = p.FeedZMmMin,
                    SpindleRpm = p.SpindleRpm,
                }),
            ],
            Nest = new NestDefaults(),
        };
        return lib;
    }

    static WorkshopLibrary EnsureDefaults(WorkshopLibrary lib)
    {
        lib.Materials ??= [];
        lib.Tools ??= [];
        lib.Remnants ??= [];
        lib.Nest ??= new NestDefaults();
        lib.Labeler ??= new LabelerDefaults();
        if (string.IsNullOrWhiteSpace(lib.Labeler.MachinePictureDir))
            lib.Labeler.MachinePictureDir = new LabelerDefaults().MachinePictureDir;
        lib.RecentFiles ??= [];
        lib.Display ??= new DisplayLayers();
        // Shop stock is 1200×2400; migrate the previous factory default so tab 2 cards update.
        if (Math.Abs(lib.Nest.DefaultSheetWidthMm - 1220) < 1e-6
            && Math.Abs(lib.Nest.DefaultSheetLengthMm - 2440) < 1e-6)
        {
            lib.Nest.DefaultSheetWidthMm = 1200;
            lib.Nest.DefaultSheetLengthMm = 2400;
        }
        if (lib.Materials.Count == 0 || lib.Tools.Count == 0)
        {
            var d = CreateDefault();
            if (lib.Materials.Count == 0) lib.Materials = d.Materials;
            if (lib.Tools.Count == 0) lib.Tools = d.Tools;
        }
        // Ensure Day-7 presets exist even on older library.json
        foreach (var preset in ToolCatalog.DefaultPresets)
        {
            if (lib.Tools.Any(t => t.Id.Equals(preset.ToolId, StringComparison.OrdinalIgnoreCase)))
                continue;
            lib.Tools.Insert(0, new LibTool
            {
                Id = preset.ToolId,
                Name = preset.Name,
                DiameterMm = preset.DiameterMm,
                FeedXyMmMin = preset.FeedXyMmMin,
                FeedZMmMin = preset.FeedZMmMin,
                SpindleRpm = preset.SpindleRpm,
            });
        }
        return lib;
    }
}

namespace CabinetNC.Application.Projects;

using CabinetNC.Domain;
using CabinetNC.Domain.Parts;
using CabinetNC.FusionPackage;

public sealed class ProjectSession
{
    public CutPackage? Package { get; private set; }
    public string? SourcePath { get; private set; }
    public string? PackageJson { get; private set; }
    public string? SourceSnapshotJson { get; private set; }
    /// <summary>Parsed manufacturing-snapshot when the last successful open was a .cnjob / snapshot JSON.</summary>
    public ManufacturingSnapshot? LastImportSnapshot { get; private set; }
    public string? ProjectDbPath { get; private set; }
    public string MachineId { get; set; } = "osai_e4_1325";
    public IReadOnlyList<ValidationIssue> LastWarnings { get; private set; } = [];
    public IReadOnlyList<ValidationIssue> LastErrors { get; private set; } = [];

    /// <summary>True after geom/feature edits until nest/CAM are rebuilt.</summary>
    public bool ManufacturingDirty { get; private set; }
    public EditHistory History { get; } = new();

    public PackageImportResult OpenPackageFile(string path)
    {
        var result = PackageImporter.FromPath(path);
        LastWarnings = result.Warnings;
        LastErrors = result.Errors;
        if (result.Ok && result.Package is not null)
        {
            Package = result.Package;
            // ponytail: project.db still stores flat cut-package JSON; woodjob zip stays on SourcePath.
            PackageJson = CutPackageJson.Serialize(result.Package);
            SourceSnapshotJson = result.SourceSnapshotJson;
            LastImportSnapshot = result.Snapshot ?? TryParseSnapshot(result.SourceSnapshotJson);
            SourcePath = path;
            ProjectDbPath = null;
            ManufacturingDirty = false;
            History.Clear();
        }
        return result;
    }

    public PackageImportResult OpenPackageJson(
        string json,
        string? sourceLabel = null,
        string? sourceSnapshotJson = null)
    {
        var result = CutPackageImporter.FromJson(json);
        LastWarnings = result.Warnings;
        LastErrors = result.Errors;
        if (result.Ok && result.Package is not null)
        {
            Package = result.Package;
            PackageJson = json;
            SourceSnapshotJson = sourceSnapshotJson;
            LastImportSnapshot = TryParseSnapshot(sourceSnapshotJson);
            SourcePath = sourceLabel;
            ManufacturingDirty = false;
            History.Clear();
        }
        return result;
    }

    static ManufacturingSnapshot? TryParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = ManufacturingSnapshotImporter.FromJson(json);
            return parsed.Snapshot;
        }
        catch
        {
            return null;
        }
    }

    public void SetProjectDbPath(string? path) => ProjectDbPath = path;

    public void ReplacePanel(Panel panel, bool recordHistory = true)
    {
        if (Package is null) return;
        if (recordHistory)
            History.PushBeforeEdit(PackageJson ?? CutPackageJson.Serialize(Package));
        Package = Package.WithPanel(panel);
        PackageJson = CutPackageJson.Serialize(Package);
        ManufacturingDirty = true;
    }

    public void RemovePanel(string panelId, bool recordHistory = true)
    {
        if (Package is null) return;
        if (recordHistory)
            History.PushBeforeEdit(PackageJson ?? CutPackageJson.Serialize(Package));
        Package = Package.WithoutPanel(panelId);
        PackageJson = CutPackageJson.Serialize(Package);
        ManufacturingDirty = true;
    }

    public string NextCopyPanelId(string baseId)
    {
        if (Package is null) return $"{baseId}_copy";
        var n = 1;
        string id;
        do { id = n == 1 ? $"{baseId}_copy" : $"{baseId}_copy{n}"; n++; }
        while (Package.Panels.Any(p => p.PanelId.Equals(id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }

    public void MarkManufacturingClean() => ManufacturingDirty = false;

    public bool TryUndo()
    {
        if (Package is null) return false;
        var current = PackageJson ?? CutPackageJson.Serialize(Package);
        var prev = History.Undo(current);
        if (prev is null) return false;
        return ApplySnapshot(prev);
    }

    public bool TryRedo()
    {
        if (Package is null) return false;
        var current = PackageJson ?? CutPackageJson.Serialize(Package);
        var next = History.Redo(current);
        if (next is null) return false;
        return ApplySnapshot(next);
    }

    bool ApplySnapshot(string json)
    {
        var result = CutPackageImporter.FromJson(json);
        if (!result.Ok || result.Package is null) return false;
        Package = result.Package;
        PackageJson = json;
        ManufacturingDirty = true;
        return true;
    }

    public void Clear()
    {
        Package = null;
        SourcePath = null;
        PackageJson = null;
        SourceSnapshotJson = null;
        ProjectDbPath = null;
        LastWarnings = [];
        LastErrors = [];
        ManufacturingDirty = false;
        History.Clear();
    }
}

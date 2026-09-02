namespace CabinetNC.Application.Projects;

using CabinetNC.Domain;
using CabinetNC.Domain.Nesting;
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
    /// <summary>Shop-facing project title. Used for window chrome, save default, and NC file names.</summary>
    public string? ProjectName { get; set; }
    public string ResolvedProjectName =>
        !string.IsNullOrWhiteSpace(ProjectName) ? ProjectName.Trim()
        : !string.IsNullOrWhiteSpace(Package?.JobId) ? Package!.JobId!.Trim()
        : "未命名工程";
    public string MachineId { get; set; } = "osai_e4_1325";
    public string LabelerMachineId { get; set; } = "osai_e4_1325";
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
            Package = StampIfNeeded(result.Package, path);
            // ponytail: project.db still stores flat cut-package JSON; woodjob zip stays on SourcePath.
            PackageJson = CutPackageJson.Serialize(Package);
            SourceSnapshotJson = result.SourceSnapshotJson;
            LastImportSnapshot = result.Snapshot ?? TryParseSnapshot(result.SourceSnapshotJson);
            SourcePath = path;
            ProjectDbPath = null;
            ManufacturingDirty = false;
            History.Clear();
            ResetProjectName(Package.JobId, path);
        }
        return result;
    }

    public PackageImportResult AddPackageFile(string path)
    {
        var result = PackageImporter.FromPath(path);
        LastWarnings = result.Warnings;
        LastErrors = result.Errors;
        if (!result.Ok || result.Package is null)
            return result;
        var incomingId = PackageMerge.SuggestId(result.Package, path);
        var incomingLabel = PackageMerge.SuggestLabel(result.Package, path);
        if (Package is null)
        {
            Package = PackageMerge.Stamp(result.Package, incomingId, incomingLabel);
            SourcePath = path;
            SuggestProjectName(Package.JobId, path);
        }
        else
        {
            if (Package.Panels.All(p => string.IsNullOrWhiteSpace(p.Identity?.PackageId)))
                Package = StampIfNeeded(Package, SourcePath);
            Package = PackageMerge.Merge(Package, result.Package, incomingId, incomingLabel);
        }
        PackageJson = CutPackageJson.Serialize(Package);
        ManufacturingDirty = true;
        History.Clear();
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
            Package = StampIfNeeded(result.Package, sourceLabel);
            PackageJson = CutPackageJson.Serialize(Package);
            SourceSnapshotJson = sourceSnapshotJson;
            LastImportSnapshot = TryParseSnapshot(sourceSnapshotJson);
            SourcePath = sourceLabel;
            ManufacturingDirty = false;
            History.Clear();
        }
        return result;
    }

    public void ResetProjectName(string? hint, string? sourcePath = null)
    {
        ProjectName = FirstHint(hint, sourcePath);
    }

    public void SuggestProjectName(string? hint, string? sourcePath = null)
    {
        if (!string.IsNullOrWhiteSpace(ProjectName)) return;
        ProjectName = FirstHint(hint, sourcePath);
    }

    static string? FirstHint(string? hint, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(hint)) return hint.Trim();
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            if (!string.IsNullOrWhiteSpace(stem)
                && !stem.Equals("project", StringComparison.OrdinalIgnoreCase)
                && !stem.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                return stem;
        }
        return null;
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

    static CutPackage StampIfNeeded(CutPackage pkg, string? sourcePath)
    {
        if (pkg.Panels.Any(p => !string.IsNullOrWhiteSpace(p.Identity?.PackageId)))
            return pkg;
        return PackageMerge.Stamp(
            pkg,
            PackageMerge.SuggestId(pkg, sourcePath),
            PackageMerge.SuggestLabel(pkg, sourcePath));
    }

    public void SetProjectDbPath(string? path) => ProjectDbPath = path;

    public void AcceptPackage(CutPackage package, string? sourcePath = null)
    {
        Package = StampIfNeeded(package, sourcePath);
        PackageJson = CutPackageJson.Serialize(package);
        SourceSnapshotJson = null;
        LastImportSnapshot = null;
        SourcePath = sourcePath;
        ProjectDbPath = null;
        LastWarnings = [];
        LastErrors = [];
        ManufacturingDirty = false;
        History.Clear();
        ResetProjectName(Package.JobId, sourcePath);
    }

    public void ReplacePanel(Panel panel, bool recordHistory = true)
    {
        if (Package is null) return;
        if (recordHistory)
            History.PushBeforeEdit(PackageJson ?? CutPackageJson.Serialize(Package));
        Package = Package.WithPanel(panel);
        PackageJson = CutPackageJson.Serialize(Package);
        ManufacturingDirty = true;
    }

    public bool TryMergeMaterialKinds(
        IReadOnlyList<NestGroupKey> selected,
        NestGroupKey target,
        BlindFeatureDepthPolicy blindPolicy)
    {
        if (Package is null || selected.Count < 2)
            return false;
        History.PushBeforeEdit(PackageJson ?? CutPackageJson.Serialize(Package));
        Package = MaterialCorrect.MergeKinds(Package, selected, target, blindPolicy);
        PackageJson = CutPackageJson.Serialize(Package);
        ManufacturingDirty = true;
        return true;
    }

    public bool TryChangePanelMaterials(
        IReadOnlyList<string> panelIds,
        NestGroupKey target,
        BlindFeatureDepthPolicy blindPolicy)
    {
        if (Package is null || panelIds.Count == 0)
            return false;
        var next = MaterialCorrect.RetargetPanels(Package, panelIds, target, blindPolicy);
        if (ReferenceEquals(next, Package))
            return false;
        History.PushBeforeEdit(PackageJson ?? CutPackageJson.Serialize(Package));
        Package = next;
        PackageJson = CutPackageJson.Serialize(Package);
        ManufacturingDirty = true;
        return true;
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

    /// <summary>Unload one imported .cnjob from the merged nest list. Last package clears the session.</summary>
    public bool TryRemovePackage(string packageKey)
    {
        if (Package is null || string.IsNullOrWhiteSpace(packageKey))
            return false;
        var next = PackageMerge.Remove(Package, packageKey);
        if (ReferenceEquals(next, Package))
            return false;
        if (next.Panels.Count == 0)
        {
            Clear();
            return true;
        }
        History.PushBeforeEdit(PackageJson ?? CutPackageJson.Serialize(Package));
        Package = next;
        PackageJson = CutPackageJson.Serialize(Package);
        ManufacturingDirty = true;
        return true;
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

    public string NextDraftPanelId()
    {
        var n = 1;
        string id;
        do { id = $"DRAFT-{n++}"; }
        while (Package?.Panels.Any(p => p.PanelId.Equals(id, StringComparison.OrdinalIgnoreCase)) == true);
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
        LabelerMachineId = "osai_e4_1325";
        ProjectName = null;
    }
}

namespace CabinetNC.Domain.Manufacturing;

using System.Text.Json;
using CabinetNC.Domain;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Nesting;

/// <summary>Pluggable NC post (Day 10). RC dialects wrap NcEmitter.</summary>
public interface IPostProcessor
{
    string Id { get; }
    string Emit(IEnumerable<CutOp> ops, MachineProfile profile, PostRecipe? recipe = null);
}

/// <summary>
/// Future machine-specific ATC/M6 post. RC default returns null — do not invent M6.
/// </summary>
public interface IToolChangePost
{
    string Id { get; }
    string? EmitToolChange(ToolDefinition tool, MachineProfile profile);
}

/// <summary>Conservative RC stub until shop confirms controller M6 syntax.</summary>
public sealed class NullToolChangePost : IToolChangePost
{
    public string Id => "none";
    public string? EmitToolChange(ToolDefinition tool, MachineProfile profile) => null;
}

/// <summary>
/// Resolves the post processor for a machine. The NC-emitting implementations live in
/// CabinetNC.Domain.Compute, which registers itself here when loaded; a process without that assembly
/// (the customer build) must supply its own <see cref="IPostProcessor"/> (the intranet one) explicitly.
/// </summary>
public static class PostProcessorCatalog
{
    public static Func<MachineProfile, IPostProcessor>? Provider { get; set; }

    public static IPostProcessor Resolve(MachineProfile profile) =>
        Provider?.Invoke(profile)
        ?? throw new InvalidOperationException(
            "No post processor is available in this process: NC is produced on the intranet server; pass the intranet post processor explicitly.");

    /// <summary>Profile as the dialect-specific emitter sees it (dialect id, program end); shared by every implementation.</summary>
    public static (string PostId, MachineProfile Profile) DialectFor(MachineProfile profile) =>
        profile.Dialect == "fanuc_like"
            ? ("fanuc_like", CloneProfile(profile, "fanuc_like", "M30"))
            : ("generic_mm", CloneProfile(profile, "generic", profile.ProgramEnd));

    public static MachineProfile CloneProfile(MachineProfile profile, string dialect, string? programEnd) =>
        new()
        {
            Id = profile.Id,
            Name = profile.Name,
            Dialect = dialect,
            ProgramEnd = programEnd ?? profile.ProgramEnd,
            SafeZMm = profile.SafeZMm,
            FeedXyMmMin = profile.FeedXyMmMin,
            FeedZMmMin = profile.FeedZMmMin,
            SpindleRpm = profile.SpindleRpm,
            ToolDiameterMm = profile.ToolDiameterMm,
            ContourDepthMm = profile.ContourDepthMm,
            ContourStepdownMm = profile.ContourStepdownMm,
            DrillPeckMm = profile.DrillPeckMm,
            EnableContour = profile.EnableContour,
            EnableDrill = profile.EnableDrill,
            EnableGroove = profile.EnableGroove,
            OriginNote = profile.OriginNote,
        };
}

public sealed class ToolNcProgram
{
    public required string ToolId { get; init; }
    public required string NcFileName { get; init; }
    public required string NcText { get; init; }
    public int OpCount { get; init; }
}

public sealed class SheetArtifact
{
    public required int SheetIndex { get; init; }
    public required string DxfFileName { get; init; }
    public required string DxfText { get; init; }
    public required string ManifestJson { get; init; }
    public required IReadOnlyList<ToolNcProgram> ToolPrograms { get; init; }
    public int OpCount { get; init; }
    public IReadOnlyList<string> PanelIds { get; init; } = [];
    public IReadOnlyList<string> ToolIds { get; init; } = [];

    /// <summary>Compatibility: first tool program file name.</summary>
    public string NcFileName => ToolPrograms.Count > 0 ? ToolPrograms[0].NcFileName : "";
    /// <summary>Compatibility: first tool program body.</summary>
    public string NcText => ToolPrograms.Count > 0 ? ToolPrograms[0].NcText : "";
}

public sealed class ExportBundle
{
    public required string JobId { get; init; }
    public required string PostId { get; init; }
    public required IReadOnlyList<SheetArtifact> Sheets { get; init; }
    public required string RootManifestJson { get; init; }
    public string? JobSheetHtml { get; init; }
    public string? BomCsv { get; init; }
    public string? LabelsHtml { get; init; }
    public IReadOnlyList<WorkpieceLabel> Labels { get; init; } = [];
}

/// <summary>Per-sheet DXF/manifest + per Sheet×Tool NC programs (audited RC).</summary>
public static class SheetBundleBuilder
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static ExportBundle Build(
        CutPackage package,
        IReadOnlyList<NestPlacement> placements,
        IReadOnlyList<CutOp> ops,
        MachineProfile profile,
        IPostProcessor? post = null,
        string? jobSheetHtml = null,
        IReadOnlyDictionary<string, Parts.Panel>? panelsById = null,
        double sheetWidthMm = 0,
        double sheetLengthMm = 0,
        FaceRegistration? registration = null,
        bool enforcePreflight = true,
        IToolChangePost? toolChangePost = null,
        IReadOnlyDictionary<string, ToolDefinition>? tools = null,
        PostRecipe? recipe = null)
    {
        post ??= PostProcessorCatalog.Resolve(profile);
        toolChangePost ??= new NullToolChangePost();
        var catalog = tools ?? ToolCatalog.DefaultMap();

        if (enforcePreflight)
        {
            var panels = panelsById
                ?? package.Panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
            var report = NcPreflight.Check(ops, profile, sheetWidthMm, sheetLengthMm, panels, registration);
            if (!report.Ok)
                throw new InvalidOperationException("Export blocked by preflight:\n" + NcPreflight.Format(report));
        }

        var placed = ops.Where(o => o.Placed && o.Enabled).ToList();
        if (placed.Any(o => string.IsNullOrWhiteSpace(o.ToolId)))
            throw new InvalidOperationException("Export blocked: unbound ToolId — refuse mixed or anonymous tool programs.");

        var jobId = package.JobId ?? "job";
        var sheetIndexes = placements.Select(p => p.SheetIndex).Distinct().OrderBy(i => i).ToList();
        if (sheetIndexes.Count == 0 && placed.Count > 0)
            sheetIndexes = placed.Select(o => o.SheetIndex).Distinct().OrderBy(i => i).ToList();

        var sheets = new List<SheetArtifact>();
        foreach (var si in sheetIndexes)
        {
            var sheetOps = placed.Where(o => o.SheetIndex == si).ToList();
            var sheetPlaces = placements.Where(p => p.SheetIndex == si).ToList();
            var dxf = NestDxfWriter.Write(package, placements, si);
            var panelIds = sheetPlaces.Select(p => p.PanelId).Distinct().OrderBy(x => x).ToList();
            var toolIds = sheetOps.Select(o => o.ToolId!).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var programs = new List<ToolNcProgram>();
            foreach (var toolId in toolIds)
            {
                var toolOps = sheetOps.Where(o => string.Equals(o.ToolId, toolId, StringComparison.OrdinalIgnoreCase)).ToList();
                catalog.TryGetValue(toolId, out var def);
                // Reserved for future machine M6 — RC must not invent ATC codes.
                _ = toolChangePost.EmitToolChange(
                    def ?? new ToolDefinition { ToolId = toolId, Name = toolId },
                    profile);
                var nc = post.Emit(toolOps, profile, recipe);
                programs.Add(new ToolNcProgram
                {
                    ToolId = toolId,
                    NcFileName = $"{jobId}_S{si + 1}_{toolId}.nc",
                    NcText = nc,
                    OpCount = toolOps.Count,
                });
            }

            var manifest = new
            {
                schema = "cabinetnc.sheet-manifest",
                schemaVersion = 2,
                jobId,
                sheetIndex = si,
                sheetLabel = $"S{si + 1}",
                post = post.Id,
                toolChangePost = toolChangePost.Id,
                machineId = profile.Id,
                panelIds,
                toolIds,
                opCount = sheetOps.Count,
                files = new
                {
                    dxf = $"{jobId}_S{si + 1}.dxf",
                    programs = programs.Select(p => new { toolId = p.ToolId, nc = p.NcFileName, opCount = p.OpCount }),
                },
                programs = programs.Select(p => new { toolId = p.ToolId, nc = p.NcFileName, opCount = p.OpCount }),
            };
            sheets.Add(new SheetArtifact
            {
                SheetIndex = si,
                DxfFileName = $"{jobId}_S{si + 1}.dxf",
                DxfText = dxf,
                ManifestJson = JsonSerializer.Serialize(manifest, JsonOpts),
                ToolPrograms = programs,
                OpCount = sheetOps.Count,
                PanelIds = panelIds,
                ToolIds = toolIds,
            });
        }

        var root = new
        {
            schema = "cabinetnc.export-bundle",
            schemaVersion = 2,
            jobId,
            post = post.Id,
            toolChangePost = toolChangePost.Id,
            machineId = profile.Id,
            sheetCount = sheets.Count,
            outputPolicy = "sheet_x_tool_nc",
            sheets = sheets.Select(s => new
            {
                sheetIndex = s.SheetIndex,
                dxf = s.DxfFileName,
                manifest = $"{jobId}_S{s.SheetIndex + 1}.manifest.json",
                opCount = s.OpCount,
                panelIds = s.PanelIds,
                toolIds = s.ToolIds,
                programs = s.ToolPrograms.Select(p => new { toolId = p.ToolId, nc = p.NcFileName, opCount = p.OpCount }),
            }),
        };

        var labels = LabelBomBuilder.BuildLabels(package, placements);
        var bom = LabelBomBuilder.ToCsv(labels, ops);
        var labelsHtml = LabelBomBuilder.ToLabelsHtml(labels);

        return new ExportBundle
        {
            JobId = jobId,
            PostId = post.Id,
            Sheets = sheets,
            RootManifestJson = JsonSerializer.Serialize(root, JsonOpts),
            JobSheetHtml = jobSheetHtml,
            BomCsv = bom,
            LabelsHtml = labelsHtml,
            Labels = labels,
        };
    }

    public static IReadOnlyList<string> WriteToDirectory(ExportBundle bundle, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var s in bundle.Sheets)
        {
            foreach (var prog in s.ToolPrograms)
            {
                var ncPath = Path.Combine(directory, prog.NcFileName);
                File.WriteAllText(ncPath, prog.NcText);
                written.Add(ncPath);
            }
            var dxfPath = Path.Combine(directory, s.DxfFileName);
            var manPath = Path.Combine(directory, $"{bundle.JobId}_S{s.SheetIndex + 1}.manifest.json");
            File.WriteAllText(dxfPath, s.DxfText);
            File.WriteAllText(manPath, s.ManifestJson);
            written.Add(dxfPath);
            written.Add(manPath);
        }
        var rootPath = Path.Combine(directory, $"{bundle.JobId}.bundle.json");
        File.WriteAllText(rootPath, bundle.RootManifestJson);
        written.Add(rootPath);
        if (!string.IsNullOrWhiteSpace(bundle.JobSheetHtml))
        {
            var htmlPath = Path.Combine(directory, $"{bundle.JobId}_sheet.html");
            File.WriteAllText(htmlPath, bundle.JobSheetHtml);
            written.Add(htmlPath);
        }
        if (!string.IsNullOrWhiteSpace(bundle.BomCsv))
        {
            var bomPath = Path.Combine(directory, $"{bundle.JobId}_bom.csv");
            File.WriteAllText(bomPath, bundle.BomCsv);
            written.Add(bomPath);
        }
        if (!string.IsNullOrWhiteSpace(bundle.LabelsHtml))
        {
            var labPath = Path.Combine(directory, $"{bundle.JobId}_labels.html");
            File.WriteAllText(labPath, bundle.LabelsHtml);
            written.Add(labPath);
        }
        return written;
    }
}

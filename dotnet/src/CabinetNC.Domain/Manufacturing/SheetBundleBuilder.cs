namespace CabinetNC.Domain.Manufacturing;

using System.Text.Json;
using CabinetNC.Domain;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing.Verification;
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

public sealed class GenericMmPostProcessor : IPostProcessor
{
    public string Id => "generic_mm";
    public string Emit(IEnumerable<CutOp> ops, MachineProfile profile, PostRecipe? recipe = null)
    {
        var p = CloneProfile(profile, dialect: "generic", programEnd: profile.ProgramEnd);
        return NcEmitter.OpsToNc(ops, p, recipe: recipe);
    }

    internal static MachineProfile CloneProfile(MachineProfile profile, string dialect, string? programEnd) =>
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

public sealed class FanucLikePostProcessor : IPostProcessor
{
    public string Id => "fanuc_like";
    public string Emit(IEnumerable<CutOp> ops, MachineProfile profile, PostRecipe? recipe = null)
    {
        var p = GenericMmPostProcessor.CloneProfile(profile, "fanuc_like", "M30");
        return NcEmitter.OpsToNc(ops, p, recipe: recipe);
    }
}

public static class PostProcessorCatalog
{
    public static IPostProcessor Resolve(MachineProfile profile) =>
        profile.Dialect == "fanuc_like"
            ? new FanucLikePostProcessor()
            : new GenericMmPostProcessor();
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
    /// <summary>Geometry verification of this sheet's programs; null when no verifier ran.</summary>
    public VerifyReport? Verify { get; init; }
    /// <summary>Serialized <see cref="Verify"/> written next to the NC as <c>{job}_S{n}.verify.json</c>.</summary>
    public string? VerifyReportJson { get; init; }

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

    /// <summary>All sheets' verification merged; null when no verifier ran.</summary>
    public VerifyReport? Verify { get; init; }
    /// <summary>Plan→emit→verify rounds when built through <see cref="SheetBundleBuilder.BuildWithRepair"/>.</summary>
    public IReadOnlyList<RepairRound> RepairTrail { get; init; } = [];
    /// <summary>Overrides the final programs were planned with (empty on a first-round pass).</summary>
    public CamOverrides Overrides { get; init; } = CamOverrides.Empty;
}

/// <summary>Raised when the geometry verifier finds an error and the caller asked to enforce it.</summary>
public sealed class ExportVerifyException(VerifyReport report)
    : InvalidOperationException("Export blocked by 计码验算:\n" + VerifyReport.Format(report))
{
    public VerifyReport Report { get; } = report;
    /// <summary>Rounds attempted before giving up (empty when no repair loop ran).</summary>
    public IReadOnlyList<RepairRound> Trail { get; init; } = [];
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
        PostRecipe? recipe = null,
        IExportVerifier? verifier = null,
        bool enforceVerify = true)
    {
        post ??= PostProcessorCatalog.Resolve(profile);
        toolChangePost ??= new NullToolChangePost();
        var catalog = tools ?? ToolCatalog.DefaultMap();
        var verifyPanels = panelsById?.Values.ToList() ?? package.Panels;
        var zDatum = VerifyInput.DatumOf(recipe);

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

            // 计码 gate: CAD intent vs the programs just emitted. Independent of the planner.
            VerifyReport? verify = null;
            if (verifier is not null)
            {
                verify = verifier.Verify(new VerifyInput
                {
                    Panels = verifyPanels,
                    Placements = placements,
                    SheetIndex = si,
                    Programs = programs.Select(p => new VerifyProgram(p.NcText, p.ToolId)).ToList(),
                    Tools = catalog,
                    ZDatum = zDatum,
                    Bridges = recipe?.Bridges ?? [],
                });
                if (enforceVerify && !verify.Ok)
                    throw new ExportVerifyException(verify);
            }

            var manifest = new
            {
                schema = "cabinetnc.sheet-manifest",
                schemaVersion = 3,
                jobId,
                sheetIndex = si,
                sheetLabel = $"S{si + 1}",
                post = post.Id,
                toolChangePost = toolChangePost.Id,
                machineId = profile.Id,
                zDatum = zDatum.ToString(),
                panelIds,
                toolIds,
                opCount = sheetOps.Count,
                placements = sheetPlaces.Select(p => new
                {
                    panelId = p.PanelId,
                    offsetX = p.OffsetX,
                    offsetY = p.OffsetY,
                    rotationDeg = p.RotationDeg,
                }),
                bridges = (recipe?.Bridges ?? []).Where(b => b.SheetIndex == si).Select(b => new
                {
                    id = b.Id,
                    panelId = b.PanelId,
                    featureId = b.FeatureId,
                    x = b.X,
                    y = b.Y,
                    widthMm = b.WidthMm,
                }),
                verify = verify is null ? null : new
                {
                    ok = verify.Ok,
                    engine = verify.Engine,
                    errors = verify.Issues.Count(i => i.IsError),
                    warnings = verify.Issues.Count(i => !i.IsError),
                    file = $"{jobId}_S{si + 1}.verify.json",
                },
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
                Verify = verify,
                VerifyReportJson = verify is null ? null : JsonSerializer.Serialize(verify, JsonOpts),
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
            Verify = verifier is null ? null : VerifyReport.Merge(sheets.Where(s => s.Verify is not null).Select(s => s.Verify!)),
        };
    }

    public const int DefaultRepairRounds = 3;

    /// <summary>
    /// Plan → emit → verify, and when the verifier finds an error ask <paramref name="repair"/>
    /// for stricter process overrides and plan again — at most <paramref name="maxRounds"/>
    /// times. The verifier and its tolerances never change between rounds; only the
    /// planner's whitelisted knobs do. Throws <see cref="ExportVerifyException"/> when the
    /// last round still fails or the repair planner has nothing left to try.
    /// </summary>
    public static ExportBundle BuildWithRepair(
        CutPackage package,
        IReadOnlyList<NestPlacement> placements,
        Func<CamOverrides, IReadOnlyList<CutOp>> plan,
        MachineProfile profile,
        IExportVerifier verifier,
        IRepairPlanner repair,
        int maxRounds = DefaultRepairRounds,
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
        var catalog = tools ?? ToolCatalog.DefaultMap();
        var overrides = CamOverrides.Empty;
        var trail = new List<RepairRound>();
        ExportBundle? last = null;
        for (var round = 1; round <= Math.Max(1, maxRounds); round++)
        {
            var ops = plan(overrides);
            var bundle = Build(
                package, placements, ops, profile, post, jobSheetHtml, panelsById,
                sheetWidthMm, sheetLengthMm, registration, enforcePreflight, toolChangePost,
                catalog, recipe, verifier, enforceVerify: false);
            var report = bundle.Verify ?? VerifyReport.Empty(-1);
            trail.Add(new RepairRound(round, overrides, report));
            last = bundle;
            if (report.Ok)
            {
                return new ExportBundle
                {
                    JobId = bundle.JobId,
                    PostId = bundle.PostId,
                    Sheets = bundle.Sheets,
                    RootManifestJson = bundle.RootManifestJson,
                    JobSheetHtml = bundle.JobSheetHtml,
                    BomCsv = bundle.BomCsv,
                    LabelsHtml = bundle.LabelsHtml,
                    Labels = bundle.Labels,
                    Verify = bundle.Verify,
                    RepairTrail = trail,
                    Overrides = overrides,
                };
            }

            var next = repair.Propose(report, overrides, catalog);
            if (next is null || next.SameAs(overrides))
                break;
            overrides = next;
        }

        throw new ExportVerifyException(last!.Verify ?? VerifyReport.Empty(-1)) { Trail = trail };
    }

    public static string RepairTrailJson(ExportBundle bundle) =>
        JsonSerializer.Serialize(new
        {
            schema = "cabinetnc.export-repair",
            schemaVersion = 1,
            jobId = bundle.JobId,
            rounds = bundle.RepairTrail.Select(r => new
            {
                round = r.Round,
                ok = r.Ok,
                overrides = r.Overrides.Describe(),
                errors = r.Report.Issues.Where(i => i.IsError).Select(i => new { i.Code, i.PanelId, i.FeatureId, i.Message }),
            }),
            finalOverrides = bundle.Overrides.All.Select(o => new
            {
                panelId = o.Key.PanelId,
                featureId = o.Key.FeatureId,
                stepoverMm = o.Value.StepoverMm,
                forceGrooveClear = o.Value.ForceGrooveClear,
                toolId = o.Value.ToolId,
                forceFinishLoop = o.Value.ForceFinishLoop,
            }),
        }, JsonOpts);

    public static IReadOnlyList<string> WriteToDirectory(ExportBundle bundle, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        if (bundle.RepairTrail.Count > 1)
        {
            var repPath = Path.Combine(directory, $"{bundle.JobId}.repair.json");
            File.WriteAllText(repPath, RepairTrailJson(bundle));
            written.Add(repPath);
        }
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
            if (s.VerifyReportJson is not null)
            {
                var verPath = Path.Combine(directory, $"{bundle.JobId}_S{s.SheetIndex + 1}.verify.json");
                File.WriteAllText(verPath, s.VerifyReportJson);
                written.Add(verPath);
            }
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

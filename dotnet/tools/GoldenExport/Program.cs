using System.Text.Json;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using CabinetNC.FusionPackage;

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "package.json")) &&
            Directory.Exists(Path.Combine(dir.FullName, "dotnet")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("cabinetnc-cut repo root not found");
}

var root = RepoRoot();
var outDir = Path.Combine(root, "docs", "sprint", "golden");
Directory.CreateDirectory(outDir);

var sampleZip = Path.Combine(root, "public", "samples", "demo_woodjob_120.zip");
var sampleJson = Path.Combine(root, "public", "samples", "demo_cut_package.json");

var import = PackageImporter.FromPath(sampleZip);
if (!import.Ok || import.Package is null)
    throw new InvalidOperationException("woodjob import failed: " +
        string.Join("; ", import.Errors.Select(e => e.Message)));

var package = import.Package;
var profile = MachineCatalog.Get(MachineCatalog.DefaultId);

var parts = package.Panels.Select(p =>
{
    var (_, _, _, _, w, h) = PanelEdit.BBox(p);
    return new NestPart
    {
        PanelId = p.PanelId,
        WidthMm = w,
        HeightMm = h,
        MayRotate = p.MayRotate90,
    };
}).ToList();

var sheets = package.Sheets.Count > 0
    ? package.Sheets.Select(s => new NestSheetSpec
    {
        WidthMm = s.WidthMm > 0 ? s.WidthMm : 1220,
        LengthMm = s.LengthMm > 0 ? s.LengthMm : 2440,
        BorderMm = 15,
        Label = s.Material,
    }).ToList()
    : [new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15 }];

var nest = BlfNester.Pack(new NestRequest
{
    Parts = parts,
    Sheets = sheets,
    SpacingMm = 12,
    BorderMm = 15,
    AllowRotation = true,
});

var ops = OpsPlanner.AttachToNest(
    OpsPlanner.FeaturesToOps(package.Panels, enableGroove: true),
    nest.Placements);
ops = ContourToolOffset.Apply(ops, profile.ToolDiameterMm / 2.0);
var nc = NcEmitter.OpsToNc(ops, profile);
var dxf = NestDxfWriter.Write(package, nest.Placements, sheetIndex: 0);
var placed = nest.Placements.Select(p => p.PanelId).ToHashSet();
var html = JobSheetBuilder.BuildHtml(
    package,
    profile,
    nest.Placements,
    placed,
    "golden baseline preflight placeholder",
    utilizationPct: 0,
    unplacedCount: nest.Unplaced.Count);

var cutJson = File.Exists(sampleJson)
    ? File.ReadAllText(sampleJson)
    : JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });

File.WriteAllText(Path.Combine(outDir, "baseline_demo120.nc"), nc);
File.WriteAllText(Path.Combine(outDir, "baseline_demo120_S1.dxf"), dxf);
File.WriteAllText(Path.Combine(outDir, "baseline_demo120_sheet.html"), html);
File.WriteAllText(Path.Combine(outDir, "baseline_demo_cut_package.json"), cutJson);
File.WriteAllText(
    Path.Combine(outDir, "baseline_manifest.json"),
    JsonSerializer.Serialize(new
    {
        schema = "cabinetnc.sprint-golden",
        schemaVersion = 1,
        createdAt = DateTimeOffset.Now.ToString("o"),
        source = "public/samples/demo_woodjob_120.zip",
        panels = package.Panels.Count,
        placements = nest.Placements.Count,
        sheets = nest.SheetCount,
        unplaced = nest.Unplaced,
        engine = nest.Engine,
        profile = profile.Id,
        files = new[]
        {
            "baseline_demo120.nc",
            "baseline_demo120_S1.dxf",
            "baseline_demo120_sheet.html",
            "baseline_demo_cut_package.json",
        },
    }, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"golden written → {outDir}");
Console.WriteLine($"panels={package.Panels.Count} placed={nest.Placements.Count} sheets={nest.SheetCount} unplaced={nest.Unplaced.Count}");

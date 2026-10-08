// VerifyJob — run the independent 计码 verifier outside Omni.
//
//   VerifyJob <export-dir>                       verify an exported bundle ({job}.bundle.json + manifests + .cut.json)
//   VerifyJob --package <pkg> --manifest <S1.manifest.json> [--nc file ...] [--datum top|bottom]
//   VerifyJob <pkg.cut.json|.cnjob>              import + full pipeline (nest → ops → offset → bundle → verify)
//   VerifyJob --demo                             nest + plan + offset + emit the sample package, then verify it
//
// Exit code 0 = no errors, 1 = verification errors, 2 = usage / input problem.

using System.Text.Json;
using CabinetNC.Domain;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using CabinetNC.FusionPackage;
using CabinetNC.Verify;

var args0 = args.ToList();
if (args0.Count == 0)
    return Usage();

try
{
    if (args0[0] == "--demo")
        return RunDemo(args0.Contains("--json"));
    if (args0[0] == "--package")
        return RunAdHoc(args0);
    if (Directory.Exists(args0[0]))
        return RunBundleDir(args0[0], args0.Contains("--json"));
    if (File.Exists(args0[0]))
        return RunPackageFile(args0[0], args0.Contains("--json"));
    return Usage();
}
catch (Exception ex)
{
    Console.Error.WriteLine("verify failed: " + ex.Message);
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine(
        "VerifyJob <export-dir> [--json]\n" +
        "VerifyJob <pkg.cut.json|.cnjob> [--json]\n" +
        "VerifyJob --package <pkg.cut.json|.cnjob> --manifest <job_S1.manifest.json> [--nc <file> ...] [--datum top|bottom] [--json]\n" +
        "VerifyJob --demo [--json]");
    return 2;
}

// ------------------------------------------------------------------ exported bundle

static int RunBundleDir(string dir, bool json)
{
    var bundlePath = Directory.EnumerateFiles(dir, "*.bundle.json").FirstOrDefault()
        ?? throw new FileNotFoundException("no *.bundle.json in " + dir);
    using var bundle = JsonDocument.Parse(File.ReadAllText(bundlePath));
    var jobId = bundle.RootElement.GetProperty("jobId").GetString() ?? "job";

    var pkgPath = Path.Combine(dir, jobId + ".cut.json");
    if (!File.Exists(pkgPath))
        pkgPath = Directory.EnumerateFiles(dir, "*.cut.json").FirstOrDefault()
            ?? throw new FileNotFoundException("no .cut.json next to the bundle — export writes it; verification needs the CAD intent");
    var panels = LoadPanels(pkgPath);

    var worst = 0;
    var reports = new List<VerifyReport>();
    foreach (var sheet in bundle.RootElement.GetProperty("sheets").EnumerateArray())
    {
        var manifestPath = Path.Combine(dir, sheet.GetProperty("manifest").GetString()!);
        var report = VerifyManifest(panels, manifestPath, dir, ncOverride: null, datumOverride: null);
        reports.Add(report);
        worst = Math.Max(worst, Print(report, json));
    }
    if (json)
        Console.WriteLine(JsonSerializer.Serialize(VerifyReport.Merge(reports), new JsonSerializerOptions { WriteIndented = true }));
    return worst;
}

static int RunAdHoc(List<string> a)
{
    string? pkg = null, manifest = null, datum = null;
    var nc = new List<string>();
    var json = false;
    for (var i = 0; i < a.Count; i++)
    {
        switch (a[i])
        {
            case "--package": pkg = a[++i]; break;
            case "--manifest": manifest = a[++i]; break;
            case "--nc": nc.Add(a[++i]); break;
            case "--datum": datum = a[++i]; break;
            case "--json": json = true; break;
        }
    }
    if (pkg is null || manifest is null) return Usage();
    var panels = LoadPanels(pkg);
    var report = VerifyManifest(panels, manifest, Path.GetDirectoryName(Path.GetFullPath(manifest))!,
        nc.Count > 0 ? nc : null, datum);
    return Print(report, json);
}

static VerifyReport VerifyManifest(
    IReadOnlyList<Panel> panels, string manifestPath, string dir, List<string>? ncOverride, string? datumOverride)
{
    using var man = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var root = man.RootElement;
    var version = root.TryGetProperty("schemaVersion", out var v) ? v.GetInt32() : 0;
    if (version < 3 || !root.TryGetProperty("placements", out var placesEl))
        throw new InvalidOperationException(
            $"{Path.GetFileName(manifestPath)} is schemaVersion {version} without placements — re-export with the current build");

    var sheetIndex = root.GetProperty("sheetIndex").GetInt32();
    var placements = placesEl.EnumerateArray().Select(p => new NestPlacement
    {
        PanelId = p.GetProperty("panelId").GetString()!,
        SheetIndex = sheetIndex,
        OffsetX = p.GetProperty("offsetX").GetDouble(),
        OffsetY = p.GetProperty("offsetY").GetDouble(),
        RotationDeg = p.GetProperty("rotationDeg").GetDouble(),
    }).ToList();

    var datumText = datumOverride ?? (root.TryGetProperty("zDatum", out var zd) ? zd.GetString() : null) ?? "BoardTop";
    var datum = datumText.Contains("bottom", StringComparison.OrdinalIgnoreCase) ? ZDatum.BoardBottom : ZDatum.BoardTop;

    var programs = new List<VerifyProgram>();
    if (ncOverride is not null)
    {
        foreach (var f in ncOverride)
            programs.Add(new VerifyProgram(File.ReadAllText(f), GuessToolId(f)));
    }
    else
    {
        foreach (var p in root.GetProperty("programs").EnumerateArray())
        {
            var file = Path.Combine(dir, p.GetProperty("nc").GetString()!);
            programs.Add(new VerifyProgram(File.ReadAllText(file), p.GetProperty("toolId").GetString()));
        }
    }

    var bridges = new List<ProfileBridge>();
    if (root.TryGetProperty("bridges", out var br))
    {
        foreach (var b in br.EnumerateArray())
        {
            bridges.Add(new ProfileBridge
            {
                Id = b.GetProperty("id").GetString() ?? "",
                PanelId = b.GetProperty("panelId").GetString() ?? "",
                FeatureId = b.TryGetProperty("featureId", out var fid) && fid.ValueKind == JsonValueKind.String ? fid.GetString() : null,
                SheetIndex = sheetIndex,
                X = b.GetProperty("x").GetDouble(),
                Y = b.GetProperty("y").GetDouble(),
                WidthMm = b.GetProperty("widthMm").GetDouble(),
            });
        }
    }

    return new ExportVerifier().Verify(new VerifyInput
    {
        Panels = panels,
        Placements = placements,
        SheetIndex = sheetIndex,
        Programs = programs,
        Tools = ToolCatalog.DefaultMap(),
        ZDatum = datum,
        Bridges = bridges,
    });
}

static string? GuessToolId(string file)
{
    var stem = Path.GetFileNameWithoutExtension(file);
    var idx = stem.LastIndexOf("_T", StringComparison.OrdinalIgnoreCase);
    return idx >= 0 && idx + 2 < stem.Length && char.IsDigit(stem[idx + 2]) ? stem[(idx + 1)..] : null;
}

static IReadOnlyList<Panel> LoadPanels(string path)
{
    var import = PackageImporter.FromPath(path);
    if (!import.Ok || import.Package is null)
        throw new InvalidOperationException("package import failed: " + string.Join("; ", import.Errors.Select(e => e.Message)));
    return import.Package.Panels;
}

// ------------------------------------------------------------------ single package: import + full pipeline

static int RunPackageFile(string path, bool json)
{
    var import = PackageImporter.FromPath(path);
    if (!import.Ok || import.Package is null)
    {
        Console.WriteLine("import failed: " + string.Join("; ", import.Errors.Select(e => e.Message)));
        return 2;
    }
    Console.WriteLine($"== {Path.GetFileName(path)}");
    return RunPipeline(import.Package, json);
}

// ------------------------------------------------------------------ demo: full pipeline on the sample job

static int RunDemo(bool json)
{
    var root = RepoRoot();
    var samples = Path.Combine(root, "public", "samples");
    var worst = 0;
    foreach (var sample in Directory.EnumerateFiles(samples)
                 .Where(f => f.EndsWith(".cnjob", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith("demo_cut_package.json", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(f => f, StringComparer.Ordinal))
    {
        Console.WriteLine($"== {Path.GetFileName(sample)}");
        var import = PackageImporter.FromPath(sample);
        if (!import.Ok || import.Package is null)
        {
            Console.WriteLine("  import failed: " + string.Join("; ", import.Errors.Select(e => e.Message)));
            continue;
        }
        worst = Math.Max(worst, RunPipeline(import.Package, json));
    }
    return worst;
}

static int RunPipeline(CutPackage package, bool json)
{
    var profile = MachineCatalog.Get(MachineCatalog.DefaultId);

    var sheet = new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12, AllowRotation = true };
    var nest = GroupedBlfNester.Pack(
        package.Panels,
        new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true },
        [sheet],
        GroupedBlfNester.SizeOfOutline);

    var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(package.Panels), nest.Placements);
    ops = ContourToolOffset.Apply(ops, ClearanceToolPick.DiameterOf("T2") / 2);
    var recipe = PostRecipe.TroyDefault();

    var byId = package.Panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
    var pre = NcPreflight.Check(ops, profile, 1220, 2440, byId);
    Console.WriteLine($"preflight: {(pre.Ok ? "OK" : "FAIL")}"
        + (pre.Issues.Count == 0 ? "" : " · " + string.Join(", ", pre.Issues.Select(i => i.Code).Distinct())));

    var bundle = SheetBundleBuilder.Build(
        package, nest.Placements, ops, profile,
        sheetWidthMm: 1220, sheetLengthMm: 2440,
        enforcePreflight: false, recipe: recipe,
        verifier: new ExportVerifier(), enforceVerify: false);

    Console.WriteLine($"demo: panels={package.Panels.Count} placed={nest.Placements.Count} sheets={bundle.Sheets.Count}");
    var worst = 0;
    foreach (var s in bundle.Sheets)
        worst = Math.Max(worst, Print(s.Verify!, json));
    return worst;
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "package.json")) && Directory.Exists(Path.Combine(dir.FullName, "dotnet")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("cabinetnc-cut repo root not found");
}

// ------------------------------------------------------------------ output

static int Print(VerifyReport r, bool json)
{
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        var errors = r.Issues.Count(i => i.IsError);
        var warns = r.Issues.Count - errors;
        Console.WriteLine($"S{r.SheetIndex + 1}: {(r.Ok ? "OK" : "FAIL")} · panels={r.PanelCount} features={r.FeatureCount} strokes={r.StrokeCount} · errors={errors} warnings={warns}");
        foreach (var i in r.Issues.OrderBy(i => i.IsError ? 0 : 1).ThenBy(i => i.PanelId, StringComparer.Ordinal))
            Console.WriteLine($"  {(i.IsError ? "✗" : "!")} [{i.Code}] {i.PanelId}{(i.FeatureId is null ? "" : "/" + i.FeatureId)} @({i.AtX:0.#},{i.AtY:0.#}) {i.Message}");
    }
    return r.Ok ? 0 : 1;
}

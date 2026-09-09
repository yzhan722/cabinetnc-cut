using System.Globalization;
using System.Runtime.CompilerServices;
using CabinetNC.Domain;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests.Regression;

public sealed record GoldenArtifact(string RelativePath, string Utf8Text);

/// <summary>Raw (un-normalised) program as the post emitted it, plus which tool it is for.</summary>
public sealed record GoldenProgram(string Name, string? ToolId, string NcText);

/// <summary>Fixed nest/CAM environment shared by goldens and safety invariants.</summary>
public sealed class GoldenPipeline
{
    public const double SheetWidthMm = 1220;
    public const double SheetLengthMm = 2440;

    public required MachineProfile Profile { get; init; }
    public required NestResult Nest { get; init; }
    public required IReadOnlyList<CutOp> Ops { get; init; }
    public required IReadOnlyDictionary<string, Panel> PanelsById { get; init; }
    public required PreflightReport Preflight { get; init; }
}

public static class GoldenJobRunner
{
    public const string UpdateEnvVar = "CABINETNC_UPDATE_GOLDENS";

    /// <summary>Nest → ops → tool binding → preflight, with the one fixed environment.</summary>
    public static GoldenPipeline Prepare(GoldenJob job)
    {
        var profile = MachineCatalog.Get(MachineCatalog.DefaultId);
        var sheet = new NestSheetSpec
        {
            WidthMm = GoldenPipeline.SheetWidthMm,
            LengthMm = GoldenPipeline.SheetLengthMm,
            BorderMm = 15,
            SpacingMm = 12,
            AllowRotation = true,
        };
        var nest = GroupedBlfNester.Pack(
            job.Panels,
            new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true },
            [sheet],
            GroupedBlfNester.SizeOfOutline);

        var ops = ToolBinder.BindAll(
            OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(job.Panels), nest.Placements));
        var byId = job.Panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var pre = NcPreflight.Check(ops, profile, GoldenPipeline.SheetWidthMm, GoldenPipeline.SheetLengthMm, byId);
        return new GoldenPipeline
        {
            Profile = profile,
            Nest = nest,
            Ops = ops,
            PanelsById = byId,
            Preflight = pre,
        };
    }

    /// <summary>Raw programs for the job's post: one Troy file, or one file per sheet × tool.</summary>
    public static IReadOnlyList<GoldenProgram> EmitPrograms(GoldenJob job, GoldenPipeline p)
    {
        if (job.Post == "troy")
        {
            var nc = NcEmitter.OpsToNc(p.Ops, p.Profile, recipe: PostRecipe.TroyDefault());
            return [new GoldenProgram("nc/program", null, nc)];
        }

        var pkg = new CutPackage
        {
            SchemaName = CutPackage.Schema,
            JobId = job.Id,
            Panels = job.Panels,
        };
        var bundle = SheetBundleBuilder.Build(
            pkg,
            p.Nest.Placements,
            p.Ops,
            p.Profile,
            sheetWidthMm: GoldenPipeline.SheetWidthMm,
            sheetLengthMm: GoldenPipeline.SheetLengthMm,
            enforcePreflight: false);

        var programs = new List<GoldenProgram>();
        foreach (var sh in bundle.Sheets)
        {
            foreach (var prog in sh.ToolPrograms)
                programs.Add(new GoldenProgram($"nc/S{sh.SheetIndex + 1}_{prog.ToolId}", prog.ToolId, prog.NcText));
        }
        return programs;
    }

    public static IReadOnlyList<GoldenArtifact> Run(GoldenJob job)
    {
        var p = Prepare(job);
        var artifacts = new List<GoldenArtifact>
        {
            new("preflight-codes.txt", FormatCodes(p.Preflight)),
            new("layout.txt", FormatLayout(p.Nest, p.PanelsById)),
        };

        if (!p.Preflight.Ok)
            return artifacts;

        foreach (var prog in EmitPrograms(job, p))
            artifacts.Add(new(prog.Name + ".nc.norm", NcTextNormalizer.Normalize(prog.NcText)));

        return artifacts;
    }

    public static void AssertMatchesGoldens(string jobId, IReadOnlyList<GoldenArtifact> actual)
    {
        var dir = Path.Combine(DataRoot(), "goldens", jobId);
        var update = string.Equals(
            Environment.GetEnvironmentVariable(UpdateEnvVar),
            "1",
            StringComparison.Ordinal);

        if (update)
        {
            Directory.CreateDirectory(dir);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in actual)
            {
                var path = Path.Combine(dir, a.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, a.Utf8Text);
                written.Add(a.RelativePath.Replace('\\', '/'));
            }

            foreach (var leftover in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, leftover).Replace('\\', '/');
                if (!written.Contains(rel))
                    File.Delete(leftover);
            }
        }

        Assert.True(Directory.Exists(dir), $"missing golden dir {dir} — set {UpdateEnvVar}=1 once to seed");
        foreach (var a in actual)
        {
            var path = Path.Combine(dir, a.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{jobId} missing golden {a.RelativePath}");
            // Goldens are authored with LF; a Windows checkout (core.autocrlf) or an
            // editor may rewrite them as CRLF, which must not read as a product change.
            var expected = NormalizeNewlines(File.ReadAllText(path));
            if (string.Equals(expected, a.Utf8Text, StringComparison.Ordinal))
                continue;
            Assert.Fail(
                $"{jobId}/{a.RelativePath} differs from golden {path}\n" +
                FirstDifference(expected, a.Utf8Text) +
                $"Review the change; run once with {UpdateEnvVar}=1 only if the new output is intended.");
        }
    }

    static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var g = actual.Split('\n');
        var n = Math.Max(e.Length, g.Length);
        for (var i = 0; i < n; i++)
        {
            var el = i < e.Length ? e[i] : "<eof>";
            var al = i < g.Length ? g[i] : "<eof>";
            if (!string.Equals(el, al, StringComparison.Ordinal))
                return $"line {i + 1}:\n  expected: {el}\n  actual:   {al}\n";
        }
        return "";
    }

    public static string DataRoot([CallerFilePath] string? cs = null)
    {
        var dir = Path.GetDirectoryName(cs)
                  ?? throw new InvalidOperationException("no caller path");
        return Path.GetFullPath(Path.Combine(dir, "..", "..", "testdata", "regression"));
    }

    static string FormatCodes(PreflightReport pre)
    {
        var codes = pre.Issues.Select(i => i.Code).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal);
        return string.Join('\n', codes);
    }

    static string FormatLayout(NestResult nest, IReadOnlyDictionary<string, Panel> byId)
    {
        var lines = nest.Placements
            .Select(p =>
            {
                byId.TryGetValue(p.PanelId, out var panel);
                var mat = panel?.Material ?? "";
                var th = (panel?.ThicknessMm ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
                return $"{p.PanelId}\t{p.SheetIndex}\t{mat}\t{th}";
            })
            .OrderBy(l => l, StringComparer.Ordinal);
        return string.Join('\n', lines) + (nest.Placements.Count == 0 ? "" : "\n");
    }
}

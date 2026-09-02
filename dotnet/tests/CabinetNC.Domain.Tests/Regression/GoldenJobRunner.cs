using System.Globalization;
using System.Runtime.CompilerServices;
using CabinetNC.Domain;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests.Regression;

public sealed record GoldenArtifact(string RelativePath, string Utf8Text);

public static class GoldenJobRunner
{
    public const string UpdateEnvVar = "CABINETNC_UPDATE_GOLDENS";

    public static IReadOnlyList<GoldenArtifact> Run(GoldenJob job)
    {
        var profile = MachineCatalog.Get(MachineCatalog.DefaultId);
        var sheet = new NestSheetSpec
        {
            WidthMm = 1220,
            LengthMm = 2440,
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
        var pre = NcPreflight.Check(ops, profile, 1220, 2440, byId);

        var artifacts = new List<GoldenArtifact>
        {
            new("preflight-codes.txt", FormatCodes(pre)),
            new("layout.txt", FormatLayout(nest, byId)),
        };

        if (!pre.Ok)
            return artifacts;

        if (job.Post == "troy")
        {
            var nc = NcEmitter.OpsToNc(ops, profile, recipe: PostRecipe.TroyDefault());
            artifacts.Add(new("nc/program.nc.norm", NcTextNormalizer.Normalize(nc)));
            return artifacts;
        }

        var pkg = new CutPackage
        {
            SchemaName = CutPackage.Schema,
            JobId = job.Id,
            Panels = job.Panels,
        };
        var bundle = SheetBundleBuilder.Build(
            pkg,
            nest.Placements,
            ops,
            profile,
            sheetWidthMm: 1220,
            sheetLengthMm: 2440,
            enforcePreflight: false);

        foreach (var sh in bundle.Sheets)
        {
            foreach (var prog in sh.ToolPrograms)
            {
                var name = $"nc/S{sh.SheetIndex + 1}_{prog.ToolId}.nc.norm";
                artifacts.Add(new(name, NcTextNormalizer.Normalize(prog.NcText)));
            }
        }

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
            var expected = File.ReadAllText(path);
            Assert.Equal(expected, a.Utf8Text);
        }
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

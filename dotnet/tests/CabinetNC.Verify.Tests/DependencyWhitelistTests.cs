using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CabinetNC.Verify.Tests;

/// <summary>
/// The verifier must never read the CAM generator's intermediate geometry. If it did,
/// a wrong tool offset or a bad pocket clear would be "verified" by the same bug.
/// This is the boundary that keeps 计码 verification independent from Omni.
/// </summary>
public class DependencyWhitelistTests
{
    static readonly string[] Forbidden =
    [
        "ContourToolOffset", "PocketClearer", "PocketClearIslands", "GrooveClear", "GrooveGeometry",
        "OpsPlanner", "CutOp", "NcEmitter", "CamSafety", "CamStrategy", "ClearanceToolPick",
        "NcToPanels", "NcReverse", "NcProcessInfer", "ToolBinder", "PanelEdit", "CadPath",
        "ClimbCut", "SheetBundleBuilder", "NcPreflight", "GuillotineCutPlanner", "RecutNester",
        "GroupedBlfNester", "BlfNester", "NestExportGate", "NestValidator",
    ];

    [Fact]
    public void Verify_sources_do_not_touch_generator_types()
    {
        var src = VerifySourceDir();
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(files);

        var hits = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var name in Forbidden)
            {
                foreach (Match m in Regex.Matches(text, $@"\b{Regex.Escape(name)}\b"))
                {
                    var line = text[..m.Index].Count(c => c == '\n') + 1;
                    var lineText = text.Split('\n')[line - 1].Trim();
                    if (lineText.StartsWith("//", StringComparison.Ordinal)
                        || lineText.StartsWith("///", StringComparison.Ordinal)
                        || lineText.StartsWith("*", StringComparison.Ordinal))
                        continue;
                    hits.Add($"{Path.GetFileName(file)}:{line} uses {name}");
                }
            }
        }
        Assert.True(hits.Count == 0,
            "CabinetNC.Verify must stay independent of the CAM generator:\n  " + string.Join("\n  ", hits));
    }

    [Fact]
    public void Verify_project_references_only_domain_and_clipper()
    {
        var csproj = Path.Combine(VerifySourceDir(), "CabinetNC.Verify.csproj");
        var text = File.ReadAllText(csproj);
        var refs = Regex.Matches(text, @"ProjectReference Include=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Single(refs);
        Assert.EndsWith("CabinetNC.Domain.csproj", refs[0]);
        Assert.DoesNotContain("Desktop", text);
        Assert.DoesNotContain("Infrastructure", text);
    }

    static string VerifySourceDir([CallerFilePath] string? cs = null)
    {
        var dir = Path.GetDirectoryName(cs) ?? throw new InvalidOperationException("no caller path");
        return Path.GetFullPath(Path.Combine(dir, "..", "..", "src", "CabinetNC.Verify"));
    }
}

namespace CabinetNC.Domain.Tests.Regression;

[Trait("Category", "GoldenRegression")]
public class ReleaseGoldenRegressionTests
{
    [Fact]
    public void sheet_tool_single_panel()
    {
        var job = GoldenFixtures.SheetToolSinglePanel();
        var arts = GoldenJobRunner.Run(job);
        GoldenJobRunner.AssertMatchesGoldens(job.Id, arts);

        Assert.Contains(arts, a => a.RelativePath == "preflight-codes.txt" && a.Utf8Text.Length == 0);
        var layout = arts.Single(a => a.RelativePath == "layout.txt").Utf8Text.TrimEnd();
        Assert.StartsWith("P\t0\t", layout, StringComparison.Ordinal);
        var nc = arts.Where(a => a.RelativePath.StartsWith("nc/", StringComparison.Ordinal)).ToList();
        Assert.True(nc.Count >= 2, "sheet×tool should emit at least two single-tool files");
        Assert.All(nc, a =>
        {
            var tools = a.Utf8Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(l => l.StartsWith("(tool ", StringComparison.Ordinal));
            Assert.Equal(1, tools);
        });
    }

    [Fact]
    public void multi_material_no_share()
    {
        var job = GoldenFixtures.MultiMaterialNoShare();
        var arts = GoldenJobRunner.Run(job);
        GoldenJobRunner.AssertMatchesGoldens(job.Id, arts);

        var rows = arts.Single(a => a.RelativePath == "layout.txt").Utf8Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
        var sheets = rows.Select(r => r.Split('\t')[1]).Distinct().ToList();
        var mats = rows.Select(r => r.Split('\t')[2]).Distinct().ToList();
        Assert.Equal(2, sheets.Count);
        Assert.Equal(2, mats.Count);
        Assert.Contains(arts, a => a.RelativePath.Contains("/S1_", StringComparison.Ordinal));
        Assert.Contains(arts, a => a.RelativePath.Contains("/S2_", StringComparison.Ordinal)
                                  || a.RelativePath.Contains("/S3_", StringComparison.Ordinal));
    }

    [Fact]
    public void troy_single_file_atc()
    {
        var job = GoldenFixtures.TroySingleFileAtc();
        var arts = GoldenJobRunner.Run(job);
        GoldenJobRunner.AssertMatchesGoldens(job.Id, arts);

        var nc = arts.Single(a => a.RelativePath == "nc/program.nc.norm").Utf8Text;
        Assert.Contains("M6 T", nc, StringComparison.Ordinal);
        Assert.Single(arts, a => a.RelativePath.StartsWith("nc/", StringComparison.Ordinal));
    }
}

using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class ExportSummaryTests
{
    [Fact]
    public void Missing_bitmaps_produce_an_error_that_names_the_stems_and_the_consequence()
    {
        var status = ExportSummary.LabelStatus(3, @"D:\out", @"D:\CNC", ["OHC_D0", "OHC_D1"]);
        Assert.Contains("2 个标签没有 BMP", status);
        Assert.Contains("OHC_D0, OHC_D1", status);
        Assert.Contains("M701", status);

        var toast = ExportSummary.Toast(1, 3, 2, @"D:\CNC");
        Assert.Equal(StatusSeverity.Error, toast.Severity);
        Assert.Contains("2 个标签缺少 BMP", toast.Title);
        Assert.True(toast.OfferOpenFolder);
    }

    [Fact]
    public void Complete_export_tells_the_operator_where_the_bitmaps_go()
    {
        var status = ExportSummary.LabelStatus(3, @"D:\out", @"D:\CNC", []);
        Assert.Contains(@"复制到机床 D:\CNC", status);
        Assert.Contains("不要放子目录", status);

        var toast = ExportSummary.Toast(2, 3, 0, @"D:\CNC");
        Assert.Equal(StatusSeverity.Success, toast.Severity);
        Assert.Equal("已导出 2 个程序文件", toast.Title);
        Assert.Contains(@"D:\CNC", toast.Detail);
    }

    [Fact]
    public void Export_without_labels_says_so()
    {
        var toast = ExportSummary.Toast(1, 0, 0, @"D:\CNC");
        Assert.Equal("没有标签需要复制。", toast.Detail);
    }

    [Fact]
    public void Status_list_of_missing_stems_is_capped_at_five()
    {
        var many = Enumerable.Range(1, 8).Select(i => $"S{i}").ToList();
        var status = ExportSummary.LabelStatus(8, @"D:\out", @"D:\CNC", many);
        Assert.Contains("S5", status);
        Assert.DoesNotContain("S6", status);
        Assert.Contains("8 个标签没有 BMP", status);
    }
}

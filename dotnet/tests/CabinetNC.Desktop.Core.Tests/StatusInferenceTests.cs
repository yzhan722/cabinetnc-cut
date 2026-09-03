using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class StatusInferenceTests
{
    [Theory]
    [InlineData("密排失败: 引擎超时", StatusSeverity.Error)]
    [InlineData("预检硬错误，禁止导出", StatusSeverity.Error)]
    [InlineData("标签 3 张在 D:\\x；但程序请求的 1 个标签没有 BMP", StatusSeverity.Error)]
    [InlineData("板件已修改，密排与刀路需要重新生成", StatusSeverity.Warning)]
    [InlineData("刀路 · 需要先完成密排", StatusSeverity.Warning)]
    [InlineData("请先载入方案", StatusSeverity.Warning)]
    [InlineData("已取消密排 · 保留上一次结果", StatusSeverity.Warning)]
    [InlineData("密排完成 · 已排 12 件 · 2 张大板", StatusSeverity.Success)]
    [InlineData("已导出 3 个文件 → D:\\out", StatusSeverity.Success)]
    [InlineData("已打开工程 lounge · 6 块板", StatusSeverity.Success)]
    [InlineData("初始密排中…", StatusSeverity.Busy)]
    [InlineData("正在停止密排…", StatusSeverity.Busy)]
    [InlineData("导出 · 选中右侧程序文件", StatusSeverity.Info)]
    [InlineData("", StatusSeverity.Info)]
    [InlineData(null, StatusSeverity.Info)]
    public void Keywords_map_to_expected_severity(string? text, StatusSeverity expected) =>
        Assert.Equal(expected, StatusInference.Infer(text));

    [Fact]
    public void Error_wins_over_success_when_both_words_appear()
    {
        // "导出完成，但有 1 个标签失败" — a failure inside a success sentence must not read green.
        Assert.Equal(StatusSeverity.Error, StatusInference.Infer("导出完成，但有 1 个标签失败"));
    }
}

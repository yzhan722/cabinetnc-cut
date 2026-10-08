namespace CabinetNC.Desktop.Core;

public enum StatusSeverity
{
    Info,
    Success,
    Warning,
    Error,
    Busy,
}

/// <summary>
/// Sorts a free-text status message into a severity so the shell can colour it. Most call
/// sites pass plain Chinese text; the keyword lists are the contract, so keep them here and
/// tested rather than inline in the window.
/// </summary>
public static class StatusInference
{
    static readonly string[] ErrorWords =
        ["失败", "错误", "禁止", "无法", "不能", "没有 BMP", "异常", "拒绝", "无效", "找不到"];

    // "需要先 / 需要重新" describe a prerequisite the operator still has to do — a warning even
    // when the sentence also contains a success word such as 完成.
    static readonly string[] WarningWords =
        ["警告", "未通过", "作废", "失效", "请先", "需要先", "需要重新", "尚未", "缺", "跳过", "不匹配", "超出", "已取消"];

    static readonly string[] SuccessWords =
        ["已导出", "已写入", "已保存", "已载入", "已打开", "已计算", "成功", "完成", "通过", "就绪", "已应用", "已合并", "已删除", "已添加", "已更新", "已切换", "Saved"];

    public static StatusSeverity Infer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return StatusSeverity.Info;
        if (text.EndsWith("…", StringComparison.Ordinal)
            || text.EndsWith("...", StringComparison.Ordinal)
            || text.Contains("中…", StringComparison.Ordinal))
            return StatusSeverity.Busy;
        foreach (var k in ErrorWords) if (text.Contains(k, StringComparison.Ordinal)) return StatusSeverity.Error;
        foreach (var k in WarningWords) if (text.Contains(k, StringComparison.Ordinal)) return StatusSeverity.Warning;
        foreach (var k in SuccessWords) if (text.Contains(k, StringComparison.Ordinal)) return StatusSeverity.Success;
        return StatusSeverity.Info;
    }
}

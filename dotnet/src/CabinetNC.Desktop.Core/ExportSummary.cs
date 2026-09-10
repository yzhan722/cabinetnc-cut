namespace CabinetNC.Desktop.Core;

/// <summary>
/// What the operator is told after an export. Centralised because the 2026-08-19 labeling
/// incident was, in part, a status line that said "done" while the machine had nothing to
/// print; every wording here is testable.
/// </summary>
public static class ExportSummary
{
    public static string LabelStatus(int labelCount, string exportDir, string machinePictureDir, IReadOnlyList<string> missingStems)
    {
        if (missingStems.Count > 0)
        {
            return $"标签 {labelCount} 张在 {exportDir}；但程序请求的 {missingStems.Count} 个标签没有 BMP（{string.Join(", ", missingStems.Take(5))}），"
                 + "上机前必须补齐，否则 M701 会一直等待";
        }
        return $"标签 {labelCount} 张已平铺写入 {exportDir}，全部复制到机床 {machinePictureDir}（不要放子目录）";
    }

    public static (string Title, string Detail, StatusSeverity Severity, bool OfferOpenFolder) Toast(
        int fileCount, int labelCount, int missingLabels, string machinePictureDir)
    {
        if (missingLabels > 0)
        {
            return ($"导出完成，但有 {missingLabels} 个标签缺少 BMP",
                "程序里的 LS11 请求了不存在的标签图片。上机前补齐，否则机床会在 M701 一直等待。",
                StatusSeverity.Error, true);
        }
        var detail = labelCount > 0
            ? $"{labelCount} 张标签 BMP 已平铺写在同一目录。全部复制到机床 {machinePictureDir}，不要放子目录。"
            : "没有标签需要复制。";
        return ($"已导出 {fileCount} 个程序文件", detail, StatusSeverity.Success, true);
    }
}

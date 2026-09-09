namespace CabinetNC.Desktop.Core;

public sealed record Shortcut(string Group, string Keys, string Action);

/// <summary>
/// The keyboard/mouse bindings the shell implements, as data — shown by 帮助 → 快捷键 and
/// checked by a test so the sheet cannot drift from what MainWindow.OnPreviewKeyDown does.
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<Shortcut> All { get; } =
    [
        new("文件", "Ctrl+O", "打开方案"),
        new("文件", "Ctrl+Shift+O", "打开工程"),
        new("文件", "Ctrl+S", "保存工程"),
        new("文件", "Ctrl+E", "一键导出（需先完成密排与刀路）"),
        new("导航", "Ctrl+1 … Ctrl+5", "跳到 载入 / 板材 / 密排 / 刀路 / 导出"),
        new("编辑", "Ctrl+Z / Ctrl+Y", "撤销 / 重做"),
        new("编辑", "Ctrl+C / Ctrl+X / Ctrl+V", "复制 / 剪切 / 粘贴选中板件"),
        new("编辑", "Delete", "删除选中特征，否则删除选中板件"),
        new("编辑", "Enter / Esc", "数值框：提交 / 放弃"),
        new("视口", "滚轮", "以指针为中心缩放"),
        new("视口", "中键拖动", "平移"),
        new("视口", "F 或 Home", "适配整张大板"),
        new("视口", "+ / −", "放大 / 缩小"),
        new("密排", "左键拖动", "移动板件；拖到侧栏为暂存"),
        new("密排", "拖动中按右键", "旋转 90°"),
        new("密排", "拖动中按住 Alt", "锁定单轴移动"),
        new("密排", "拖动中按住 S", "吸附到相邻板件（按间距）"),
        new("密排", "选中两块时按住 D", "显示两块板的间距"),
        new("仿真", "Space", "播放 / 暂停（导出页）"),
        new("仿真", "点击代码行", "刀位跳到该块"),
    ];
}

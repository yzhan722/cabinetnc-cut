# Desktop 可测试性拆分计划（验收标准，非实现指南）

**现状（2026-09-02 量化）**：`CabinetNC.Desktop` 13.4k 行、0 个自动化测试。`MainWindow.xaml.cs` 7 395 行、
约 400 个方法、109 个字段、单文件、无 region；最近 30 个提交里改了 14 次。方法按职责粗分：
99 个事件处理器、76 个排版（nest/holding/PIP/guillotine/leftover）、58 个界面刷新、26 个导出/NC、
21 个阶段切换、20 个库/材料/刀具、10 个导入、6 个板件编辑、5 个贴标。

唯一的行为验证是 `tests/manual/smoke_desktop.py`（UIA），最后一次成功运行 2026-07-22，
控件名已经是 OmniCam 改版前的，upstream 自己的设计说明也承认它"不能当关门"。

这份文档不规定怎么拆，只规定拆完以后**什么算数**。谁来拆、什么顺序，由 Troy 决定；
建议从事故高发、逻辑最重、UI 最薄的两块开始。

## 目标状态

| 层 | 放什么 | 测试方式 |
|---|---|---|
| `CabinetNC.Domain` / `Application` | 一切算法与规则（已是现状） | xUnit（已有 430） |
| **`CabinetNC.Desktop.Core`（新，net10.0 纯类库）** | 视图模型、命令、导出流程编排、阶段状态机、拖拽状态机 | xUnit，不需要 WPF |
| `CabinetNC.Desktop`（WPF） | XAML、绑定、Skia 绘制、对话框、文件系统与 `MessageBox` 的薄封装 | 编译 + 少量 UIA 冒烟 |

关键约束：`Desktop.Core` **不能引用** `PresentationFramework` / `System.Windows.*`。这一条用一个测试锁住
（反射断言程序集引用），拆分过程中就不会"顺手"把 UI 类型带过去。

## 验收标准

### 阶段 1 —— 导出与贴标（事故高发区，先做）

- [ ] `ExportFlow`（或等价命名）在 `Desktop.Core`：输入 = 选中的 `ExportNcFile` 列表 + 目标目录 + `LabelerDefaults`；
      输出 = 要写的文件（路径、字节/文本）+ 状态文案 + 缺失标签列表。**不碰文件系统**，写盘由 WPF 层做。
- [ ] 单测：单文件导出、多文件导出、含 Process 2 且 BMP 齐全、含 Process 2 且缺 BMP（状态文案含缺失 stem）、
      `MachinePictureDir` 变更后文案跟随。
- [ ] `MainWindow.WriteExportNcFiles` / `OnSaveNcClick` / `WriteLabelBmps` 退化为：调用 `ExportFlow`，把结果写盘，`SetStatus`。
- [ ] `GuardExportPreflight` 的判定逻辑进 `Desktop.Core`，UI 只负责弹窗。

### 阶段 2 —— 阶段状态机与工作流点

- [ ] `_stage`、`StageTabs.SelectedIndex`、`ApplyStageVisibility`、`UpdateStageChrome`、`RefreshWorkflowDots` 背后的
      "哪个阶段可进、哪些按钮可用、脏标记如何传播"抽成 `StageMachine`。
- [ ] 单测：导入后只有 1–2 阶段可用；改几何后 Nest/CAM 标脏且导出被挡；重新 Nest 后恢复；`HasNcText()` 为假时导出不可用。

### 阶段 3 —— 排版交互状态机

- [ ] `_dragMode`（`nest` / `label` / `nestBox` …）、holding bay、PIP、guillotine 预览的状态转换抽成一个纯 C# 状态机；
      输入是抽象的指针事件（位置、按下/抬起/移动），输出是要执行的 Domain 调用与要重绘的区域。
- [ ] 单测：拖一块板到另一块板上被拒绝并回弹；拖标签锚点越界被夹取；右键卸载 cnjob 不删文件；双击改名 stock kind。

### 阶段 4 —— 面板编辑与 Undo

- [ ] `PanelDraftWindow.xaml.cs`（1 916 行）的草稿编译走 `PanelDraftCompile`（已在 Domain），窗口只做输入输出；
      Undo/Redo 栈进 `Desktop.Core`。

### 全局

- [ ] `MainWindow.xaml.cs` ≤ 2 500 行，且不再直接调用 `ContourToolOffset` / `NcEmitter` / `GroupedBlfNester` /
      `NcPreflight`（目前 20 处），全部经 `Application`/`Desktop.Core`。
- [ ] `MessageBox.Show`（目前 16 处）收敛到一个 `IUserPrompt` 接口，`Desktop.Core` 只依赖接口。
- [ ] Windows CI 里 `dotnet test tests/CabinetNC.Desktop.Core.Tests` 加入门禁。
- [ ] `smoke_desktop.py` 重新录制到 OmniCam 控件名，或者删掉并在 `KNOWN_LIMITATIONS.md` 明写"无 UIA"。不要留一个跑不起来的脚本装作有覆盖。

## 不要做

- 不要为了拆而引入 MVVM 框架大迁移（`CommunityToolkit.Mvvm` 可以用，但不是先决条件）。
- 不要在拆分 PR 里同时改行为。拆分 PR 的 golden、安全不变量、往返测试必须与拆分前完全一致。
- 不要一次拆完。每个阶段一个 PR，≤ 1 500 行。

## 度量

每次 PR 合并后更新这一行：

| 日期 | `MainWindow.xaml.cs` 行数（非空行） | Desktop.Core 测试数 | 直接 Domain 算法调用数 | `MessageBox.Show` 数 |
|------|---------------------------|---------------------|------------------------|----------------------|
| 2026-09-02 | 7 395 | 0 | 20 | 16 |
| 2026-09-02（UI 改版后） | 7 799（+404：状态/通知系统、引导态、快捷键、导出流程；样式已移出到 `Theme.xaml` 442 行） | 0 | 20 | 16（预检 Yes/No 改为专用对话框，快捷键说明用了一个新的） |
| 2026-09-03（CAD/CAM 惯例对照后） | 约 8 100（+300：全阶段视口、仿真传输/DRO/代码联动、菜单命令、回车提交） | 0 | 20 | 16 |
| 2026-09-03（Desktop.Core 第一步） | 8 426（未保存模型、最近文件、反推对照、图层开关、取消密排又加了功能；纯逻辑已迁出 257 行） | **43** | 20 | 18（关闭/打开前的"保存吗"两个提示） |

### 阶段 0 已完成：`CabinetNC.Desktop.Core` 建立

`dotnet/src/CabinetNC.Desktop.Core`（net10.0，仅引用 Domain 与 Infrastructure）与 `dotnet/tests/CabinetNC.Desktop.Core.Tests`
已进入解决方案和两个 CI 工作流。第一批迁入的都是纯函数 / 纯状态：

| 类型 | 职责 | 原来在 |
|---|---|---|
| `StatusInference` | 状态文案 → 严重度（关键词表是契约） | `MainWindow.InferStatusKind` |
| `RecentFiles` | MRU 去重 / 上限 / 访问键转义 / 种类标签 | `RememberRecentFile` 等 |
| `WorkFingerprint` | 可保存内容指纹（剔除视图状态与衍生 Ops） | `WorkFingerprint()` |
| `NcSimTimeline` | 段起点、步进、按源码行定位 | `_ncSimStarts` + 三个处理器 |
| `ViewportMath` | 适配比例、绕指针缩放、屏幕↔板料坐标 | `CurrentNestFit` / `ZoomViewportAt` / `ScreenToSheet` |
| `ExportSummary` | 导出后的状态栏与通知文案 | `WriteLabelBmps` / `AnnounceExport` |

守卫测试 `NoWpfDependencyTests` 断言该程序集不引用 PresentationFramework / PresentationCore / WindowsBase / System.Xaml。
迁出即有收益：`NcSimTimelineTests` 在第一次运行就抓到"仿真时间正好在段起点时，下一段原地不动"的边界 bug，Desktop 原实现同样有这个问题。

下一步（阶段 1）：`ExportFlow` —— 把 `WriteExportNcFiles` / `WriteLabelBmps` 里"要写哪些文件、写到哪、缺什么"的决策变成纯函数，文件系统只留写盘一行。

说明：UI 改版把"状态栏 + Toast + 作废横幅 + 步骤标签"的逻辑全部写在了 code-behind 里，是阶段 1/2 拆分时第一批要搬进 `Desktop.Core` 的内容（`SetStatus/InferStatusKind`、`ShowToast`、`RefreshStaleBanner`、`RefreshWorkflowDots`、`AnnounceExport` 都是纯函数或接近纯函数）。

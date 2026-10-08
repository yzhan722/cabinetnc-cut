# 贴标（Process 2）需求与验收 — 从 2026-08-19 事故导出

来源：`LABELING_INCIDENT_HANDOVER_2026-08-19.md`。事故根因是标签软件网卡地址错误，
但交接文档同时指出了软件侧 5 处可以防止同类停机的改进。本文把它们写成可验收的需求，
并记录当前状态。状态含义：**Done** 有代码和测试；**Partial** 有代码无自动验证或只覆盖一部分；
**Open** 未实现，写明阻塞点。

| # | 需求 | 验收条件 | 状态（2026-09-02） |
|---|------|----------|-------------------|
| L1 | 机床标签图片目录可配置，默认 `D:\CNC`，不再写死 `D:\Label` | (a) `library.json` 有 `labeler.machinePictureDir`，缺省 `D:\CNC`；(b) 导出状态栏显示的是该配置值；(c) 代码中不再出现字面量 `D:\Label`；(d) 有 UI 可编辑 | **Done** — (a)(b)(c) 在 `WorkshopLibrary.Labeler` / `MainWindow.WriteLabelBmps`；(d) 「参数设置 → 机床与贴标」卡片，保存写回 `library.json` |
| L2 | 导出时给出可直接平铺复制的目录，并明确最终目标路径 | (a) BMP 与 `.anc` 写在同一目录，不再放 `label\` 子目录；(b) 导出后给出目标目录并注明"不要放子目录"；(c) 能一键打开导出目录 | **Done**（Desktop）：状态栏 + 导出通知卡（绿色，带「打开目录」）；缺 UIA/手工用例，见 `MANUAL_SMOKE_10MIN.md` 待补 |
| L3 | 导出前校验 ANC 中每个 `LS11` 都有同名 BMP | (a) Domain 提供纯函数 `LabelExport.Ls11Stems` / `MissingBitmaps`，大小写不敏感；(b) 导出后对目录做一次核对，缺失则以红色错误通知卡列出缺失 stem（停留 12 秒，带「打开目录」），并写入 `UsageLog`（`export.labels.missing`）；(c) 单测覆盖 | **Done**（`LabelExportTests.Ls11Stems_*`、`MissingBitmaps_*`）。注意目前是"写完再核对并警告"，不是"阻断导出"——阻断需要 Troy 确认车间是否接受 |
| L4 | `E41/E42` 重试加上限或超时，失败后明确停机报警 | (a) Process 2 中 `(GTO,STxx,E41=0)` 循环有次数上限；(b) 超限后 `M00`/报警并显示 stem；(c) 有对应的 golden/单测 | **Open** — 需要 OSAI 控制器手册确认：`E41/E42` 的语义、可用的计数变量、报警指令写法。在机床上验证前不改 `EmitPro2` 的循环结构 |
| L5 | 机床联调确认前不改 U/V 坐标算法 | 任何改动 `LabelAnchorFinder` / `NestTransform.ToSheet` 的 PR 必须附上机验证记录 | **Done**（流程约束，写入 `POST_CHANGE_CHECKLIST.md`） |

## 补充需求（本次审查新增）

| # | 需求 | 验收条件 | 状态 |
|---|------|----------|------|
| L6 | 上机检查单包含贴标网络与目录核对 | `MACHINE_DRYRUN_CHECKLIST.md` "Before power" 段有 `LS11`↔BMP、`192.168.0.4`、ping `.2`/`.3` 三项 | **Done** |
| L7 | 标签 stem 与 BMP 文件名的规则在一处定义 | `LabelExport.SafeStem` 是唯一来源，Desktop 直接用 `paste.Stem + ".bmp"` | **Done**（已是现状，本次确认） |
| L8 | 隐藏扩展名导致 `*.bmp.bmp` 的问题在导出侧不可能发生 | Desktop 用 `File.WriteAllBytes(stem + ".bmp")` 写文件，不经过用户输入 | **Done**（已是现状） |

## 待 Troy 决策

1. L3 的缺失 BMP 是**警告**还是**阻断导出**？建议阻断（M701 无限等待的代价是整机停机），但需要车间确认没有"先出 NC 后补标签"的工作流。
2. L4 需要 OSAI 手册或现场试验：`E41=0` 在 M701 失败时是否会被置位、是否有可用的用户变量做计数。
3. L1 的 UI：是否放进"车间库 → 排版默认值"页，还是单独一个"机床"页。

# Intranet Cloud PoC — Implementation Notes

按任务追加记录。每条记录写清：做了什么、观察到什么、哪些是 PASS / FAIL / BLOCKED / PARTIAL，不得把 BLOCKED 写成 PASS。

计划文档：

- Spec: `docs/superpowers/specs/2026-09-05-intranet-cloud-design.md`
- Plan: `docs/superpowers/plans/2026-09-05-intranet-cloud-implementation.md`
- 入口: `CURSOR_START_HERE.md`

---

## Task 1 — Baseline and branch（2026-09-06）

### 1.1 仓库与分支状态

| 项目 | 值 |
|---|---|
| Repo | `yzhan722/cabinetnc-cut` (public, default branch `main`) |
| 计划基线 | `sprint/14d-rc @ 5e410d554e17ba77d2dbb8deb3ff1967154d0c67` |
| 实际 `sprint/14d-rc` HEAD (2026-09-06) | `5e410d554e17ba77d2dbb8deb3ff1967154d0c67` — **与基线一致，RC 未前进**，无需比对差异 |
| 工作分支 | `feature/intranet-cloud-poc`（从 `sprint/14d-rc` 新建） |
| `git status --short`（克隆后） | clean |
| 最近 5 commit | `5e410d5 ci: make the UI smoke a blocking step…` / `583551f docs: CHANGES_SINCE_TROY…` / `f5fe968 chore(eval)…` / `155f836 chore(eval)…` / `7d3cb1c fix(desktop)…` |

**权限提醒**：当前执行账号（gh `xbbwa`）对该仓库只有 `pull` 权限（`push: false`）。所有 commit 只能留在本地或推到 fork；合并回 `yzhan722/cabinetnc-cut` 需要维护者操作或 PR。

### 1.2 执行环境

机器 A（2026-09-06 上午，Task 1 原始执行）：

| 组件 | 版本 / 状态 |
|---|---|
| OS | Windows 10 (10.0.19045) |
| .NET SDK | 10.0.302 |
| git | 2.54.0.windows.1 |
| gh | 2.97.0（已登录，pull-only） |
| Docker | 29.7.2（本轮尚未使用） |
| PowerShell | Windows PowerShell 5.1 —— **未安装 pwsh 7**。计划中 `pwsh dotnet/tests/ui-smoke/run-all.ps1` 改用 `powershell -NoProfile -ExecutionPolicy Bypass -File dotnet\tests\ui-smoke\run-all.ps1` 可运行（脚本本身兼容 5.1）。 |

机器 B（2026-09-06 下午起，从交接包 bundle 还原，Task 2 起在此执行）：

| 组件 | 版本 / 状态 |
|---|---|
| OS | Windows 10 (10.0.19045) |
| .NET SDK | 10.0.400（用户级安装 `%LOCALAPPDATA%\Microsoft\dotnet`；系统 PATH 里的 `C:\Program Files\dotnet` 只有 9.0.8 运行时、无 SDK，需把用户级目录放到 PATH 前面并把 `DOTNET_ROOT` 指向它，否则 `dotnet --version` 报 "No .NET SDKs were found"） |
| git | 2.54.0.windows.1 |
| Docker | **未安装**（Task 4/5/8 前需补装 Docker Desktop） |
| PowerShell | Windows PowerShell 5.1 + pwsh 7（WindowsApps） |
| 仓库来源 | 交接包 `cabinetnc-cut.bundle`（`git bundle verify` = complete history）；`resume.ps1` 的 `git bundle verify` 需在某个 git 仓库目录内执行，父目录不是仓库时会报 `need a repository to verify a bundle` |
| 基线核对 | `origin/sprint/14d-rc == 5e410d554e17ba77d2dbb8deb3ff1967154d0c67`，与计划基线一致，RC 仍未前进 |

### 1.3 .NET 回归基线 — PASS

命令：`dotnet test dotnet/CabinetNC.slnx -c Release --verbosity minimal`

| 测试项目 | 通过 | 失败 | 跳过 |
|---|---|---|---|
| CabinetNC.Package.Tests | 40 | 0 | 0 |
| CabinetNC.Domain.Tests | 431 | 0 | 0 |
| CabinetNC.Infrastructure.Tests | 11 | 0 | 0 |
| CabinetNC.Desktop.Core.Tests | 72 | 0 | 0 |
| **合计** | **554** | **0** | **0** |

Release 构建 `CabinetNC.ComputeWorker` 与 `CabinetNC.Desktop`：成功。Desktop 有 6 条既有 `NU1701` 警告（SkiaSharp.Views.WPF / OpenTK 针对 .NETFramework 还原），基线即存在，非本轮引入。

机器 B 复跑（2026-09-06，bundle 还原后未改任何代码）：同一命令 **554 / 0 / 0**（431 + 40 + 11 + 72），Worker 与 Desktop Release 构建成功，同样仅 6 条 `NU1701`。

### 1.4 Windows UI smoke — PASS（机器 B 交互式桌面会话 5/5；机器 A 首跑为 PARTIAL）

命令：`powershell -NoProfile -ExecutionPolicy Bypass -File dotnet\tests\ui-smoke\run-all.ps1`
结果文件：`dotnet/artifacts/ui-smoke/results.json`（已 gitignore，副本随交接包附带）

**第一次（机器 A，agent 驱动的 shell 会话）— PARTIAL：**

| 场景 | 结果 | 说明 |
|---|---|---|
| 01-demo-to-export | FAIL | 4 个失败全部是 `shot:*.png` 截图步骤：`CopyFromScreen` 抛“句柄无效”。**所有功能步骤（invoke / tab / assert-status / assert-title / assert-file 导出 `Left_side.bmp`）全部 ok。** |
| 02-stale-banner | FAIL | 2 个失败同样全部是 `shot:` 步骤；功能步骤全部 ok。 |
| 03-anc-reverse-recut | PASS | 含 2 张截图成功 |
| 04-library-recovery | PASS | 含 2 张截图成功 |
| 05-corrupt-project | PASS | 含 1 张截图成功 |

判定：3/5 PASS；01/02 归类为 `ENVIRONMENT`（截图 GDI 捕获在 agent 驱动的 shell 会话前两分钟内失败，随后场景截图恢复正常），未发现产品功能回归；按规则不记为 PASS。

**第二次（机器 B，交互式桌面会话，基线代码未改）— PASS 5/5：**

`results.json`（`schema: cabinetnc.ui-smoke`，`ranAt: 2026-09-06T14:14:47+08:00`，`passed: true`）：

| 场景 | 结果 | failures |
|---|---|---|
| 01-demo-to-export | PASS | `[]`（20 步全部 ok，含 4 张截图、`Left_side.bmp` 导出） |
| 02-stale-banner | PASS | `[]`（11 步全部 ok，含 2 张截图） |
| 03-anc-reverse-recut | PASS | `[]`（18 步全部 ok，`NC_01.bmp` / `NC_02.bmp` 导出） |
| 04-library-recovery | PASS | `[]`（7 步全部 ok，含从 `.bak` 恢复提示） |
| 05-corrupt-project | PASS | `[]`（4 步全部 ok） |

11 张截图（`01-empty.png` … `11-after-corrupt-project.png`）全部生成，退出码 0。这证实机器 A 的 01/02 失败确为会话环境问题，不是产品回归。

### 1.5 现有 compute 调用点（`rg -n "WorkerProcessHost|GetNestingClient|StartNesting|GenerateOperations|GenerateNc" dotnet/src`）

```text
dotnet/src/CabinetNC.Desktop/Worker/WorkerProcessHost.cs:11    class WorkerProcessHost : IAsyncDisposable
dotnet/src/CabinetNC.Desktop/Worker/WorkerProcessHost.cs:103   GetNestingClient()
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:52             readonly WorkerProcessHost _worker = new();
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:92             StartNestingReply? _nest;           (UI 状态容器，复用 proto 类型)
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:302            _worker.DisposeAsync()
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:5266           async Task RunNestAsync(bool withNc)   ← 真正的 Nest 调用路径
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:5325-5337      Task.Run(() => new NestEngineRouter(advanced: …).Run(new NestEngineRequest{…}))
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:5352           _nest = new StartNestingReply { … }   (把 NestResult 装回 UI 容器)
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:6160           _nest = new StartNestingReply { … }   (打开工程时还原已保存摆位，非计算)
dotnet/src/CabinetNC.Desktop/MainWindow.xaml.cs:6866/6879      _worker.EnsureStartedAsync() / GetHealthClient()   (RefreshWorkerAsync 自检)
dotnet/src/CabinetNC.ComputeWorker/Services/NestingServiceImpl.cs:12    NestingServiceImpl.StartNesting (gRPC)
dotnet/src/CabinetNC.ComputeWorker/Services/NestingServiceImpl.cs:125   OperationsServiceImpl.GenerateOperations (gRPC)
dotnet/src/CabinetNC.ComputeWorker/Services/PostProcessorServiceImpl.cs:13  PostProcessorServiceImpl.GenerateNc (gRPC)
```

### 1.6 重要架构发现（影响 Task 2 / Task 9 的做法）

1. **Desktop 当前并不经 gRPC 做 Nest。** `RunNestAsync` 在 Desktop 进程内直接调用 `CabinetNC.Domain.Nesting.NestEngineRouter`（`Task.Run`），输入是完整 `Panel`（真实 outline、grain、AllowedRotations）、多张 `NestSheetSpec` 队列（余料/keep-out）、引擎偏好（`nfp` / `deepnest` / `blf`）、进度回调、锁定摆位、parts-in-part 等。`WorkerProcessHost` 目前只用于 `RefreshWorkerAsync` 健康自检和 UI 徽标。
2. `CabinetNC.ComputeWorker/Services/NestingServiceImpl.cs` 是一条**独立的矩形（AABB）Nest 路径**：proto `StartNestingRequest`（parts + sheet + spacing + border + allowRotation）→ `Panel` 矩形 → `NestEngineRouter` (`EnginePreference = "blf"`) → `NestValidator.FindAabbCollisions`。它和 spec §9 的 Nest Cloud Contract 字段一一对应。
3. 因此：
   - Task 2 的 `INestingRunner` 应从 `NestingServiceImpl` 提取（矩形契约，`blf` 偏好，默认 border 15 / spacing 12 / 1220×2440），本地 gRPC 变成 adapter。
   - Task 9 的 `LocalComputeGateway` 若按计划走 `WorkerProcessHost/gRPC`，那 Local/Server parity 比较的是**同一个 runner** 在两端的输出，parity 可做 exact equality。
   - Desktop 现有进程内 NFP/Deepnest 路径**不在**本 PoC 的云化范围内（spec §9 契约是矩形），Task 9 接入时只替换 `RunNestAsync` 里 `NestEngineRouter.Run` 那一段的“取结果”环节，且 Intranet 模式下的输入必须先降级成矩形 AABB 契约；差异（引擎、余料队列、锁定摆位）要在 `IMPLEMENTATION_NOTES.md` 和 acceptance 里明说，不得偷偷吞掉。
4. `.gitignore` 含 `.env.*`，会把 Task 8 的 `deploy/intranet/.env.example` 忽略掉。Task 8 需追加 `!deploy/intranet/.env.example`。
5. `CabinetNC.Desktop.csproj` 的 `CopyWorker` target 在 Build/Publish 后把 `CabinetNC.ComputeWorker` 输出复制进 Desktop 输出目录和 `dist/CabinetNC-Cut`。Task 12 的 customer package 校验要覆盖这条链路（customer build 必须能跳过/排除该 target）。
6. 现有测试全部是 xunit 2.9.3 + `Microsoft.NET.Test.Sdk 17.14.1` + `coverlet.collector 6.0.4`，`<Using Include="Xunit" />`。新测试项目沿用同一套。
7. 解决方案文件是 `dotnet/CabinetNC.slnx`（XML 格式），新项目要加进 `/src/` 与 `/tests/` folder。
8. CI：`regression.yml`（ubuntu，按项目逐个 `dotnet test`，不测 Desktop）；`windows-desktop.yml`（windows-latest，构建 Desktop + Worker，跑 4 套测试 + UI smoke，UI smoke 已是 blocking）。新增 Cloud 测试项目需要同步加进这两个 workflow；需要 PostgreSQL/MinIO 的集成测试要在 CI 用 service container 或标记为可跳过（缺环境时 SKIP 并明示，不得假 PASS）。

### 1.7 本任务产出

- `docs/cloud/IMPLEMENTATION_NOTES.md`（本文件）
- `docs/cloud/INTRANET_POC_RUNBOOK.md`（初版，Task 8/9 后补全 compose/证书/Desktop 配置）
- 计划文档导入仓库：`CURSOR_START_HERE.md`、`docs/superpowers/specs/2026-09-05-intranet-cloud-design.md`、`docs/superpowers/plans/2026-09-05-intranet-cloud-implementation.md`
- Commit: `docs: establish intranet cloud poc baseline`

### 1.8 Task 1 Gate

- .NET regression：PASS（554/554，机器 A 与机器 B 各一次）
- UI smoke：PASS（机器 B 交互式会话 5/5；机器 A 首跑 3/5 的失败已确认为会话环境问题）
- 无未解释的基线失败。**Gate 通过，可进入 Task 2。**

---

## Task 2 — Extract shared Nesting runner

NOT STARTED（交接点）。见 `docs/cloud/HANDOFF_2026-09-06.md` 的“下一步从这里开始”。

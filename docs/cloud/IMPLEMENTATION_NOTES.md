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

## Task 2 — Extract shared Nesting runner（2026-09-06，机器 B）

### 2.1 做了什么

新建 `CabinetNC.Compute.Core`（net10.0，只引用 `CabinetNC.Domain`）：

| 文件 | 内容 |
|---|---|
| `Nesting/NestingInput.cs` | `NestingPartInput` / `NestingInput` record，字段与 proto `NestPartMsg` / `StartNestingRequest` 一一对应 |
| `Nesting/NestingOutput.cs` | `NestingPlacementOutput` / `NestingWarningOutput` / `NestingOutput` record |
| `Nesting/INestingRunner.cs` | `NestingOutput Run(NestingInput input)` |
| `Nesting/NestingRunner.cs` | 原 `NestingServiceImpl.StartNesting` L14–119 的编排逐行搬入；默认值提成常量 `DefaultBorderMm=15 / DefaultSpacingMm=12 / DefaultSheetWidthMm=1220 / DefaultSheetLengthMm=2440 / EnginePreference="blf" / StockLabel="STOCK"`；算法仍是 Domain 的 `NestEngineRouter → GroupedBlfNester` + `NestValidator.FindAabbCollisions`，未重写任何算法 |

新建 `CabinetNC.Compute.Core.Tests`（xunit 2.9.3 / Test.Sdk 17.14.1 / coverlet 6.0.4，与既有测试项目同一套），`NestingRunnerTests.cs` 7 个用例——先写测试，对着 `throw new NotImplementedException()` 的桩跑出 7 红，再搬实现跑出 7 绿：

| 用例 | 锁定的行为 |
|---|---|
| `Run_places_two_rectangles_without_overlap` | 2 件同板、`grouped_blf_v0`、间距 ≥ spacing、四边不越 border、无 `aabb_gap` |
| `Run_honors_no_rotation_part` | 300×1200 板上 1000×100：`MayRotate=true` → 90° 放入；`MayRotate=false` → `Unplaced=["R"]`、`SheetCount=0` |
| `Run_returns_unplaced_for_oversized_part` | 3000×3000 进 `Unplaced`，同请求的 500×500 正常放置，`Ok=true`、`Error=null` |
| `Run_preserves_material_and_thickness_behavior` | MDF·18 ×2 / MDF·25 / Plywood·12 → 3 张板、不同组不共板、同组共板（空白 STOCK 模板按组克隆） |
| `Run_is_deterministic_for_same_input` | 24 件混合输入，同实例两次 + 新实例一次，placements / unplaced / warnings 全等 |
| `Run_applies_legacy_worker_defaults_for_non_positive_dimensions` | sheet/spacing/border 传 0 → 1220×2440 / 15 边距：1190×2410 落在 (15,15)，1200×2410 不可转 → unplaced |
| `Run_returns_error_instead_of_throwing` | 重复 PanelId 在 validator 里抛 `ArgumentException` → `Ok=false`、`Error` 非空、集合全空，不向上抛 |

修改：

| 文件 | 改动 |
|---|---|
| `dotnet/src/CabinetNC.ComputeWorker/Services/NestingServiceImpl.cs` | 变成 adapter：`ToInput(proto)` → `runner.Run` → `ToReply(output)`；构造函数注入 `INestingRunner`；失败路径仍返回 `Ok=false, Error=ex.Message`（`Engine=""`、集合空）。`OperationsServiceImpl` 未动 |
| `dotnet/src/CabinetNC.ComputeWorker/Program.cs` | `builder.Services.AddSingleton<INestingRunner, NestingRunner>()` |
| `dotnet/src/CabinetNC.ComputeWorker/CabinetNC.ComputeWorker.csproj` | 引用 `CabinetNC.Compute.Core` |
| `dotnet/CabinetNC.slnx` | `/src/` 加 Compute.Core，`/tests/` 加 Compute.Core.Tests |
| `.github/workflows/regression.yml`、`windows-desktop.yml` | 各加一步 `Compute.Core tests` |
| `dotnet/scripts/smoke-worker.ps1` | 原来无条件把 `C:\Program Files\dotnet` 放到 PATH 最前，在机器 B 上会遮住用户级 SDK 导致 Debug 构建失败、exe 不存在；改为仅当 PATH 上没有 `dotnet` 时才追加。机器 A 行为不变 |

### 2.2 Gate：本地 gRPC Nest 行为不变 — PASS

1. **A/B 逐字节对比（最强证据）**：用 `3a5ef1c`（Task 2 之前）在独立 worktree 构建旧 Worker，与工作树的新 Worker 各起一次，用同一个一次性 gRPC 探针客户端（源码存 `.handoff/local-evidence/grpc-probe/`，未入库）通过 Named Pipe `cabinetnc.compute.v1` 发 11 个 `StartNesting` 请求，回复以 protobuf canonical JSON 落盘：`.handoff/local-evidence/task2-grpc-replies-{old,new}-worker.txt`。**两份文件完全一致（5837 字符 / 22 行）**。覆盖：全默认、需旋转（允许/锁定）、超尺寸、混合材质厚度、重复 ID 错误路径、24 件混合、显式 spacing 30 / border 20 / AllowRotation=false、空请求、零宽零件、恰好填满内框 + 超 10 mm。
2. `dotnet test dotnet/CabinetNC.slnx -c Release`：**561 / 0 / 0**（Compute.Core.Tests 7 + Domain 431 + Package 40 + Infrastructure 11 + Desktop.Core 72）。日志 `.handoff/local-evidence/task2-dotnet-test-release.log`。
3. `powershell -File dotnet\scripts\smoke-worker.ps1`：`OK worker-alive pid=… pipe=cabinetnc.compute.v1`（修脚本 PATH 前缀后）。
4. `CabinetNC.ComputeWorker` Release 构建 0 警告 0 错误；`CabinetNC.Desktop` Release 构建成功（仍只有 6 条既有 `NU1701`），`CopyWorker` 目标按目录整体复制，`CabinetNC.Compute.Core.dll` 已出现在 `Desktop\bin\Release\net10.0-windows\` 和 `dist\CabinetNC-Cut\`（否则 Desktop 自带的 Worker 会因缺程序集起不来）。

### 2.3 观察到的既有行为（原样保留，未修，供 Task 3 / 7 / 9 / 10 决策）

这些在旧 Worker 和新 Worker 上完全一致，均由 A/B 回复文件佐证：

1. **请求里的 `SpacingMm` / `AllowRotation` 不影响排版，只影响事后校验。** `GroupedBlfNester.Pack` 调 `NestStockOverrides.ForGroup(settings, stock)`，用 stock 模板的 `SpacingMm`（默认 12）和 `AllowRotation`（默认 true）覆盖全局 settings；而 Worker 构造的 `STOCK` 模板没有设这两个字段。R7（spacing 30、AllowRotation=false）实测：零件按 12 mm 间距摆放（B 在 x=20+500+12=532）、D 仍被转了 90°，然后 `NestValidator.FindAabbCollisions(…, 30)` 报出 4 条 `aabb_gap`。**边距 `BorderMm` 是生效的**（stock.BorderMm=border）。**每零件 `MayRotate=false` 是生效的**（走 `Panel.AllowedRotations=[0,180]`）。Task 3 定契约时要决定：是把 `SpacingMm`/`AllowRotation` 写进 stock 模板让它们真正生效（行为变化，需要走 parity 验收），还是在契约里明说这两个字段只用于校验。
2. **`BlfNester.SplitFree` 只在已放零件的右侧/上方预留 gap。** R6（默认 spacing）实测 P01（转 90°，右边缘 x=559）与 P03（左边缘 x=560）只隔 1 mm，被 validator 报 `aabb_gap P03 × P01 on sheet 1`。这是 Domain 引擎的既有行为，属于制造语义，本 PoC 不改；但说明 Local/Server parity 断言必须包含 `warnings`，且 Task 10 的“可导出”判定不能只看 `Ok`。
3. **宽或高 ≤ 0 的零件被 `BlfNester` 静默丢弃**（`Where(p => p.WidthMm > 0 && p.HeightMm > 0)`），既不在 `placements` 也不在 `unplaced`（R9）。Task 3 的 DTO 校验应在进入 runner 之前拒绝这类输入。
4. 重复 `PanelId` 走异常路径：`Ok=false, Error="An item with the same key has already been added. Key: DUP"`（R5）。同样应由 Task 3 的输入校验提前拦截并给出可读错误。
5. 空请求返回 `Ok=true, SheetCount=0`（R8）。

### 2.4 本任务产出

- 新项目 `CabinetNC.Compute.Core`、`CabinetNC.Compute.Core.Tests`；Worker 变 adapter；CI 两个 workflow 加 Compute.Core 测试步。
- 证据（未入库，随交接目录）：`.handoff/local-evidence/task2-*`、`grpc-probe/`。
- Commit：`refactor: extract transport-neutral nesting runner`。

### 2.5 Task 2 Gate

- 焦点测试 7/7、全量 561/561、smoke-worker PASS、A/B gRPC 回复逐字节一致、Desktop 打包含新 DLL。**Gate 通过，可进入 Task 3（`CabinetNC.Cloud.Contracts`）。**

---

## Task 3 — Cloud contracts

NOT STARTED。

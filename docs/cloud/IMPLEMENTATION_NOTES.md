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
| Docker | 2026-09-06 15:00 安装 **Docker Desktop 4.89.0**（Engine 29.7.2，WSL 2 后端）。**按用户模式**（`install --user`，无需管理员、不装特权服务），程序在 `D:\Docker\Program`，WSL 数据盘在 `D:\Docker\wsl`。前置：机器自带的 inbox WSL 不满足 ≥ 2.1.5，先 `wsl --update`（无需提权）升到 WSL 2.7.13 / 内核 6.18。8 月 3 日的安装失败是因为当时系统还是 19042，现已 19045。 |
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

## Task 3 — Cloud contracts（2026-09-06，机器 B）

### 3.1 做了什么

新建 `CabinetNC.Cloud.Contracts`（net10.0，**零项目引用、零 NuGet**——只用 BCL 的 System.Text.Json，这样 Task 12 的 customer build 可以带它而不带 Domain / Compute.Core）：

| 文件 | 类型 | 说明 |
|---|---|---|
| `AuthContracts.cs` | `LoginRequest(Tenant, Email, Password, DeviceId, DeviceName?)` / `LoginResponse(AccessToken, AccessTokenExpiresInSeconds, RefreshToken, RefreshTokenExpiresInSeconds, TenantId, UserId, DeviceId, Role)` / `RefreshRequest(RefreshToken, DeviceId)` / `RefreshResponse(AccessToken, AccessTokenExpiresInSeconds, RefreshToken, RefreshTokenExpiresInSeconds)` / `LogoutRequest(RefreshToken, DeviceId)` | 过期用**相对秒数**而不是绝对时间，Desktop 时钟偏差（spec §6 允许 ≤ 60 s）不影响“提前 60 s 刷新”的判断。Task 6 给 LoginRequest 增加 tenant slug，用于在允许 `(TenantId, Email)` 重复的模型里定位 tenant；它不是客户端指定的可信 TenantId |
| `JobContracts.cs` | `JobStatus { Queued, Running, Succeeded, Failed }` / `NestPartDto` / `SubmitNestJobRequest` / `SubmitNestJobResponse(JobId, Status, CorrelationId)` / `JobStatusResponse(JobId, JobType, Status, AttemptCount, CreatedAtUtc, StartedAtUtc?, CompletedAtUtc?, ErrorCode?, ErrorMessage?, DurationMs?, CorrelationId)` / `NestPlacementDto` / `NestWarningDto` / `NestJobResult(JobId, Engine, EngineVersion, Placements, SheetCount, Unplaced, Warnings, InputSha256, ResultSha256, DurationMs)` | `SubmitNestJobRequest` 字段 = spec §9 request = 本地 Worker `StartNestingRequest` = `Compute.Core.NestingInput`，一一对应；`NestJobResult` = spec §9 result + `JobId`。`JobId` 用 `Guid` |
| `ApiError.cs` | `ApiError(Code, Message, CorrelationId)` + `ApiErrorCodes`（12 个常量 + `All`） | 与 spec §12 的 `{"code","message","correlationId"}` 逐字节一致 |
| `ApiRoutes.cs` | `ApiRoutes`（7 条路径常量 + `ForJobStatus(Guid)` / `ForJobResult(Guid)`）、`ApiHeaders`（`Idempotency-Key`、`X-Correlation-ID`）、`JobTypes.Nest = "nest"` | API 和 Desktop 客户端共用同一组字符串 |
| `CloudJson.cs` | `CloudJson.Options`（只读单例）+ `Serialize<T>` / `Deserialize<T>` | camelCase、声明顺序、显式写 null、enum 只接受/输出字符串（`allowIntegerValues:false`）、读时大小写不敏感、忽略未知字段（向前兼容）。`Deserialize` 对 `null` 正文抛 `JsonException` 而不是返回 null |

`CloudJson.Options` 是 Task 7 计算 `inputSha256` 的规范化序列化配置——**改它就等于改所有哈希**，文件头注释已写明。

新建 `CabinetNC.Cloud.Contracts.Tests`，`ContractJsonRoundTripTests.cs` 16 个用例（含 Theory 展开）。先写测试、后写类型（纯 DTO，红=编译失败）；第一版 `CloudJson` 用了无参 `MakeReadOnly()`，所有用例以 `TypeInitializationException` 失败（"must specify a TypeInfoResolver"），改为 `MakeReadOnly(populateMissingResolver: true)` 后 16/16 绿。关键断言：

- `SubmitNestJobRequest_canonical_json_is_stable`：**整串 JSON 逐字符固定**（`{"parts":[{"panelId":"A","widthMm":600,…}],"sheetWidthMm":1220,…,"allowRotation":true}`），且 `Serialize(Deserialize(json)) == json`。
- `JobStatus_serializes_as_spec_strings` ×4 + `JobStatus_rejects_unknown_and_numeric_values`（`"Cancelled"`、`2` 都抛）。
- `ApiError_matches_spec_shape`：spec §12 示例原样反序列化再序列化得到同一串。
- `ApiErrorCodes_cover_spec_list`：12 个 code 与 spec 顺序、内容、唯一性一致。
- `NestJobResult_round_trips_with_nullable_warning_fields`：`engine_fallback` 的 `panelIdA/B/sheetIndex` 以 `null` 显式输出并回读。
- `Deserialize_is_case_insensitive_and_ignores_unknown_properties`：旧 Desktop 能解析新增字段的响应。

修改：`dotnet/CabinetNC.slnx`（`/src/` + `/tests/`）、`.github/workflows/regression.yml`、`windows-desktop.yml`（各加 `Cloud.Contracts tests` 步）。

### 3.2 决策记录（给 Task 6 / 7 / 9）

1. `NestJobResult` 是 **API 响应信封**（`GET …/result`），不是存进 MinIO 的对象格式。Task 7 存 `result.json` 时自行决定存什么（建议：不含 `ResultSha256` 的载荷），`ResultSha256` 从 Job 行读出来填进信封。Contracts 不约束存储格式。
2. `SubmitNestJobRequest` 不带 tenant / user / device / correlationId——全部来自 token 与 header（spec §7：不能信任 Desktop 传入的 TenantId）。`Idempotency-Key` 是 header（`ApiHeaders.IdempotencyKey`），不在 body。
3. Task 7 的输入校验（拒绝 0 零件、空/重复 PanelId、非正尺寸、负 spacing/border）应在 DTO → `NestingInput` 映射之前做，正好拦住 Task 2 §2.3 记录的“零宽零件被静默丢弃 / 重复 ID 走异常路径”两个 runner 既有行为。
4. `DurationMs` 用 `long`；`AttemptCount` 用 `int`；时间戳用 `DateTimeOffset`（序列化为 ISO-8601 带偏移，如 `2026-09-06T06:30:00+00:00`）。
5. 没有加 `HealthResponse` DTO：`GET /api/v1/health` 的正文由 Task 6 定义时再加进 Contracts。

### 3.3 验证

- 焦点：`dotnet test dotnet/tests/CabinetNC.Cloud.Contracts.Tests -c Release` → **16 / 0 / 0**。
- 全量：`dotnet test dotnet/CabinetNC.slnx -c Release` → **577 / 0 / 0**（Cloud.Contracts 16 + Compute.Core 7 + Package 40 + Domain 431 + Infrastructure 11 + Desktop.Core 72）。日志 `.handoff/local-evidence/task3-*.log`。
- Commit：`feat: add cloud api contracts`。

### 3.4 Task 3 Gate

- DTO 齐全（plan 要求的 7 组 + LogoutRequest / ApiErrorCodes / ApiRoutes / ApiHeaders / CloudJson），camelCase 与 canonical JSON 已被测试逐字节锁定，全量回归绿。**Gate 通过。**
- **下一步 Task 4 需要真实 PostgreSQL（Docker）。机器 B 目前未安装 Docker——这是继续前必须由人处理的环境前置。**

---

## Task 4 — PostgreSQL persistence + Job leasing（2026-09-06，机器 B）

### 4.1 做了什么

新建 `CabinetNC.Cloud.Infrastructure`（net10.0；引用 `Cloud.Contracts`；NuGet `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`）：

| 文件 | 内容 |
|---|---|
| `Entities/Entities.cs` | `TenantEntity` / `UserEntity` / `DeviceEntity` / `RefreshTokenEntity`（含 `FamilyId`，供 reuse 检测整族撤销）/ `ComputeJobEntity`（spec §8 全部字段 + `InputStoredAtUtc`）/ `AuditEventEntity`（`DetailsJson` 为 jsonb） |
| `CloudDbContext.cs` | 表名 `Tenants / Users / Devices / RefreshTokens / ComputeJobs / AuditEvents`（EF 默认 PascalCase，带引号）。唯一索引：`Users(TenantId, Email)`、`Devices(TenantId, DeviceKey)`、`ComputeJobs(TenantId, UserId, IdempotencyKey)`、`RefreshTokens(TokenHash)`。`Status` 以字符串存（`Queued/Running/Succeeded/Failed`）。领取扫描索引 `(Status, LockedUntilUtc, CreatedAtUtc)` |
| `Jobs/IJobRepository.cs` | `NewComputeJob` / `JobLease(Job, WorkerId, LockedUntilUtc)` / `JobCompletion`；接口 6 个方法：`CreateOrGetByIdempotencyKeyAsync`、`MarkInputStoredAsync`、`TryClaimNextAsync`、`MarkSucceededAsync`、`MarkFailedAsync`、`GetAsync` |
| `Jobs/PostgresJobRepository.cs` | 见 §4.2 |
| `ObjectKeys.cs` | `tenant/{tenantId}/jobs/{jobId}/input.json` / `result.json`（spec §10） |
| `ServiceCollectionExtensions.cs` | `services.AddCloudPersistence(connectionString)`：DbContext + `IJobRepository` + `TimeProvider.System` |

尚未生成 EF migration——按计划"Create initial migration after API startup project exists"，留给 Task 6；测试用 `EnsureCreatedAsync()`。

### 4.2 状态机与并发设计

每个状态迁移都是**一条带条件的 SQL**，两个 worker、或一个 worker 与自己的僵尸进程，不可能同时赢：

| 操作 | SQL 形态 | 条件 / 效果 |
|---|---|---|
| CreateOrGet | EF `INSERT`，捕获 `23505` 后按 `(TenantId, UserId, IdempotencyKey)` 查回 | `Id = Guid.CreateVersion7()`（时间有序），`Status=Queued`，`InputObjectKey` 由 `ObjectKeys.JobInput` 生成，`InputStoredAtUtc=NULL` |
| MarkInputStored | `UPDATE … SET InputStoredAtUtc = COALESCE(InputStoredAtUtc, now) WHERE Id=@id` | 幂等；只有存好输入的 Queued 才可被领取，worker 永远看不到半提交的 job |
| TryClaimNext ①收尸 | `UPDATE … SET Status=Failed, ErrorCode=compute_failed, ErrorMessage='lease expired after 3 attempts…' WHERE Status=Running AND LockedUntilUtc<now AND AttemptCount>=3` | 连续崩 3 次的 job 不再被重试 |
| TryClaimNext ②领取 | `UPDATE ComputeJobs j SET Status=Running, AttemptCount=j.AttemptCount+1, LockedBy=@w, LockedUntilUtc=@until, StartedAtUtc=COALESCE(StartedAtUtc,@now) FROM (SELECT Id … WHERE ((Queued AND InputStoredAtUtc IS NOT NULL) OR (Running AND LockedUntilUtc<@now)) AND AttemptCount<3 ORDER BY CreatedAtUtc, Id LIMIT 1 FOR UPDATE SKIP LOCKED) c WHERE j.Id=c.Id RETURNING j.Id` | 单语句原子；用 ADO.NET 直接执行（EF 的 `FromSql`/`SqlQuery` 会把语句包成子查询，而数据修改 CTE 不能在子查询里）；`command.Transaction` 挂到 `db.Database.CurrentTransaction`，调用方开了事务也能正确参与 |
| MarkSucceeded | `UPDATE … WHERE Id=@id AND Status=Running AND LockedBy=@worker` | `LockedBy` 就是 fencing token：租约被别人接管后，晚到的 worker 写不进结果（返回 false） |
| MarkFailed(retryable) | 先试 `… AND (NOT @retryable OR AttemptCount>=3)` → Failed；否则 `→ Queued, LockedBy=NULL` 立即可再领 | 返回 `Failed` / `Queued` / `null`（租约已丢） |

`TimeProvider` 注入，测试用 `ManualClock` 推进时间验证租约过期，不 sleep。

### 4.3 测试（先写、后实现）

`CabinetNC.Cloud.Infrastructure.Tests`：`Testcontainers.PostgreSql 4.14.0` 自动起 `postgres:17-alpine`；设了 `CABINETNC_TEST_PG` 则直接用该库。**没有可用 Docker 时用自定义 `[PostgresFact]` 显式 SKIP 并给出原因**（探测顺序：环境变量 → `DOCKER_HOST` / `\\.\pipe\docker_engine` / `/var/run/docker.sock` → `docker info --format {{.OSType}}` 必须是 `linux`），绝不假 PASS。

| 用例 | 对应计划要求 |
|---|---|
| `Duplicate_idempotency_key_returns_same_job` | duplicate idempotency key → same logical job（第二次用不同 payload hash，返回的是**原** hash，供 API 报 `idempotency_conflict`） |
| `Same_idempotency_key_across_tenants_creates_separate_jobs` | same key across tenants → allowed |
| `Two_simultaneous_workers_only_one_claims_the_job` | 8 个连接并发领同一 job，恰好 1 个拿到 |
| `Claim_skips_rows_locked_by_an_uncommitted_transaction` | worker A 在**未提交事务**里领走 job1；worker B 不阻塞、跳过 job1 领到 job2；再领为 null；A 提交后 job1 是 A 的。这是 `SKIP LOCKED` 的直接证明（阻塞实现会撞 15 s 超时） |
| `Expired_lease_is_reclaimable_and_the_old_worker_is_fenced_out` | expired lease → reclaimable（AttemptCount 2、LockedBy=B、StartedAtUtc 保留首次）；A 的 MarkSucceeded/MarkFailed 被拒；B 成功；成功不可重复 |
| `Third_retryable_failure_marks_job_failed` | third retry → Failed（1、2 次 → Queued，3 次 → Failed，CompletedAtUtc/ErrorCode 落库，之后不可领） |
| `Non_retryable_failure_fails_on_first_attempt` | 校验类失败立即 Failed |
| `Job_without_stored_input_is_not_claimable` | 未 MarkInputStored 不可领；Mark 幂等；不存在的 job 返回 false |
| `Worker_that_keeps_dying_is_failed_after_max_attempts` | 3 次租约过期无回报 → 第 4 次领取时被收尸为 Failed（compute_failed，"lease expired…"） |
| `Claims_are_served_oldest_first` | FIFO |
| `GetAsync_enforces_tenant_isolation` | 跨租户读 = 不存在 |
| `Schema_enforces_the_planned_unique_constraints` | 4 组唯一约束都抛 `23505`；同 email / device key 换租户允许 |

结果：**12 / 0 / 0**（真实 PostgreSQL 17，日志 `.handoff/local-evidence/task4-infra-tests.log`）；把 `DOCKER_HOST` 指向死端口复跑：**12 个全部 SKIP，0 通过 0 失败**，每条都带 "PostgreSQL unavailable: `docker info` failed…"（`task4-infra-tests-skip-when-no-docker.log`）。

全量 `dotnet test dotnet/CabinetNC.slnx -c Release`：**589 / 0 / 0**（Cloud.Infrastructure 12 + Cloud.Contracts 16 + Compute.Core 7 + Package 40 + Domain 431 + Infrastructure 11 + Desktop.Core 72）。

CI：`regression.yml`（ubuntu，有 Docker → 真跑）与 `windows-desktop.yml`（windows-latest 只有 Windows 容器 → 预期 SKIP，已在 step 名里注明）各加一步，用 `--verbosity normal` 让 skip 原因出现在日志里。

### 4.4 决策记录（给 Task 6 / 7）

1. Email 唯一索引大小写敏感——Task 6 存用户前必须 lower-case 归一化。
2. 没有配置外键：PoC 阶段以 Guid 引用为主，避免 EF 级联删除误伤审计；Task 6 若需要可加。
3. `CreateOrGetByIdempotencyKeyAsync` 的"捕获唯一冲突再查"路径在**调用方自己开的事务里**会让该事务进入 aborted 状态（PostgreSQL 语义）——API 提交 job 时不要包在外层事务里调它。
4. `AuditEventEntity` 只建了表；写审计的服务留给 Task 6（auth.*）/ Task 7（job.*）。
5. Task 7 的 worker 循环：`TryClaimNextAsync` → 跑 runner → `MarkSucceededAsync` 返回 false 时**丢弃结果并记 warning**（租约已被接管，结果对象可能已被别人写）。

### 4.5 Task 4 Gate

- 5 个计划必需用例 + 7 个补充用例全部在真实 PostgreSQL 上通过；无 Docker 时诚实 SKIP；全量 589 绿。**Gate 通过，可进入 Task 5（MinIO 对象存储）。**
- Commit：`feat: add cloud persistence and job leasing`。

---

## Task 5 — MinIO object store（2026-09-06，机器 B）

### 5.1 做了什么

在 `CabinetNC.Cloud.Infrastructure` 下新增 `Storage/`（NuGet `Minio 7.0.0`，官方 SDK，7.0.0 相对 6.0.5 仅修 bug、API 不变）：

| 类型 | 说明 |
|---|---|
| `IObjectStore` | spec §10 的三个方法：`PutAsync(key, Stream, contentType)` / `OpenReadAsync(key)` / `ExistsAsync(key)` |
| `ObjectStoreNotFoundException(Key)` | `OpenReadAsync` 找不到对象时抛；与 MinIO SDK 自己的 `ObjectNotFoundException` 区分命名 |
| `ObjectStoreOptions` | `Endpoint`（host:port，无 scheme）/ `AccessKey` / `SecretKey` / `Bucket`（默认 `cabinetnc`）/ `UseSsl` / `Region?` |
| `ObjectKeyRules.EnsureSafe(key)` | 拒绝：空/空白、长度 > 1024、前导 `/`、反斜杠、控制字符（< 0x20、0x7F）、任何 `.` 或 `..` 段、空段（`//`、结尾 `/`）。允许 `..input.json` 这类以点开头但不是 `..` 的段 |
| `MinioObjectStore` | 首次使用时建 bucket（双检 + `MakeBucket` 冲突后复查，兼容多个进程同时首启）；`Put` 对不可 seek 的流先缓冲到内存以取得长度；`OpenRead` 用 `GetObjectAsync` 回调流复制到 `MemoryStream` 后返回（payload 是 KB～MB 级 JSON，不是媒体文件，注释已写明这个取舍）；三个方法都**先** `EnsureSafe` 再碰网络 |
| `AddCloudObjectStore(options)` | 单例注册（MinIO client 线程安全、内部连接池） |

### 5.2 测试（先写、后实现）

`ObjectKeyRulesTests`（纯单元，21 个 case）：14 个非法 key 全部 `ArgumentException("key")`；超长；4 个合法 key；`ObjectKeys.JobInput/JobResult` 生成的 spec 布局 key 合法；以及 `Store_rejects_unsafe_keys_before_any_network_call`——把 store 指向 `127.0.0.1:1`（没人监听），非法 key 必须在校验阶段失败而不是连接失败。

`MinioObjectStoreTests`（`[MinioFact]`，`Testcontainers.Minio 4.14.0` 起 `minio/minio:latest`，每个测试类一个随机 bucket；或用 `CABINETNC_TEST_MINIO_ENDPOINT/_ACCESS_KEY/_SECRET_KEY` 指向现成 MinIO；都没有则 SKIP 并给出原因）：

| 用例 | 对应计划要求 |
|---|---|
| `Put_then_OpenRead_returns_the_exact_bytes_for_the_job_input_key` | **exact byte round trip** for `tenant/{tenantId}/jobs/{jobId}/input.json`：64 KB+17 字节随机内容嵌在 JSON 里，长度、字节序列、SHA-256 三重相等；写前 `Exists=false`、写后 `true` |
| `Missing_key_is_not_found` | `Exists=false`，`OpenRead` 抛 `ObjectStoreNotFoundException` 且 `Key` 正确 |
| `Put_overwrites_the_previous_object` | 同 key 二次写读到新内容 |
| `Non_seekable_streams_are_stored_completely` | 只能前向读的流也完整落盘 |
| `Two_stores_on_the_same_bucket_see_each_others_objects` | API 进程写、worker 进程读的形态 |

`DockerProbe` 从 Task 4 的 `PostgresAvailability` 里抽出来供 PostgreSQL / MinIO 两套 `[…Fact]` 共用。

结果：Infrastructure 套件 **38 / 0 / 0**（12 PostgreSQL + 5 MinIO + 21 key 规则，日志 `.handoff/local-evidence/task5-infra-tests.log`）；全量 `dotnet test dotnet/CabinetNC.slnx -c Release` **615 / 0 / 0**。CI 两个 workflow 的 step 名改为 "PostgreSQL + MinIO"。

### 5.3 决策记录（给 Task 7 / 8）

1. bucket 名默认 `cabinetnc`，Task 8 的 compose / `.env.example` 用 `CABINETNC_OBJECTSTORE_*` 之类的变量喂 `ObjectStoreOptions`；MinIO root 凭据不能进 Git。
2. `PutAsync` 不做"存在即跳过"——同 key 重写是允许的（幂等重提交时 API 会重新上传同一份 input，字节相同）。
3. `OpenReadAsync` 返回的是内存流，Task 7 的 result 端点直接把它写进 HTTP 响应即可；若将来要存大文件（NC/DXF/BMP，Task 11 之后），再把接口改为流式并加大小上限。
4. MinIO SDK 的 `GetObjectAsync` 在 bucket 不存在时抛 `BucketNotFoundException`——store 在每个操作前 `EnsureBucketAsync`，所以只会遇到对象级 404。

### 5.4 Task 5 Gate

- 接口、key 校验、MinIO 实现、逐字节 round-trip 集成测试全部完成并通过；无 Docker 时 SKIP。**Gate 通过，可进入 Task 6（Cloud API：health / correlation / bootstrap admin / JWT / rotating refresh / rate limit）。**
- Commit：`feat: add intranet object storage`。

---

## Task 6 — Cloud API shell, correlation, auth, dynamic Token

### 6.1 做了什么（2026-09-06，机器 B）

新建 `CabinetNC.Cloud.Api`（net10.0 Web）：

| 区域 | 实现 |
|---|---|
| 启动配置 | 生产 `CloudApiOptions.FromEnvironment()` **直接**用 `Environment.GetEnvironmentVariable`，不会从 appsettings/命令行读 secret；只读 `CABINETNC_DB_CONNECTION`、`CABINETNC_JWT_SIGNING_KEY`、三个 bootstrap 变量。DB/JWT 缺失或 signing key 不在 32–4096 bytes 直接拒绝启动，错误只报变量名、不回显值；测试通过 DI 替换显式注入 test-only options |
| 数据库 | 启动时 `Database.MigrateAsync()`；本 Task 用本地工具清单 `dotnet-tools.json`（`dotnet-ef 10.0.11`）生成 `InitialCloudPersistence` migration。`CloudDbContextFactory` 让 EF tool 只需 DB env、不需启动 API/JWT key。EF Core 显式统一到 10.0.11，避免 Design 私有依赖与 Npgsql 最低版本 10.0.4 在 API 项目里冲突 |
| Bootstrap | 三个 bootstrap 变量必须全有或全无；密码 12–1024 字符、tenant 1–200、email ≤320；**先完成全部配置校验，再迁移 schema**；tenant/email trim + lower-case；首启创建 Tenant + admin，ASP.NET `PasswordHasher<UserEntity>` 存 hash；已有用户不覆盖密码；三项全不设则不创建任何默认账号。Tenant name 加唯一索引，bootstrap transaction 先取 PostgreSQL advisory lock，两个 API replica 同时首启也只建一个账号 |
| Correlation | `CorrelationIdMiddleware`：只接受**单个标准 D 格式 UUID** 的 `X-Correlation-ID`，规范化为小写；多值、token/string/其他格式一律换成服务端 UUIDv7。这样 caller 不能把 password/refresh token 塞进 header 后借 `AuditEvents.CorrelationId` 明文落库；所有响应（含 204、401、429、500）都回 header，`ApiError.correlationId` 与之相同 |
| 错误 | `ApiExceptionMiddleware` 把已知问题、坏 JSON、未知异常统一成 `ApiError`；500 日志只记 exception type + correlationId，不记 exception message/连接串；Kestrel request body 上限 2 MiB（Task 7 仍需按 parts 数做业务上限）；Contracts 增加 `rate_limited` / `internal_error`（spec 12 个 minimum 仍保留） |
| JWT | HS256，issuer `cabinetnc-cloud`，audience `cabinetnc-desktop`，TTL 15 min，clock skew 60 s；claims：`sub / tenant_id / device_id / role / jti`；access token 只在响应里返回，不落 DB；401 保留标准 `WWW-Authenticate: Bearer`（过期为 `invalid_token`）；全局 fallback policy 默认要求认证，只有 health/login/refresh 显式 `AllowAnonymous` |
| Login | `POST /api/v1/auth/login`；request 的 tenant slug trim + lower-case 后查 DB，权威 TenantId 只来自匹配的 DB row；密码只用于 PasswordHasher 校验；未知 tenant/email 也跑一个 dummy PBKDF2 verify，避免明显的账号枚举时间差；tenant ≤200、email ≤320、password ≤1024、deviceName ≤200，DeviceId 必须 GUID；用 `INSERT ... ON CONFLICT (TenantId, DeviceKey) DO UPDATE ... WHERE existing.UserId=excluded.UserId` 原子 upsert，同 device 并发登录不重复、不同 user 不能抢；发一个新的 refresh family；成功/失败写 audit |
| Refresh | `POST /api/v1/auth/refresh`；48-byte CSPRNG → Base64Url，DB 只存 SHA-256 hex；事务先由 hash 查 family，再取 `pg_advisory_xact_lock(Int64(familyId))`，随后 `SELECT ... FOR UPDATE` 锁 token；每次成功 revoke old + 同 family 新 row；复用有 `ReplacedByTokenId` 的旧 token → revoke 同 family 全部 active token + `refresh_reuse_detected` |
| Logout | `POST /api/v1/auth/logout`（JWT 必需）；按 token claims + request device key 双重匹配后 revoke 整个 family；幂等返回 204；写 `auth.logout` audit |
| Rate limit | ASP.NET fixed-window，按 client IP：login 10/min、refresh 30/min、queue=0；429 返回 `rate_limited` + `Retry-After`。这是**单 API 进程内** limiter；Task 8 PoC 单 API replica 可用，多 replica 需 Redis/网关统一限流 |
| Token response cache | login/refresh 在执行业务前就写 `Cache-Control: no-store` + `Pragma: no-cache`，成功与错误 token endpoint response 都不可缓存 |
| Health | `GET /api/v1/health` → `HealthResponse(status/service/version/timestampUtc)`；liveness，不依赖 DB readiness |
| Logging | JSON console；业务代码不记录 request body、password、access/refresh token、signing key。验证日志搜索测试 password/signing key/known token 均无命中 |

`Cloud.Contracts` 同步新增 `HealthResponse`，`CloudJson` 的既有 canonical 配置不变。

### 6.2 数据与并发语义

1. Refresh rotate/reuse 在同一 PostgreSQL transaction 内按 family advisory lock 串行，再锁 hash 对应行。两个并发 refresh：第一个生成 successor 并提交；第二个醒来看到 old 已 revoked + `ReplacedByTokenId != null`，按 reuse 处理并撤销 successor，设备必须重新登录。family lock 也覆盖 logout，避免 old-token reuse 的 `UPDATE family` 与 successor refresh/insert 在 Read Committed 不同 statement snapshot 中错过新 token。
2. Logout-revoked token 没有 `ReplacedByTokenId`，再次 refresh 返回 `refresh_invalid`，不会误报被攻击；rotate-revoked token 才是 `refresh_reuse_detected`。
3. Audit details 只放 `{"reason":"<code>"}`，不放 email/password/token；未知 email/token 用 `TenantId=Guid.Empty`（模型当前无 FK，Task 4 的决策保持）。
4. `LoginRequest.Tenant` 是人可读 slug/name，只用于查找 tenant row；请求不能提交 TenantId。JWT 的 `tenant_id` 始终取自 DB。两 tenant 可有同 email，测试证明 `Tenant` 能正确选到各自 user。
5. API options 在 DI 中延迟解析；生产 factory 只读进程环境，`WebApplicationFactory` 则在 host build 时替换整个 options singleton，避免改全局环境变量造成测试串扰。
6. Task 8 加 reverse proxy 时，必须在 rate limiter 前配置受信任 proxy/network 的 Forwarded Headers；否则所有客户端会按 proxy 的 IP 共用 10/min。不能无条件信任任意来源的 `X-Forwarded-For`。
7. `InitialCloudPersistence` 假设目标是空库或已由 migration 管理的库；不要指向 Task 4 测试曾用 `EnsureCreated` 造出来、但没有 `__EFMigrationsHistory` 的临时库。
8. Task 6 只完成应用层 auth；**在 Task 8 的 reverse proxy + 内部 CA/TLS 完成前，不能把 login/refresh HTTP 端点直接暴露给内网客户端**，否则密码和 bearer token 会以明文经过网络。

### 6.3 测试

`CabinetNC.Cloud.Api.Tests` 通过 `WebApplicationFactory<Program>` 启动真实 HTTP pipeline，连接一个由 `Testcontainers.PostgreSql` 创建的**空 PostgreSQL 17**；API 自己执行 migration + bootstrap。26 个测试覆盖：

- health + 自动/回显 correlation id；
- bootstrap 只来自配置、密码不明文、二次启动不重复；三项全省略时零默认 tenant/user；两个 API replica 并发 bootstrap 仍只有一份 tenant/admin；
- login success / wrong password / unknown email / malformed device / same device not duplicated；同设备两个并发 login 都成功且仍只有一条 Device；同 email 在两个 tenant 时由 slug 精确隔离；
- access TTL ≈15m、HS256、5 个必需 claims；
- refresh rotate（old revoke、successor/family/link/30d expiry）；同一个 old token 两个并发 refresh 恰好 1 成功、1 reuse-detected；old reuse 与 successor refresh 并发后 family 无任何 active token；另一个测试先从独立 DB transaction 持有同一 advisory lock、观察 `pg_locks` 中未获锁 waiter、释放后 HTTP refresh 才完成，直接证明 API 确实等 family lock；
- rotated token reuse → family 全撤销；unknown / expired / foreign-device refresh；
- 手工制造 token/user/device tenant 不一致时 refresh 拒绝，不会把 Tenant-A refresh row 换成 Tenant-B JWT；
- logout revoke；匿名 / expired / malformed access token 的统一 401 code；
- raw refresh token 只有 64-char SHA-256 落库；用**真实 raw refresh token** 冒充 `X-Correlation-ID` 再触发 audit，服务端将它替换成 UUID；随后把六张表整行转 JSON 搜索，raw token 无命中；
- malformed JSON、未知 route 也返回统一 `ApiError` + correlation id；token response 有 no-store/no-cache，401 有 WWW-Authenticate；
- login 第 11 次 / refresh 第 31 次 → 429。
- 3 个纯 options 测试锁定：只读指定环境变量名、无 DB/JWT 默认值、弱 key/超大 key 拒绝且错误不回显 secret。

结果：

- `Cloud.Api.Tests`：**26 / 0 / 0**（23 个真实 PostgreSQL HTTP 集成 + 3 个 options 单元；`.handoff/local-evidence/task6-api-tests.log`）。
- 强制 `DOCKER_HOST=tcp://127.0.0.1:1`：3 个 options PASS、23 个 PostgreSQL用例 SKIP，证明 Windows CI/无 Docker 时不假 PASS。
- `Cloud.Contracts.Tests`：**17 / 0 / 0**。
- 全量 `dotnet test dotnet/CabinetNC.slnx -c Release`：**642 / 0 / 0**（API 26 + Cloud Infrastructure 38 + Contracts 17 + Compute Core 7 + 原有 554）。

`regression.yml`（Ubuntu + Docker）真跑 API + PostgreSQL；`windows-desktop.yml` 的 Windows container daemon 下预期显式 SKIP。

### 6.4 Task 6 Gate

- Auth 计划中的 9 个必需测试 + health/correlation/bootstrap/rate-limit/error-shape 补充测试全部通过；migration 从空库启动验证通过；无默认凭据；日志证据无已知 secret。**Gate 通过，可进入 Task 7（Nest Job API + Cloud Worker）。**
- Commit：`feat: add rotating cloud authentication`。

---

## Task 7 — Nest Job API + Cloud Worker

按计划拆成两个 commit：7A API（本节），7B Worker（下节）。

### 7A.1 做了什么（2026-09-06，机器 B）

`CabinetNC.Cloud.Api` 新增 `Jobs/`：

| 类型 | 说明 |
|---|---|
| `NestJobValidator` | API 边界校验，在任何对象落盘、任何 runner 代码之前：parts 1–1000（`MaxParts`，PoC 性能目标 500 留余量）、sheet 尺寸有限且 > 0、spacing/border 有限且 ≥ 0、`border*2 < sheet`、PanelId 非空/≤200/不重复、零件宽高 > 0、厚度 ≥ 0、material ≤ 200、所有数值 ≤ 100 000 mm 且非 NaN/Inf。这正好拦住 Task 2 §2.3 记录的两个 runner 既有行为（零宽零件被静默丢弃、重复 ID 走异常路径） |
| `NestJobService.SubmitAsync` | validate → `Idempotency-Key`（恰好一个、非空、≤200）→ 从 JWT claims 取 tenant/user/device（**不信任 body**）→ `CloudJson.Serialize(request)` 的 UTF-8 bytes 就是存储对象，`SHA-256` = `InputSha256` → `IJobRepository.CreateOrGetByIdempotencyKeyAsync` → 已有 job 的 `InputSha256` 不同 → **409 `idempotency_conflict`** → 若 `InputStoredAtUtc` 为空：`PutAsync(input.json)` → `MarkInputStoredAsync`（此后 worker 才能领取）→ audit `job.submitted` → **202** `SubmitNestJobResponse(jobId, Queued, correlationId)` |
| `NestJobService.GetStatusAsync` | `GetAsync(jobId, tenantId-from-token)` → 不属于本 tenant 与不存在同样是 **404 `job_not_found`** |
| `NestJobService.GetResultAsync` | 非 `Succeeded` → **409 `job_not_ready`**；读 `result.json` → 重算 SHA-256 与 `ResultSha256` 不一致 / 对象缺失 / JSON 坏 → **500 `storage_failed`**（不把可疑结果给 Desktop）；正常则把 payload + DB 里的 `EngineVersion/InputSha256/ResultSha256/DurationMs` 组成 `NestJobResult` |
| `NestJobEndpoints` | `POST /api/v1/jobs/nest`（body 上限 2 MiB）、`GET /api/v1/jobs/{jobId}`、`GET /api/v1/jobs/{jobId}/result`，全部 `RequireAuthorization` |

`Cloud.Contracts` 新增 `NestJobResultPayload(Engine, Placements, SheetCount, Unplaced, Warnings)`——这是**存进 MinIO 的对象**；`JobId/EngineVersion/InputSha256/ResultSha256/DurationMs` 留在 PostgreSQL、由 result 端点拼装，避免 `ResultSha256` 出现在被哈希的字节里（Task 3 §3.2 决策 1 的落地）。Canonical JSON 已被 Contracts 测试逐字节钉死。

`Cloud.Infrastructure` 配套：

- `ObjectStoreOptions.FromEnvironment()`：`CABINETNC_OBJECTSTORE_ENDPOINT / _ACCESS_KEY / _SECRET_KEY`（必填）、`_BUCKET`（默认 `cabinetnc`）、`_USE_SSL`（true/false）、`_REGION`；错误只报变量名。`AddCloudObjectStore(Func<IServiceProvider, ObjectStoreOptions>)` 延迟解析，API 启动时注册。
- `PostgresJobRepository.CreateOrGetByIdempotencyKeyAsync` 从 "EF INSERT + 捕获 23505" 改为 `INSERT ... ON CONFLICT ("TenantId","UserId","IdempotencyKey") DO NOTHING` + SELECT：重复提交是**正常路径**，不该在日志里留下 EF `Update` ERROR 堆栈；同时解决了 Task 4 §4.4 决策 3 的"调用方事务被 abort"问题。API 测试日志里 `23505` 出现次数：0。

### 7A.2 测试（先写、后实现）

`JobEndpointTests`（`[PostgresFact]`，真实 API + 真实 PostgreSQL，对象存储用 `TestObjectStore` 内存双件——可注入 put/read 失败）。第一次运行 8 个全部因端点不存在 404 而红；实现后全绿：

| 用例 | 对应计划要求 |
|---|---|
| `Submit_requires_authentication` | unauth submit → 401，且不产生 job 行 |
| `Submit_requires_one_nonempty_idempotency_key` | missing idempotency → 400（缺失、空白） |
| `Submit_rejects_invalid_or_oversized_requests` | 10 种非法输入（零件为空、空 ID、重复 ID、零宽、负厚度、sheet 0/负、负 spacing/border、1001 个零件）全部 400，且零 job 行 |
| `Submit_stores_canonical_input_hash_then_marks_the_job_claimable` | 202；DB 行的 tenant/user/device 来自 token；`InputSha256` = 客户端可独立复算的 canonical SHA-256；`input.json` 字节与之完全一致、content-type `application/json`；`InputStoredAtUtc` 已设；audit `job.submitted`；随后 `TryClaimNextAsync` 真能领到它 |
| `Duplicate_submit_returns_the_same_job_and_changed_payload_conflicts` | duplicate submit → same JobId；同 key 不同 payload → 409 `idempotency_conflict`；DB 只有 1 行 |
| `Status_returns_metadata_and_result_is_not_ready_while_queued` | status 200 + 元数据；result → 409 `job_not_ready` |
| `A_tenant_cannot_read_another_tenants_job` | Tenant B 的 JWT 读 Tenant A 的 job：status/result 都是 404 `job_not_found` |
| `Storage_failure_does_not_make_the_job_claimable` | 注入 `PutAsync` 失败 → 500 `storage_failed`；job 行存在但 `InputStoredAtUtc` 为空；worker 领不到 |

结果：`Cloud.Api.Tests` **34 / 0 / 0**（26 auth + 8 job）；`Cloud.Contracts.Tests` **18 / 0 / 0**；`Cloud.Infrastructure.Tests` **38 / 0 / 0**。

### 7A.3 决策记录（给 7B / Task 9 / Task 10）

1. 幂等重提交在 `PutAsync` 失败后可以**自愈**：job 行已存在但 `InputStoredAtUtc` 为空，Desktop 用同一 `Idempotency-Key` 重发，API 重新上传并标记。Task 9 的客户端重试应复用同一个 key。
2. `GET .../result` 每次都重算并比对 `ResultSha256`；这是 Task 10 "input/output hash" 验收的服务端一半。
3. 对 POST 只路由 GET 等方法不匹配时 ASP.NET 返回空 body 405，不是 `ApiError`；Desktop 客户端不会发这种请求，记为 Task 10 收尾项。
4. `TestObjectStore` 放在 `Cloud.Infrastructure.Tests` 供 API/Worker 测试共用；真实 MinIO 的字节 round-trip 由 Task 5 的集成测试覆盖。

### 7A.4 Commit

`feat: add nest job api`。

### 7B.1 做了什么（2026-09-06，机器 B）

新建 `CabinetNC.Cloud.Worker`（net10.0 console，`Microsoft.Extensions.Hosting 10.0.11`；引用 Contracts、Infrastructure、**Compute.Core**——这是云端唯一引用排版引擎的进程）：

| 类型 | 说明 |
|---|---|
| `NestJobExecutor.ExecuteOneAsync(ct)` | 计划要求的可测试单步：`TryClaimNextAsync` → 没有可领的返回 **false** → audit `job.claimed`、`job.started` → 只支持 `JobType == "nest"`（其他 → 终态 `invalid_request`）→ 读 `input.json` 并**重算 SHA-256 与 `InputSha256` 比对**（不一致 → 终态 `storage_failed`，不重试）→ `CloudJson` 反序列化（坏 JSON → 终态 `invalid_request`）→ `NestJobMapper.ToNestingInput` → `Stopwatch` 包住 `INestingRunner.Run`（只计算法时间，不含 I/O）→ runner `Ok=false` → 终态 `compute_failed`、`ErrorMessage` 用 runner 的 `Error`；runner 抛异常 → **可重试** `compute_failed`，`ErrorMessage` 只有异常类型名 → 成功：`NestJobResultPayload` canonical bytes → SHA-256 → `PutAsync(result.json)`（失败 → 可重试 `storage_failed`）→ **然后才** `MarkSucceededAsync(resultKey, resultSha, EngineVersion, durationMs)` → 返回 false（租约被别人接管）→ 丢弃结果只记 warning → audit `job.succeeded` → 返回 **true** |
| `FailAsync` | 统一走 `MarkFailedAsync(retryable)`；结果 `Queued` → audit `job.retry`，`Failed` → audit `job.failed`，`null`（租约丢失）→ 只记日志。存进 DB 的 `ErrorMessage` 永远不含堆栈或异常文本 |
| `NestJobMapper` | `SubmitNestJobRequest → NestingInput`、`NestingOutput → NestJobResultPayload`，逐字段，不引入默认值或取整 |
| `EngineVersion.Current` | `CabinetNC.Compute.Core/<AssemblyInformationalVersion>`；SDK 内建 SourceLink 会在仓库内构建时附加 git revision，所以每个结果都能追溯到具体的引擎构建 |
| `WorkerOptions` | `CABINETNC_WORKER_ID`（默认 `机器名:pid`，≤200）、`CABINETNC_WORKER_LEASE_SECONDS`（默认 300，10–3600）、`CABINETNC_WORKER_POLL_SECONDS`（默认 1，0.1–60）；错误只报变量名 |
| `NestJobWorkerHost` | `BackgroundService`：每轮一个 DI scope（一个 DbContext）→ `ExecuteOneAsync` → 有活立刻下一轮、没活 sleep `PollInterval`、异常（DB/MinIO 不可达）记类型名后 `ErrorBackoff` 5 s 再试，进程不退出 |
| `Program` | `Host.CreateApplicationBuilder` + JSON console；`CABINETNC_DB_CONNECTION` + `CABINETNC_OBJECTSTORE_*` 环境变量；**worker 不跑 migration**，schema 归 API |

### 7B.2 测试（先写、后实现）

`CabinetNC.Cloud.Worker.Tests`（`[PostgresFact]`，真实 PostgreSQL 队列 + `TestObjectStore` + **真实 `NestingRunner`**）9 个用例：

| 用例 | 对应计划要求 |
|---|---|
| `ExecuteOne_returns_false_when_nothing_is_claimable` | 空队列 → false |
| `ExecuteOne_runs_a_queued_synthetic_nest_and_records_result_hash_version_and_duration` | worker executes queued synthetic Nest；`Succeeded`、`LockedBy` 清空、`ResultObjectKey` = spec 布局、**result object/hash exists**（`ResultSha256` = 存储字节的 SHA-256）、**engine version/duration recorded**、payload 里 A/B 放置、BIG 未放；audit 顺序 `job.claimed → job.started → job.succeeded` |
| `Server_result_equals_running_the_shared_runner_directly` | Local/Server parity 的服务端一半：存进 MinIO 的 placements/warnings/unplaced/sheetCount 与直接调 `new NestingRunner().Run(...)` 完全相等 |
| `Storage_failure_never_marks_succeeded_and_exhausts_the_retry_budget` | **storage failure never marks Succeeded**：注入 `result.json` 写失败，3 次尝试分别 Queued/Queued/Failed，`ErrorCode=storage_failed`，`ResultSha256` 始终为空，对象不存在，之后不可领；audit 2×`job.retry` + 1×`job.failed`、无 `job.succeeded` |
| `Tampered_input_fails_immediately_without_retry_or_stack_trace` | 输入对象被改 → 一次即 Failed（`storage_failed`），消息无堆栈 |
| `Unexpected_runner_exception_is_retryable_and_recorded_without_details` | runner 抛带路径的异常 → Queued（可重试）、`compute_failed`、消息只含异常类型名、不含 `secret`/堆栈；audit `job.retry` |
| `Runner_reported_failure_is_final_and_keeps_the_runner_message` | runner `Ok=false` → 终态 Failed，`ErrorMessage` = runner 的 `Error` |
| `Losing_the_lease_mid_run_discards_the_completion` | 慢 worker 的 runner 回调里把时钟推过租约并让另一个 worker 重新领取 → 慢 worker 的 `MarkSucceeded` 被 fencing 拒绝：job 仍是 `other-worker` 的 Running、`AttemptCount=2`、`ResultSha256` 空、无 `job.succeeded` |
| `Unsupported_job_type_fails_without_retry` | `JobType="post"` → 终态 `invalid_request`（Task 11 之前不接 CAM/Post） |

`Cloud.Api.Tests` 追加 3 个**端到端**用例（HTTP → DB → worker → HTTP）：

- `Submit_execute_and_fetch_result_end_to_end`：`POST /jobs/nest` → `ExecuteOneAsync` → `GET /jobs/{id}` 为 Succeeded（attempt 1、duration、completedAt）→ `GET /jobs/{id}/result` 200：`JobId/Engine/EngineVersion/InputSha256/DurationMs` 与 DB 一致，`ResultSha256` = 对象存储里字节的 SHA-256，placements 与直接跑 runner 完全一致；audit `job.submitted → job.claimed → job.started → job.succeeded`。
- `Result_endpoint_refuses_a_stored_result_whose_hash_no_longer_matches`：翻转 `result.json` 一个位 → 500 `storage_failed`，不把可疑结果交给 Desktop。
- `Failed_job_reports_its_error_code_in_status_and_has_no_result`：3 次存储失败后 status 为 Failed + `storage_failed` + 可读消息、result 为 409 `job_not_ready`。

`Worker.Program` 改为显式 `namespace CabinetNC.Cloud.Worker; public static class Program`，避免与 API 的顶级语句 `Program` 在同一测试程序集里冲突。

结果：`Cloud.Worker.Tests` **9 / 0 / 0**；`Cloud.Api.Tests` **37 / 0 / 0**（26 auth + 11 job）；日志 `.handoff/local-evidence/task7-*.log`。

### 7B.3 决策记录（给 Task 8 / 9 / 10）

1. `DurationMs` 只计 `INestingRunner.Run` 的时间——这是 Task 10 性能表里"服务器计算耗时"的定义；端到端延迟由 Desktop 端另计。
2. PoC 没有租约心跳/续期：一次 Nest 必须在 `LeaseDuration`（默认 5 min）内完成。500 panels 的 BLF 远低于此；若 Task 10 实测接近上限，加续期而不是把租约拉长。
3. 同一 job 的 `result.json` key 是确定的；租约丢失时慢 worker 写下的对象会被接管者用**相同字节**覆盖（同一 runner、同一输入、确定性算法），所以丢弃完成不会留下脏数据。
4. Worker 的 `ErrorMessage` 只含类型名或 runner 自己的 `Error` 字符串；API `GET /jobs/{id}` 直接透传给 Desktop，所以这里就是"public error never returns stack trace" 的执行点。
5. Task 8 compose：worker 与 API 共用 `CABINETNC_DB_CONNECTION`、`CABINETNC_OBJECTSTORE_*`；多 worker replica 只需不同 `CABINETNC_WORKER_ID`（默认值已含 pid，但容器里 pid 都是 1，**compose 必须显式设置**）。
6. Worker 启动不检查 schema；API 未先启动时第一轮 `TryClaimNextAsync` 会因表不存在抛异常 → 5 s backoff 重试，不崩。

### 7B.4 Task 7 Gate

- API 8 个 + 端到端 3 个 + Worker 9 个用例全部在真实 PostgreSQL 上通过；计划列出的 8 个 required tests（unauth 401 / missing idempotency 400 / duplicate same JobId / Tenant A 不能读 B / worker 执行合成 Nest / result object+hash / engine version+duration / storage failure never Succeeded）逐条有对应用例。**Gate 通过，可进入 Task 8（Docker Compose 内网栈）。**
- Commits：`feat: add nest job api`（7A）、`feat: add cloud nest worker`（7B）。

---

## Task 8 — Docker Compose intranet stack（2026-09-06，机器 B）

### 8.1 做了什么

| 文件 | 内容 |
|---|---|
| `dotnet/src/CabinetNC.Cloud.Api/Dockerfile` | 多阶段：`sdk:10.0` 先只拷 3 个 csproj 做 `restore`（层缓存），再拷源码 `publish`；运行阶段 `aspnet:10.0`，`USER $APP_UID`（非 root），`ASPNETCORE_HTTP_PORTS=8080`；`ARG SOURCE_REVISION` → `-p:SourceRevisionId` 让 InformationalVersion 带 git 版本。**不含 Compute.Core/Domain** |
| `dotnet/src/CabinetNC.Cloud.Worker/Dockerfile` | 同上，运行阶段 `runtime:10.0`（无 ASP.NET）；这是唯一带排版引擎的云端镜像 |
| 两个 Dockerfile 运行阶段 | `apt-get install libgssapi-krb5-2`：否则 Npgsql 启动时向 stderr 打 `libgssapi_krb5.so.2: cannot open shared object file`（无害但像错误） |
| `.dockerignore`（仓库根） | 构建上下文是仓库根；排除 bin/obj、`.git`、`.handoff`、Desktop/ComputeWorker/测试/前端等与云端镜像无关的目录，以及 `deploy/intranet/.env*`（只放行 `.env.example`） |
| `deploy/intranet/docker-compose.yml` | 5 个服务、2 个网络、4 个卷。`edge`（proxy↔api）固定网段 `172.28.100.0/24`；`backend`（api/worker↔postgres/minio）`internal: true`（无路由出主机）。只有 proxy 发布 443/80。所有服务有 healthcheck，`depends_on: condition: service_healthy` 串出 postgres/minio → api → worker/proxy 的启动顺序。API 的 healthcheck 用 bash `/dev/tcp` 探 `/api/v1/health`（aspnet 镜像没有 curl）；worker 探 PID 1 命令行；Caddy 探本机 admin API（不需要绕 TLS）。所有 secret 用 `${VAR:?...}` 强制来自 `.env` |
| `deploy/intranet/Caddyfile` | `{$CABINETNC_PUBLIC_HOST:localhost}` 站点，`tls internal`（切换到车间 CA 只改一行）、HSTS、`-Server`、`nosniff`、body 4 MB、`reverse_proxy cabinetnc-api:8080`；`admin 127.0.0.1:2019`、`skip_install_trust` |
| `deploy/intranet/.env.example` | 只有占位符（`CHANGE_ME_*`）与说明；JWT 占位符故意短于 32 bytes，不替换 API 就拒绝启动 |
| `.gitignore` | 加 `!deploy/intranet/.env.example`（交接文档预告过的坑） |
| `Cloud.Api/Http/TrustedProxies.cs` + `Program.cs` | `CABINETNC_TRUSTED_PROXY_CIDRS`（逗号分隔 CIDR/IP）→ `ForwardedHeadersOptions.KnownIPNetworks`，`UseForwardedHeaders` 放在 rate limiter 之前，只信任 proxy 网段的 `X-Forwarded-For`；未设置时不启用。3 个单元测试（空 → 不信任任何人；CIDR/单 IP/IPv6 解析；垃圾值报错不回显） |
| `Program.cs`（API/Worker） | 日志过滤：`Microsoft.EntityFrameworkCore.Database.Command` → Warning（worker 每秒轮询会把 SQL 刷屏）；API 另把 `Microsoft.AspNetCore.DataProtection` → Error（API 无状态、从不使用 Data Protection，其"密钥不持久化"警告是噪音，注释已写明理由） |

### 8.2 机器 B 实测（`.handoff/local-evidence/task8-*`）

- `docker compose up -d --build`：**153 s**（含两次镜像编译），5/5 healthy；改 Dockerfile 后重建 api+worker 约 100 s（build 阶段缓存命中）。
- 端口：`docker compose ps` 只有 reverse-proxy 发布 `0.0.0.0:80/443`；主机 `Test-NetConnection` 5432/9000/9001/8080 全部 False。
- TLS：导出 Caddy 内部根证书（`CN=Caddy Local Authority - 2026 ECC Root`，2026-09-06 → 2036-07-15）；`curl --cacert root.crt --ssl-revoke-best-effort https://localhost/api/v1/health` → **200**，TLS 握手 22 ms，首个请求 47 ms，之后 10 次平均 12 ms；响应带 `Strict-Transport-Security`、`X-Correlation-Id`、无 `Server`。**不带 CA** → curl exit 60 `SEC_E_UNTRUSTED_ROOT`（校验确实在）。`http://` → **308** 跳 https。
- Windows 特有：schannel 对无 CRL 的内部 CA 报 `CERT_TRUST_REVOCATION_STATUS_UNKNOWN`，需 `--ssl-revoke-best-effort`（只跳过吊销查询，不关校验）；已写进 runbook。
- 经 proxy 的功能冒烟（`task8-smoke.ps1`，密码经环境变量传入、输出只有状态码/ID/哈希前缀）：login 200（admin、900 s / 2 592 000 s）→ 错密码 401 `invalid_credentials` → 无 token 提交 401 `unauthorized` → 提交 202 → 同 key 重提交 202 同 jobId → worker 领取并完成：**Succeeded，durationMs=40–42，wall ≈1.3 s**（含 250 ms 轮询）→ result 200：`engine=grouped_blf_v0`，**`engineVersion=CabinetNC.Compute.Core/1.0.0+ffb5bd52…`**（SOURCE_REVISION 生效），A/B 放置、BIG 未放，`inputSha256`/`resultSha256` 两次运行**完全相同**（确定性）→ 不存在的 job 404 `job_not_found` → logout 204 → 用已 logout 的 refresh 再刷 401 `refresh_invalid`。
- 重建 api/worker 镜像并 `up -d`（postgres/minio/proxy 不动）后：API 在已有库上启动，migration no-op、bootstrap 跳过；重跑冒烟全部通过——这就是 Task 10 "API restart" 的一次实际演练。
- 日志：修复后 api+worker 2 分钟内 0 条 stderr、只有 19 条 Information，worker 只在领取/完成时各记一行。首次空库启动仍有 1 条预期的 EF `Failed executing DbCommand ... __EFMigrationsHistory`（EF 探测迁移表的既有行为），runbook 已说明。
- 验证结束后 `docker compose down -v` 清场，本地 `.env` 删除；镜像保留。
- 顺带修了一个在高并发 Docker 负载下暴露的启动竞态：MinIO 在 healthcheck 通过后仍有一小段窗口对 S3 请求回 503 空 body，MinIO SDK 的错误解析器对空 body 抛 `NullReferenceException`（`MinioObjectStoreTests.Missing_key_is_not_found` 在全量回归里失败 1 次）。`MinioObjectStore.EnsureBucketAsync` 的首次 bucket 探测现在最多重试 6 次（250 ms 指数退避，只针对 `MinioException` / `NullReferenceException` / `HttpRequestException`）；凭据错误等真实问题在最后一次之后照常抛出。改后连续 3 次 Infrastructure 套件 + 1 次全量（**666 / 0 / 0**）在 compose 栈同时运行的负载下全绿。

### 8.3 决策记录 / 待办

1. **第二台 LAN 机器的 HTTPS 验证未做**（机器 B 只有一台机器）：runbook §4.5 给了命令与要记录的字段，运维执行后补到本节。
2. Caddy 内部 CA 是 PoC 默认；正式部署建议用车间 CA 签发证书（Caddyfile 一行切换），否则每台 Desktop 都要装 Caddy 的根证书。
3. Worker 多副本靠不同 `CABINETNC_WORKER_ID`；compose 里只定义了一个，runbook 给了 `docker compose run -d -e CABINETNC_WORKER_ID=worker-2 ...` 的临时加法。
4. 限流现在按真实客户端 IP（proxy 网段可信）；若 proxy 网段变更必须同步 `CABINETNC_TRUSTED_PROXY_CIDRS`。
5. 备份/恢复演练（pg_dump + MinIO 卷）留给 Task 10 的 reliability 套件。

### 8.4 Task 8 Gate

- 5 服务 healthy、持久卷、DB/MinIO 不暴露、HTTPS 经 proxy、CA 信任有文档且不关校验、`.env.example` 只有占位符——计划的 7 条要求逐条满足；经 proxy 的登录→提交→worker→结果全链路实测通过。**Gate 通过（第二台机器的验证记为运维 TODO），可进入 Task 9（Desktop 登录 / DPAPI / 远程 Nest gateway）。**
- Commit：`build: add intranet cloud compose stack`。

---

## Task 9 — Desktop login, DPAPI, remote Nest gateway

拆成两个 commit：9A 无 WPF 的客户端内核（`CabinetNC.Desktop.Core/Cloud/`，Linux CI 可测），9B WPF 接线。

### 9A.1 做了什么（2026-09-06，机器 B）

`Desktop.Core` 新增引用 `Cloud.Contracts`（纯 DTO）与 `Compute.Contracts`（本机 gRPC 客户端面）以及 `System.Security.Cryptography.ProtectedData 10.0.11`；`NoWpfDependencyTests` 仍通过（没有 WPF 泄漏）。

| 类型 | 说明 |
|---|---|
| `ComputeMode` | `Local` / `Intranet`，由操作员显式选择 |
| `CloudClientOptions` | spec §13 的数字：connect 5 s、request 15 s、poll 1 s→2 s→…≤5 s、`JobTimeout` 10 min、`NetworkGrace` 60 s、`RefreshLeadTime` 60 s；`BaseAddress` + tenant slug |
| `CloudSettingsStore` | `%LocalAppData%\CabinetNC\cloud.json`：mode / serverUrl / tenant / email，**无 secret**；坏文件回默认（Local） |
| `DeviceIdentityStore` | `device-id` 文件里一个首次使用时随机生成的 GUID；**不从硬件推导**，重装即变；坏文件重生成 |
| `ITokenStore` / `WindowsTokenStore` | 只持久化 **refresh token + 所属 device/tenant/email/过期时间**；DPAPI `CurrentUser` + 固定 entropy（防止把别的 CabinetNC blob 拿来重放）；解密失败/被改动 → 视为不存在；access token 只在内存 |
| `AuthSession` | `LoginAsync`（deviceId 来自 store、deviceName = 机器名）、`TryRestoreAsync`（启动时从磁盘恢复：校验 device/tenant/过期 → 立刻 refresh 拿 access token；离线时保留待重试）、`GetAccessTokenAsync`（剩余 < 60 s 主动 refresh）、`ForceRefreshAsync(rejectedToken)`（401 后；若已有人换过 token 则不再刷）、`LogoutAsync`（服务端撤销失败也本地清空）。所有 refresh 走一个 `SemaphoreSlim` **single-flight**；refresh 被 401 → 清 session + 清盘 → `CloudAuthenticationRequiredException`；网络错误 → 保留 session → `ComputeUnavailableException` |
| `AuthenticatedHttpHandler` | 附 bearer；401 且错误码是 `token_expired`/`unauthorized` → `ForceRefreshAsync` 一次 → **重放一次**（body 已缓冲）；第二次 401 直接抛，不循环 |
| `CloudApiClient` | health / login / refresh / logout 匿名；jobs 走 handler；`CloudJson` 序列化；非 2xx → `CloudApiException(status, ApiError)`；`HttpRequestException`/超时 → `ComputeUnavailableException`。TLS 校验是平台默认，**没有任何绕过开关**（内网根证书装进 Windows 证书库，见 runbook §4.3） |
| `IComputeGateway` / `IntranetComputeGateway` | `submit → JobId → poll → result`；每次运行新 `Idempotency-Key`；轮询间隔 1,2,4,5,5…；传输失败在 `NetworkGrace` 内重试（submit 复用同一 key 所以不会造出第二个 job）；`Failed` → `ComputeJobFailedException(code,message)`；超时 → `ComputeJobTimeoutException`；未登录 → `CloudAuthenticationRequiredException`，**不回退本机** |
| `LocalComputeGateway` | 同一契约走本机 gRPC worker（A/B 用），像服务端一样算 `InputSha256`/`ResultSha256`，`EngineVersion = local-grpc/<worker version>` |
| `ComputeGatewayFactory` | `Create(mode)`；Intranet 未登录直接抛，没有静默回退 |
| `NestRequestBuilder` | **矩形契约降级**：AABB（`sizeOf`）、`settings.PanelMayRotate90(panel)`（与本机引擎相同的纹理锁规则）、只用第一种大板、统一边距；并列出每一项降级：`true_shape_to_aabb`（N 件）、`extra_sheets_ignored`、`keepouts_ignored`、`insets_ignored`、`parts_in_part_disabled`、`default_sheet`——UI 要显示，验收文档要写 |
| `NestResultMapper` | `NestJobResult → (NestResult, NestEngineRunLog)`，`SheetsUsed` = 主大板 × SheetCount，SheetCount 不低于最高使用的 sheet index+1；`AttemptedEngine = "intranet"` |

### 9A.2 测试（先写、后实现；`Desktop.Core.Tests` 72 → **117**）

`FakeCloudApiHandler`：内存版 API，忠实于线上契约（rotating refresh + reuse detection、access token 过期、幂等提交、每个 job 一条状态序列、可注入断网次数/过滤器、可拒绝 refresh）。

| 组 | 用例 |
|---|---|
| Stores（10） | device-id 生成/稳定/两台不同/坏文件重生成；cloud.json 默认/round-trip 无 secret/坏文件回默认；DPAPI round-trip 且磁盘上无明文、Clear 删文件、改动 blob 视为不存在、无文件为 null（DPAPI 3 个用 `[WindowsFact]`，非 Windows 跳过） |
| AuthSession（11） | 登录只把 refresh 落盘；错密码 → `invalid_credentials` 且未登录；839 s 时不刷、841 s 时刷一次并落盘新 refresh；12 个并发只刷 1 次；`ForceRefresh` 旧 token 不重复刷；refresh 被拒 → 登出 + 清盘；refresh 断网 → 保留 session，之后成功；从磁盘恢复 → 立刻 refresh、身份恢复、不重新 login；无存储/死 token 恢复失败且干净；别的 device 的 token 不用、不发请求；logout → 服务端 1 次 + 本地清空 + `Changed` |
| HTTP 管线（7） | 带 token；未登录不碰网络；服务端过期 → 1 次 refresh + 重放 1 次；8 个并发过期请求共用 1 次 refresh；第二次 401 不再重试且登出；非 auth 错误带 code/correlationId；health 匿名 |
| Gateway（9） | 状态序列 Q,Q,R,R,R,S → 间隔 **[1,2,4,5,5]** s、结果正确、progress 覆盖 Q/R/S；两次运行两个 key、两个 job；Failed → code+message；30 s 超时不挂死、间隔 1–5 s；轮询断 3 次仍拿到结果；submit 断 2 次同 key 重试只造 1 个 job；持续断网到 grace 后抛 `ComputeUnavailableException`；取消立即停；未登录拒绝运行 |
| 契约（8） | 矩形 1:1 无降级；纹理锁逐件；L 形 → AABB + 降级"2 件"；多大板/禁排区/按边余量/parts-in-part 全部披露；无大板 → 默认 + 披露；混合材料披露；结果映射；SheetCount 不低于最高 sheet |

### 9A.3 Commit

`feat: add desktop intranet client core`。

### 9B.1 做了什么（2026-09-06，机器 B）

WPF 侧全部放在新文件里，`MainWindow.xaml.cs` 只改了 `RunNestAsync` 的计算块和两条 catch，没有重写：

| 文件 | 内容 |
|---|---|
| `CloudLoginWindow.xaml(.cs)` | 服务器 / 租户 / 邮箱 / 密码（`PasswordBox`）；`CloudClientOptions.TryParseServerUrl` 校验（**https 任意主机；http 只允许 127.0.0.1/localhost**——凭据不走明文 LAN）；用新建的 `CloudApiClient` 登录成功后把 client 交回主窗口；错误码翻译成人话（`invalid_credentials`/`rate_limited`/连不上 → 提示检查地址、网络、根证书）；密码不离开该窗口 |
| `MainWindow.Cloud.cs`（partial） | `InitializeCloud()`：读 `cloud.json`（目录 = library.json 所在目录，所以 UI smoke 的私有库目录也隔离了 cloud 状态）→ 设置下拉 → 有 server+tenant 就建 client；`RestoreCloudSessionAsync()`（Loaded 时用 DPAPI 里的 refresh token 恢复登录）；`OnComputeModeChanged`（持久化 + 提示未登录）；`OnCloudLoginClick`（登录 / 退出二合一按钮；退出 = 服务端撤销 + 本地清盘）；`UpdateCloudUi()`（`CloudStatusText` + 顶部 `WorkerBadge` 在内网模式下描述服务器会话）；`RunIntranetNestAsync()`：`NestRequestBuilder.Build` → `ComputeGatewayFactory.Create(Intranet)`（未登录直接抛）→ 进度写状态栏（排队中/计算中 + 秒数 + job 短 id）→ `NestResultMapper.ToLocal` → 附加 3 类警告：`intranet`（job id、EngineVersion、服务器耗时、input/result 哈希前缀）、`intranet_contract`（每一条矩形契约降级）、服务器返回的 `aabb_gap` 等 |
| `MainWindow.xaml` | 密排面板「排版方式」下新增「计算位置」下拉（`ComputeModeCombo`：本机计算 / 内网计算）+ `内网登录…` 按钮 + `CloudStatusText` |
| `MainWindow.xaml.cs` | ① 记录 `computeMode`；② 计算块：`Intranet` → `RunIntranetNestAsync`，否则原样 `NestEngineRouter`（**Local 路径一行未改**）；③ `_nest.Warnings.AddRange(intranetWarnings)`；④ 完成状态尾部加 ` · 内网 job xxxxxxxx`；⑤ 新增 catch：`CloudAuthenticationRequiredException` → 错误状态 + toast「内网登录…」；`ComputeUnavailable/JobFailed/JobTimeout/CloudApi` → `内网计算失败 [code]: … · 未自动改用本机`；⑥ `UsageLog` 的 nest.run 增加 `computeMode / cloudJobId / engineVersion / engineMs / cloudError`；⑦ `RefreshWorkerAsync` 在内网模式下不再探测本机 worker、不覆盖徽标（自检菜单改为报告内网会话） |
| `ui-smoke.ps1` | 新动词 `type:AutomationId=%ENV%`（ValuePattern，密码经环境变量传入，不进脚本文件）、`select:ComboId=项文本`；`Find-ByName/Find-ById` 找不到时退回**按进程**全局查找（登录对话框不是主窗口的后代） |
| `scenarios/06-intranet-nest.txt` | 打开示例 → 3 密排 → 未登录 → 内网登录…（填 4 项）→ 登录 → 状态"已登录内网" → 选「内网计算」→ 重新密排 → 状态含"密排完成"与"内网 job" → 退出内网登录 → "未登录" |
| `run-all.ps1` | 文件名含 `intranet` 的场景在没有 `CABINETNC_SMOKE_API_URL` 时**跳过并计入 `skipped`**（Windows CI 无 Docker），不假装通过 |
| `deploy/intranet/docker-compose.smoke.yml` | 仅开发/冒烟用的叠加：把 API 额外发布到 `127.0.0.1:8080`，Desktop 走 loopback http 不必往用户证书库装 Caddy 根证书 |

### 9B.2 实测（`.handoff/local-evidence/task9b-*`）

- 本机 UI smoke（无 API）：**5/5 通过，1 跳过**（06 明确报 skipped）。
- 内网 UI smoke：`docker compose -f docker-compose.yml -f docker-compose.smoke.yml up`，设置 4 个 `CABINETNC_SMOKE_*` 环境变量后 `run-all.ps1`：**6/6 通过**（`task9b-ui-smoke-all.log`，截图 `06a/06b/06c`）。06c 截图：计算位置=内网计算、`已登录 admin@example.internal`、板件按服务器结果落位、状态栏 `密排完成 · 已排 1 件 · 1 张大板 · 未排 0 · 校验通过 · 内网 job 01a0763a`、右上徽标 `计算引擎 · 内网 · admin@example.internal`。
- 服务端核对（`task9b-audit-query.txt`）：两个 job 均 `Succeeded`，`EngineVersion = CabinetNC.Compute.Core/1.0.0+cd933b1…`，两次 **input/result 哈希完全相同**（确定性）；审计链 `job.submitted → job.claimed → job.started → job.succeeded` 带用户邮箱与设备名 `ALEX`；两次 smoke 各注册了一个不同的 device id（私有目录 → 新 GUID，证明隔离）；登出后 refresh token **2/2 已撤销**。这就是验收 Gate E"只凭 JobId 可查"的 Desktop 端实证。
- 第一次运行暴露的两个问题已修：登录控件在密排面板而非板材页（场景改为 `tab:3 密排`）；`RefreshWorkerAsync` 在内网模式下会把徽标改回本机 worker（已让它转交 `UpdateCloudUi`）。

### 9B.3 决策记录 / 待办

1. **矩形契约在 UI 里是显式的**：每次内网排版都在「未排 / 警告」列出 `intranet_contract` 条目（异形→外接矩形、只用第一种大板、忽略禁排区/按边余量、无 parts-in-part）。验收文档（Task 13）要原文引用这份清单。
2. 内网模式失败**不回退本机**：状态栏与日志都写"未自动改用本机"，`cloudError` 记录错误码；操作员手动切回「本机计算」即可。
3. Desktop 信任内网 CA 的方式是 Windows 证书库（runbook §4.3），代码里没有任何跳过校验的开关；`http://` 只在 loopback 可用，用于本机开发与 smoke。
4. `cloud.json` 与 `cloud.token`（DPAPI）与 `device-id` 都在 library.json 同目录；`OMNICAM_LIBRARY_PATH` 重定向时一并重定向。
5. Task 12 客户版：Desktop 仍引用 `Compute.Contracts`（gRPC 面）与 `CabinetNC.Domain`（本机 NFP）；去掉本机计算要在 Task 12 处理引用与 `ComputeModeCombo` 的可见性。

### 9B.4 Task 9 Gate

- 登录 / DPAPI / 显式模式 / 无静默回退 / submit→poll→result / UI smoke（本机不退化 + 内网通过）——计划要求逐条满足。**Gate 通过，可进入 Task 10（诊断、性能、可靠性套件）。**
- Commits：`feat: add desktop intranet client core`（9A）、`feat: add desktop intranet compute mode`（9B）。

---

## Task 10 — Diagnostics, performance, reliability（2026-09-06，机器 B）

### 10.1 诊断端点

`GET /api/v1/admin/jobs/{jobId}/diagnostics`（`Jobs/AdminEndpoints.cs`，`RequireAuthorization(RequireRole("admin"))`，**tenant 作用域**来自 JWT）。返回 `JobDiagnosticsResponse`：JobId、tenant（id+name）、user（id+email）、device（id+key+name）、jobType、状态、correlationId、idempotencyKey、input/result object key 与 SHA-256、EngineVersion、attemptCount、lockedBy/lockedUntil、created/inputStored/started/completed、durationMs、errorCode/errorMessage、全部 `AuditEvents`（id、类型、时间、correlationId、detailsJson）。响应逐字段拼装，**任何实体都不直接序列化**，所以 `PasswordHash`/`TokenHash` 不可能出现。

`AdminDiagnosticsTests`（5）：admin 从 JobId 拿到全部字段与 `job.submitted→claimed→started→succeeded` 审计链，且响应 JSON 里没有 admin 的密码哈希、任何 refresh token 哈希、access/refresh token 原文、也没有 `passwordHash`/`tokenHash` 字样；operator → **403** `unauthorized`；别的 tenant 的 admin → **404** `job_not_found`；不存在的 job 404、无 token 401；Running 时暴露 `lockedBy/lockedUntil`，Failed 时暴露 `errorCode/errorMessage`。

### 10.2 失败套件（`ReliabilitySuiteTests`，7 个，全部用**真实 Desktop 客户端**驱动真实 API + PostgreSQL + worker 执行体；只有对象存储是可注入故障的内存双件）

| 计划场景 | 用例 | 关键断言 |
|---|---|---|
| duplicate submission → one job | `Duplicate_submission_yields_exactly_one_job` | 同 key 顺序 2 次 + 并发 6 次 → 同 JobId，`ComputeJobs` 1 行 |
| worker crash after claim → lease recovery | `Worker_crash_after_claim_is_recovered_through_lease_expiry` | 第 1 次轮询：`crashed-worker` 领取后消失；第 2 次：时钟 +6 min；第 3 次：健康 worker 重新领取（`AttemptCount = 2`）并完成；Desktop 的同一个 `RunNestingAsync` 调用拿到结果；审计只有健康 worker 的 claimed/started/succeeded（死掉的 worker 什么都没来得及写——这正是"崩溃"的含义） |
| API restart → job persists | `Api_restart_keeps_jobs_and_sessions` | 提交后销毁整个 API 宿主，在同一库上新建；新客户端用 DPAPI 里的 refresh token `TryRestoreAsync` 成功；job 仍 Queued；worker 完成后可取结果 |
| MinIO unavailable → not Succeeded | `Object_store_outage_never_produces_a_success` | 输入落盘失败 → 提交 500 `storage_failed`、不可领取、恢复后同 key 自愈；结果落盘失败 → 3 次尝试后 `Failed/storage_failed`，`ResultSha256` 空，库里 0 个 Succeeded |
| token expiry during polling → auto refresh | `Access_token_expiry_during_polling_is_refreshed_transparently` | 轮询中时钟 +16 min；API 用 **`LifetimeValidator` 按注入的 `TimeProvider`** 判过期 → 401 `token_expired` → 客户端刷新一次并重放 → job 完成；库里 2 个 refresh token、1 个已撤销 |
| cross-tenant read → blocked | `Cross_tenant_reads_are_blocked_for_the_client_too` | tenant-b 的 operator 用自己的密码登录后读 tenant-a 的 job：status/result 都是 404 `job_not_found` |
| client disconnect after JobId → reconnect | `Client_disconnect_after_submit_reconnects_to_the_same_job` | 客户端销毁，worker 照常完成；新客户端恢复会话 → 同 JobId 的 status/result；用同一 Idempotency-Key 重提交也返回同一 job，库里仍 1 行 |

顺带的产品修正：JWT 过期判定改为 `TokenValidationParameters.LifetimeValidator`（用注入的 `TimeProvider`，抛 `SecurityTokenExpiredException` 以保留 `token_expired` 码）。此前 IdentityModel 默认用 `DateTime.UtcNow`，与签发端和 refresh 校验用的时钟不一致，也无法在测试里推进。生产环境 `TimeProvider.System`，行为不变；`AuthEndpointTests` 23/23 仍通过。

### 10.3 性能

`dotnet/tools/CabinetNC.Cloud.PerfHarness`（slnx 新 `/tools/` 目录）：确定性合成用例 50/100/300/500 件（种子固定，同尺寸同哈希）、每尺寸 5 次顺序 + 2 与 5 路并发（100 件）；用真实 `CloudApiClient` + `IntranetComputeGateway`；服务端指标取自 status 响应（`StartedAtUtc−CreatedAtUtc` = queue wait、`DurationMs` = compute、`CompletedAtUtc−CreatedAtUtc` = server e2e），客户端 e2e 用 Stopwatch；worker CPU/峰值内存用 `docker exec` 读 cgroup v2 `cpu.stat`/`memory.peak`。密码只经 `CABINETNC_PERF_PASSWORD` 环境变量。

实测写在 **`docs/cloud/PERFORMANCE_RESULTS.md`**（只有观测值）。要点：30 + 30 个 job 全部 Succeeded；**计算中位 0–4 ms**（500 件 2–4 ms，最大值都是首个 job 的 JIT）；server e2e 中位 770–910 ms（worker 轮询 1 s）→ **294–376 ms**（轮询 0.2 s）；worker 峰值内存 72–88 MiB；5 路并发 1 个 worker 1.4 s 内全完；sheet 数与哈希两轮完全一致。建议：worker 2 vCPU / 1 GiB × 2 副本、`CABINETNC_WORKER_POLL_SECONDS=0.2`、Desktop `PollInitial` 250 ms；spec §6 的 8 vCPU / 32 GB 对 Nest 明显过剩（保留给 DB/MinIO/CAM-Post）。**未测**：第二台 LAN 机器的往返、真实工单的异形排版。

### 10.4 Gate

- 诊断（只凭 JobId → user/device/time/hash/engine/duration/result/correlation）✓；7 个失败场景自动化 ✓；50/100/300/500 + 2/5 并发实测 ✓ 且只记录观测值。**Task 10 通过。**
- Commit：`test: add intranet reliability and performance validation`。

---

## Task 11 — Migrate Operations/CAM, then Post

**NOT STARTED（有意）。** 计划规定"Nest acceptance PASS 之后才开始"，且每个模块是独立的 commit/review gate。本轮把 Task 12/13 先做完，让 PoC 有完整、诚实的验收结论；CAM/Post 迁移作为下一轮的第一项，路线不变：characterization → 抽 `IOperationsRunner`/`IPostProcessorRunner` 进 Compute.Core → 本机 gRPC 变 adapter → 云 JobType → Local/Server parity（golden NC 不得为过关而归一化掉有意义的差异）。验收表里 CAM/Post 记 **NOT_RUN**。

---

## Task 12 — Customer build and code protection（2026-09-06，机器 B）

### 12.1 做了什么

| 项 | 内容 |
|---|---|
| `CabinetNC.Desktop.csproj` | `-p:CustomerBuild=true`：定义 `CUSTOMER_BUILD`；去掉 `CabinetNC.ComputeWorker` 引用（不构建、不复制）；跳过 `CopyWorker` 与 `CopyToDesktopLaunch`；`DebugType=none`；**独立输出** `bin\Customer\` + `obj\Customer\`——第一版没有隔离，客户版 publish 覆盖了 `bin\Release` 里的开发版，导致本机 UI smoke 跑的是客户版二进制（"Intranet mode is not configured"）；加隔离后开发版 smoke 恢复 5/5 |
| `MainWindow.Cloud.cs` | `#if CUSTOMER_BUILD`：`ComputeModeSelected` 恒为 Intranet，下拉锁定「内网计算」并禁用；本机分支不可达 |
| `dotnet/scripts/verify-customer-package.ps1` | 5 类检查：可部署计算组件（ComputeWorker.*、Compute.Core、Cloud.Api/Worker/Infrastructure、EF/Npgsql/Minio）；核心算法程序集（`CabinetNC.Domain.dll`，`-PoC` 时记 GAP 否则 FAIL）；PDB/源码/工程文件；服务端密钥（`.env*`、文本与二进制里的 `CABINETNC_JWT_SIGNING_KEY=`/`*_PASSWORD=`/连接串/AccessKey 模式）；产品本体。退出码 0/1 |
| `dotnet/scripts/publish-customer.ps1` | publish + verify 一步 |
| `tests/ui-smoke` | 新动词 `assert-disabled:AutomationId`；场景 `07-customer-intranet-only.txt`（下拉禁用 → 登录 → 重新密排 → `内网 job`）；`run-all.ps1` 用 `CABINETNC_SMOKE_CUSTOMER_BUILD=1` 切换：客户版只跑 `*customer*`，开发版跳过 `*customer*` |
| `docs/security/CLIENT_CODE_PROTECTION.md` | 规则、验证结果、Domain 缺口与拆分路线、混淆评估（Obfuscar 2.2.50 MIT 支持 .NET 10 但维护者明确不支持完整 WPF/XAML；Eazfuscator.NET 2026.1 与 Dotfuscator Pro 7.5 支持 .NET 10 + WPF；Dotfuscator Community 不再随 VS 2026 附带；ConfuserEx 排除）、受保护构建的四项验收 |

### 12.2 实测

- `publish-customer.ps1` → `dist/CabinetNC-Cut-Customer`：77 个文件、137 MiB；程序集 = Application、Cloud.Contracts、Compute.Contracts、Desktop、Desktop.Core、**Domain**、FusionPackage、Infrastructure。
- 严格验证：**FAIL（1）**——只因 `CabinetNC.Domain.dll`；其余 4 类全部 ok。`-PoC` 验证：PASS（1 个已知缺口）。
- 字符串扫描：`Domain.dll`（540 KiB）与 `Desktop.dll`（789 KiB）里可见 `BlfNester / ClipperNfpNestingEngine / GuillotineCutPlanner / NcEmitter / NestEngineRouter / DeepnestPreviewNestingEngine`；`Desktop.Core.dll` 无。
- 客户版功能：场景 07 通过（`task12-ui-smoke-customer.log`）——`ComputeModeCombo` 禁用、登录、`密排完成 … 内网 job`。客户版**只能**经内网排版。
- 开发版回归：全量 735/735；UI smoke 5/5（06/07 因无 API 记 skipped）。

### 12.3 结论（如实）

- "No core compute engine in client"：**FAIL**——ComputeWorker/Compute.Core 已不在包内，但排版/CAM/Post 算法仍在 `CabinetNC.Domain.dll`（Desktop 的模型、校验、CAM 叠加、NC 预览都依赖它）。修复是结构性拆分 Domain.Model / Domain.Compute + Task 11 的迁移，不属于本 PoC。
- "No embedded secrets"：**PASS**（脚本 + 人工检查：客户端不含任何 `CABINETNC_*` 服务端变量名或凭据；refresh token 只在运行时 DPAPI 文件中）。
- 混淆：**NOT_RUN**（评估完成，未实施；计划禁止随手加过时工具，且在 Domain 拆分前意义有限）。
- Commit：`build: add customer build and package verification`。

---

## Task 13 — Final acceptance document（2026-09-06，机器 B）

`docs/cloud/INTRANET_POC_ACCEPTANCE.md`：15 行验收矩阵（14 PASS、**1 FAIL**：Customer build / No core compute engine——`CabinetNC.Domain.dll` 仍在客户包内）、范围外与 NOT_RUN 清单（Task 11、第二台 LAN 机器、混淆、真实 CNC）、按计划模板写的 Cursor final report（含服务器实测）、以及"不宣称 Production Ready"的声明。

### 收尾提醒（给下一位接手者）

1. **本机环境是会话级的**：每个新 shell 要先 `$env:DOTNET_ROOT='C:\Users\alex\AppData\Local\Microsoft\dotnet'; $env:PATH="C:\Users\alex\AppData\Local\Microsoft\dotnet;D:\Docker\Program\resources\bin;$env:PATH"`，否则 `dotnet` 会解析到系统目录里只有 9.0 运行时的安装（"No .NET SDKs were found"）。用户级 `DOTNET_ROOT` 指向了错误目录，建议用户自行修正。
2. git 身份只在会话环境变量里（`yzhan722 <58527051+yzhan722@users.noreply.github.com>`），没有写入任何 git config。
3. **所有 commit 都只在本地**（`feature/intranet-cloud-poc`，`7079d18…828ec11` + 本 commit），没有 push、没有 PR——按 AGENTS.md 由人来决定。
4. Docker Desktop 4.89.0 是本轮按用户要求装的（每用户安装 `D:\Docker\Program`，WSL2 后端）；`cabinetnc/cloud-api:local`、`cabinetnc/cloud-worker:local` 镜像仍在本机，compose 栈已 `down -v`，`deploy/intranet/.env` 已删除。
5. `.handoff/` 目录（zip、bundle、`local-evidence/` 全部日志与截图）被 `.git/info/exclude` 排除，不在仓库里；交接时要单独拷走。

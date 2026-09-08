# Client Code Protection — Customer Build (Task 12)

目标（design spec §14）：客户安装包**不含可复用的核心制造算法**、**不含服务端密钥**、**不含未批准的 PDB/源码**；其余客户端代码"反编译价值显著降低"。成功标准不是"不可逆向"。

## 1. 客户版是什么

```powershell
pwsh dotnet/scripts/publish-customer.ps1            # 发布到 dist/CabinetNC-Cut-Customer 并验证（严格模式）
pwsh dotnet/scripts/publish-customer.ps1 -PoC       # 同上，但把 CabinetNC.Domain.dll 记为"已知缺口"而不是失败
```

`dotnet publish -c Release -p:CustomerBuild=true -p:DebugType=none -p:DebugSymbols=false` 时 `CabinetNC.Desktop.csproj`：

| 开关 | 效果 |
|---|---|
| `CustomerBuild=true` → `CUSTOMER_BUILD` 编译常量 | `MainWindow.Cloud.cs`：`ComputeModeSelected` 恒为 `Intranet`；「计算位置」下拉锁定为「内网计算」且禁用；`RunNestAsync` 的本机分支不可达 |
| 去掉 `CabinetNC.ComputeWorker` 的 ProjectReference | 本机 gRPC worker **不再构建、不再复制**（开发版会把整个 worker 输出——含 `CabinetNC.Compute.Core.dll`——拷到 Desktop 目录和 `dist/CabinetNC-Cut`） |
| 跳过 `CopyWorker` / `CopyToDesktopLaunch` | 客户版不写 `dist/CabinetNC-Cut`（那是开发者启动目录） |
| `DebugType=none`（全局属性） | 所有项目都不生成 PDB |

## 2. 验证脚本 `dotnet/scripts/verify-customer-package.ps1`

| 检查 | 规则 | 2026-09-06 结果（`dist/CabinetNC-Cut-Customer`，77 个文件，137 MiB） |
|---|---|---|
| 可部署计算组件 | `CabinetNC.ComputeWorker.*`、`CabinetNC.Compute.Core.dll`、`CabinetNC.Cloud.Api/Worker/Infrastructure.dll`、EF/Npgsql/Minio 必须不存在 | **ok** |
| 核心算法程序集 | `CabinetNC.Domain.dll` 必须不存在（严格）/ 记为已知缺口（`-PoC`） | **FAIL（严格）/ GAP（PoC）**——见 §3 |
| PDB / 源码 / 工程文件 | `.pdb .cs .xaml .csproj .slnx .sln .proto` 一个都不能有 | **ok** |
| 服务端密钥 | 无 `.env*`；文本与二进制文件里无 `CABINETNC_JWT_SIGNING_KEY=`、`*_PASSWORD=`、`CABINETNC_OBJECTSTORE_SECRET_KEY=`、`Host=…;Password=…`、`AccessKey:` 等模式 | **ok** |
| 产品本体 | `CabinetNC.Desktop.exe`、`CabinetNC.Desktop.Core.dll` 存在 | **ok** |

功能验证：`tests/ui-smoke/scenarios/07-customer-intranet-only.txt` 用客户版 exe 跑通——「计算位置」下拉 `IsEnabled=false`（`assert-disabled`）→ 内网登录 → 重新密排 → `密排完成 … 内网 job`（`.handoff/local-evidence/task12-ui-smoke-customer.log`，截图 `07a/07b`）。客户版**只能**通过内网计算排版。

## 3. 缺口已关闭（Phase 2 · P2-5，2026-09-08）

`CabinetNC.Domain` 已拆为 **`CabinetNC.Domain`（模型：几何、零件、包、设置、校验、断料/桥接/标签等客户端几何）** 与 **`CabinetNC.Domain.Compute`（算法：BLF/GroupedBLF/NFP/Deepnest 引擎与路由、parts-in-part 打包、密排稳定性优化、OpsPlanner/PocketClearer/CamPipeline、NcEmitter 与后处理器实现）**，命名空间不变。客户版 csproj 不引用 `Domain.Compute`，Desktop 里的本机分支全部在 `#if !CUSTOMER_BUILD` 之内；`verify-customer-package.ps1` 现在只有严格模式：禁止 `CabinetNC.Domain.Compute.dll`，并对每个 `CabinetNC.*.dll` 扫描算法类型名（`BlfNester / GroupedBlfNester / ClipperNfpNestingEngine / DeepnestPreviewNestingEngine / NestEngineRouter / PartsInPartPacker / SheetStabilityOptimizer / OpsPlanner / PocketClearer / PocketClearIslands / CamPipeline / NcEmitter`）。

2026-09-08 结果：`dist/CabinetNC-Cut-Customer`（78 个文件）**RESULT: PASS**；客户版 UI smoke 07 用真实栈跑通排版 → 刀路 → NC → 导出（全部在服务器计算）。客户版里"本张密排优化"按钮提示暂不提供（该算法尚无内网 job）。

### 3a. 历史记录：拆分前的缺口（PoC 阶段）

Desktop 需要 `CabinetNC.Domain` 的模型（`Panel`、`Outline`、包/工程、`NestSettings`、校验器 `NestValidator`/`NestExportGate`、CAM 叠加与 NC 预览），而**同一个程序集**里还住着核心算法：`BlfNester`、`ClipperNfpNestingEngine`、`DeepnestPreviewNestingEngine`、`NestEngineRouter`、`GuillotineCutPlanner`、`NcEmitter` 等。对客户包做字符串扫描：

| 程序集 | 大小 | 可见的算法类型名 |
|---|---|---|
| `CabinetNC.Domain.dll` | 540 KiB | BlfNester, ClipperNfpNestingEngine, GuillotineCutPlanner, NcEmitter, NestEngineRouter, DeepnestPreviewNestingEngine |
| `CabinetNC.Desktop.dll` | 789 KiB | 引用上述类型（本机分支虽被 `CUSTOMER_BUILD` 编译掉，但 ops 叠加/NC 预览/校验仍直接调用 Domain） |
| `CabinetNC.Desktop.Core.dll` | 99 KiB | 无 |

所以"no core manufacturing engine in client"在 PoC 阶段**没有达成**，验收表如实记 FAIL。达成它需要一次结构性拆分，不属于本 PoC：

1. `CabinetNC.Domain` → `CabinetNC.Domain.Model`（几何、零件、包、设置、校验——客户端可发）+ `CabinetNC.Domain.Compute`（BLF/NFP/Guillotine/CAM/Post——只进 `CabinetNC.Compute.Core` 与 worker）。
2. Desktop 里仍在本机调用算法的路径（CAM 叠加 `RebuildOpsOverlay`、NC 预览 `NcEmitter`、`GuillotineCutPlanner`）按 Task 11 的顺序迁到云 job：Operations → Preflight → Post。
3. 迁完之后 `verify-customer-package.ps1` 的严格模式才应该变绿；`-PoC` 开关随即删除。

## 4. 混淆评估（未实施，理由如下）

| 候选 | 版本 / 许可 | .NET 10 | WPF/XAML | 结论 |
|---|---|---|---|---|
| **Obfuscar** | 2.2.50，MIT，NuGet GlobalTool | 是（2.2.49 起） | **维护者明确声明不支持完整 XAML/WPF 混淆**（issue #551：BAML 处理依赖 ILSpy 第三方库，相关 issue/PR 不再处理；#550 里 `KeepPublicApi=false` 直接导致 WPF 应用无法启动） | 只能做"保留公共 API + 只重命名内部符号"的浅层混淆；对 `MainWindow.xaml.cs` 这种 9000 行、大量 `x:Name`/数据绑定/反射的窗口收益小、风险高。**不采用**作为主方案 |
| **Eazfuscator.NET** | 2026.1，商业（约 $399 永久 + 年更） | 是（2025.3 起完整支持，2026.1 支持 VS 2026 / SLNX） | 专门处理 WPF（XAML 命名空间映射、`XamlParseException` 修复记录在更新日志） | **首选候选**；需采购后在客户版上试跑并走 §5 的四项验收 |
| **Dotfuscator Professional** | 7.5.0，商业（订阅） | 是 | 是 | 可选；价格高于需求 |
| Dotfuscator Community | 随 VS 2022 附带；**VS 2026 不再附带** | 未验证 | 基础 | 不再是可依赖的路线 |
| ConfuserEx（含各分叉） | 停止维护 | 否 | — | 排除 |

**结论**：不在 PoC 里"随手加一个过时混淆器"（计划明令禁止）。混淆只是纵深防御的最后一层；真正的价值来自 §3 的拆分——算法根本不在客户端。等拆分完成后再对客户版做一次 Eazfuscator.NET 试跑。

## 5. 每个受保护构建必须通过的验收（未来执行）

```text
1. dotnet test dotnet/CabinetNC.slnx -c Release            全绿
2. pwsh dotnet/tests/ui-smoke/run-all.ps1                  开发版 6 个场景
   CABINETNC_SMOKE_CUSTOMER_BUILD=1 + -Exe <客户版 exe>     客户版场景 07
3. pwsh dotnet/scripts/verify-customer-package.ps1          严格模式 PASS
4. 用 ILSpy / dnSpy 打开客户包：找不到排版/CAM/Post 算法实现；剩余代码的类型/成员名不可读
```

当前状态（2026-09-06）：1 ✓（735/735）、2 ✓（开发版 6/6，客户版 07 ✓）、3 **仅 `-PoC` 模式 PASS**、4 **未执行**（未混淆、Domain 仍在包内，可预期不通过）。

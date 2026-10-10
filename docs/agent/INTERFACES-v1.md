# OmniCam Agent 接口 v1 — 已实现基线

> CURRENT / 对应 `yzhan722/cabinetnc-cut@sprint/14d-rc`，审计锚点 `8f856779`（2026-10-10）。**此处 v1 是 Agent 接口文档版本，不是制造快照版本**；跨系统 `cnjob` Schema 的权威文件仍是 `docs/manufacturing-snapshot-v1.schema.json`（当前 1.1）。
> 目标架构见 [INTERFACES-v2.md](INTERFACES-v2.md)，开发任务统一见 [Cab Lab Agent PRD](https://github.com/yzhan722/the-cab-lab/blob/devops/docs/agent/PRD-2026-10.md)。

## 1. 实际存在的调用面

| 类型 | 真实入口 | 当前能力与限制 |
|---|---|---|
| Application (.NET) | `dotnet/src/CabinetNC.Application/Projects/ProjectSession.cs` | `OpenPackageFile` / `AddPackageFile` / `OpenPackageJson`、`ReplacePanel`、`RemovePanel`、`History` 等；为应用内部 API，不是 Agent CLI |
| Import | `dotnet/src/CabinetNC.FusionPackage/PackageImporter.cs`、`ManufacturingSnapshotImporter.cs` | 读 `.cnjob` ZIP / Legacy Cut Package，返回 `PackageImportResult`（`Ok`、`Package`、`Warnings`、`Errors`） |
| 无头制造校验 | `dotnet/tools/VerifyJob/Program.cs` | `VerifyJob <file.cnjob> [--json]` 整管线；`<export-dir>` 验证现成 bundle；`--package ... --manifest ...` 复验；`--demo` 样例 |
| 核心算法 | `dotnet/src/CabinetNC.Domain/Nesting/`、`Manufacturing/` | 现有 Nest、OpsPlanner、NC、preflight、ToolCatalog / PostRecipe；暂缺统一 Agent Facade |
| 独立验证 | `dotnet/src/CabinetNC.Verify/ExportVerifier.cs` | CAD 制造意图和 NC 的独立比较；预演/金标链路调用 |
| 本机 Worker | `dotnet/protos/worker.proto` + `CabinetNC.ComputeWorker` | `cabinetnc.compute.v1` Named Pipe / gRPC；不属于互联网远程 API |
| 车间云文件服务 | `dotnet/src/CabinetNC.CloudApi/Program.cs` + Desktop `CloudJobClient.cs` | Cloud Run/GCS 上传、列举、下载、删除车间工程与产物；**不是**通用 Nest/CAM Cloud Worker |

## 2. 已有命令行语义

~~~bash
# 在 dotnet 工作区构建 VerifyJob 后，使用对应实际程序集或 dotnet run
dotnet run --project dotnet/tools/VerifyJob -- <file.cnjob> --json
dotnet run --project dotnet/tools/VerifyJob -- <bundleDir> --json
dotnet run --project dotnet/tools/VerifyJob -- --demo
~~~

`VerifyJob` 的流程：
- 包输入：`PackageImporter.FromPath` → `GroupedBlfNester.Pack` → `OpsPlanner` → `ContourToolOffset` → `NcPreflight` → `SheetBundleBuilder` → `ExportVerifier`。
- 已导出的 bundle：读取 `*.bundle.json` / 原始 `*.cut.json` / per-sheet manifests / NC，再调用 `ExportVerifier`。
- 退出码设计为 `0` 无错误、`1` 验证失败、`2` 输入或用法异常。**调用端仍必须审查所有 preflight + verify 结果**。

**v1 的自动化限制**：
1. 即使使用 `--json`，`RunPackageFile` / `RunPipeline` 仍输出“== file”“preflight: …”等人类可读行或多段结果；**不能假定 stdout 是单条 JSON 对象**。
2. `RunPipeline` 中 `NcPreflight.Check` 的失败仅打印，调用 `SheetBundleBuilder` 时 `enforcePreflight:false`、`enforceVerify:false`。因此单看进程退出码或生成了 NC，不能代表制造安全通过；Agent 对这条路径必须另外检查 preflight / ExportVerifier。
3. `ProjectSession.ManufacturingDirty` 用于标识编辑后 Nest/CAM 过期；现有内部调用不等于具有 revision 检查的 Agent 并发会话。
4. `VerifyJob` 是验证/回放工具，不等于生产应用的完整用户交互、工艺审核或机床发送许可。

## 3. 本机 ComputeWorker 协议 v1

- 命名管道 `cabinetnc.compute.v1`；契约类 `CabinetNC.Compute.Contracts/WorkerPipes.cs`，Proto 真源 `dotnet/protos/worker.proto`。
- RPC：`WorkerHealth.Ping`、`WorkerHealth.GetWorkerVersion`、`Nesting.StartNesting`、`Operations.GenerateOperations`、`PostProcessor.GenerateNc`。
- `StartNestingRequest` 当前传 `parts: {panel_id,width_mm,height_mm,may_rotate,material,thickness_mm}[]` 与板材长宽、边距、间距、旋转开关；`StartNestingReply` 含 `placements`、`unplaced`、`warnings`。
- **关键几何边界**：Worker 的 `StartNesting` 当前按宽高重建矩形板件；它不是可接受真实异形、多孔轮廓的完整制造 Snapshot 契约，不应在 Agent 中宣称 true-shape parity。
- `GenerateOperations` 根据给定面板与排版生成操作；`GenerateNc` 返回 NC 字符串和机器信息。这两条 RPC 均**不能替代安全验证与人工发布审批**。

## 4. 车间 CloudApi 已实现 HTTP 入口

| 路由 | 作用 |
|---|---|
| `GET /health` | 健康检查 |
| `GET /v1/jobs` | 车间工程列表 |
| `POST /v1/jobs` | 创建上传任务/签名上传地址 |
| `GET /v1/jobs/{id}/project` | 获取工程下载地址 |
| `DELETE /v1/jobs/{id}` | 删除工程 |

以 `X-OmniCam-Token` 做简单服务端授权；GCS 是对象存储，不等同独立租户 IAM、受限 Agent 授权或 Cloud Compute。**v1 不允许 Agent 通过这些路由自动执行删除、上传 NC 或车间操作。**

## 5. 制造契约与测试

- 输入：`.cnjob` = ZIP `manifest.json` + `snapshot.json`，Schema `cabinetnc.manufacturing-snapshot 1.1`；mm，板局部 XY，Snapshot A 为加工面，盲特征只允许单面。
- v1.1 附加 `source.producer/producerVersion`、`materialId` 分组、`colorName/surfaceMode/series/grained`、`edgeBands`、`EITHER` 条件；旧 1.0 可导入。制造 Schema 与 Agent API 版本分离。
- Golden：`dotnet/tests/CabinetNC.Domain.Tests/Regression/ReleaseGoldenRegressionTests.cs`；包括 `cab_lab_kitchen` 全管线 Nest/NC/Verify 回放。
- CI：`.github/workflows/regression.yml`、`windows-desktop.yml`；当前 `8f856779` 的 Regression 和 Windows Desktop 均通过。
- 推荐验证：`dotnet test dotnet/tests/CabinetNC.Domain.Tests -c Release`、`dotnet test dotnet/tests/CabinetNC.Verify.Tests -c Release`、`dotnet test dotnet/tests/CabinetNC.Package.Tests -c Release`，另执行 `VerifyJob <real.cnjob>`。

## 6. 当前不存在的 Agent 接口

没有正式 `omni.inspect/validate/nest/cam/verify` 的统一 JSON Tool Registry；没有完整事务会话/租户分权/人工审批的 Agent Facade；本机 Worker 不能当远程多租户 Cloud Worker。它们是 v2 开发目标，不能要求编程 AI“接入一个已存在的 Omni Agent Server”。

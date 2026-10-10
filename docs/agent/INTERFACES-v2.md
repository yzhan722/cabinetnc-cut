# OmniCam Agent 接口 v2 — 目标契约（未实现）

> TARGET / 基于 `devops@8f856779`。当前实际能力见 [v1](INTERFACES-v1.md)；实施验收见 [跨项目 Agent PRD](https://github.com/yzhan722/the-cab-lab/blob/devops/docs/agent/PRD-2026-10.md) 的 A04/A05。
> **不修改 `cnjob 1.1` 的核心制造数据模型，不复制 Nest/CAM 算法，不把 Worker gRPC 直接开放给公网。**

## 1. 目标链路

~~~text
Agent / CLI / Desktop UI
          ↓
Omni Agent Facade（权限、事务、审计、结构化返回）
          ↓
Application / PackageImporter / Domain Nest / Ops / Verify
          ↓
可复现的预演结果和报告
          ↓
人工确认 → 正式导出/车间使用（仅人工路径）
~~~

UI 和 Agent 应复用领域逻辑，但初期不要求一次性重写 WPF。

## 2. 拟新增的受控工具

| 命令（目标命名） | 风险 | 输入 → 输出 | 首批 |
|---|---|---|---|
| `omni.capabilities` | READ | 支持的输入、机器、工具版本、动作 Schema | 是 |
| `omni.import` | READ/LOCAL_SESSION | `sourcePath,expectedHash` → `sessionId,revision,panelCount,warnings/errors` | 是 |
| `omni.inspect` | READ | `sessionId` → Job/Panel/Material/Feature/制造状态摘要 | 是 |
| `omni.validate` | READ | `sessionId` → 结构、刀具、面权限、缺项、制造 gate 报告 | 是 |
| `omni.run-pipeline` | COMPUTE_PREVIEW | `sessionId,machineId,stock,recipe` → Nest/ops/NC 校验报告（只读预演） | 是 |
| `omni.verify-export` | READ | `bundleDir` 或 `package+manifest+nc` → VerifyReport | 是 |
| `omni.nest.preview` | COMPUTE_PREVIEW | 输入包与 Sheet 约束 → 布局/碰撞/未排入件 | 第二批 |
| `omni.cam.preview` | COMPUTE_PREVIEW | 已验证布局 + machine/recipe → ops/NC diff | 第二批 |
| `omni.export.request` | MANUFACTURE | 预演哈希 + 人审批准 → 产物；**默认拒绝 Agent 独立执行** | 后续 |
| `omni.machine.send` | PROHIBITED | 不为 Agent 暴露 | 不做 |

第一批只调用**已有应用/验证代码**，将 `VerifyJob` 的算法流程提炼复用；不得向 `MainWindow.xaml.cs` 添加隐式 Agent 业务实现。

## 3. 建议的单次请求 / 回执

~~~json
{
  "apiVersion": "2",
  "operationId": "op-001",
  "verb": "omni.run-pipeline",
  "sessionId": "local-001",
  "expectedRevision": 3,
  "args": {"machineId": "explicit-shop-profile"}
}
~~~

~~~json
{
  "ok": true,
  "operationId": "op-001",
  "revision": 3,
  "effect": {"sheets": 2, "panels": 16},
  "validation": {"ok": true, "preflightIssues": [], "verifyIssues": []},
  "artifacts": [{"kind": "report", "sha256": "..."}],
  "auditId": "run-001"
}
~~~

- v2 的 `ok:true` 不等于可交给机床；`validation.ok`、机床配置、人工审批状态分别提供，不可省略。
- 结构错误码：`invalid_input`、`unsupported_schema`、`unsupported_dialect`、`missing_machine_profile`、`invalid_tool`、`revision_conflict`、`validation_blocked`、`permission_denied`、`internal`。
- **方言诚实**：`ExportVerifier` 只覆盖 OSAI（v1 §3）。对 Syntec 后处理路径，v2 要么补齐方言验算，要么在结果上永久标 `dialectCoverage:"osai_only"`——Syntec 输入走 `omni.verify-export` 时报 `unsupported_dialect`，**不得沉默放行**。预检软错误的操作员豁免口（`export.preflight.override`）不自动化：Facade 不提供等价 flag，软错误一律回 `validation_blocked` 交人审。
- CLI `--json` 的 stdout **恰好一个 JSON 对象**；日志写 stderr；退出码与 `validation.ok` 一致。无机器、无工具、NC 验证红灯不能返回 0。
- 所有文件输入限定工作区/只读 staging，检查文件类型、SHA256、大小和路径遍历，失败后无持久导出副作用。

## 4. 制造安全与部署

1. Snapshot A 单面盲加工规则、`bboxFallback` 拒绝、`materialId`/板厚、`ToolCatalog`、`NcPreflight`、`ExportVerifier` 是不可绕过的执行门。Facade 调 `SheetBundleBuilder` 必须保持默认 `enforcePreflight/enforceVerify=true`；`enforce*:false` 只允许出现在标注 measurement 的内部工具里，ComputeWorker 的裸 `GenerateNc`（无门）不得包装成"发布"动词。
2. 先完成 **单一真实 `.cnjob` → import → nest → cam → post → independent verify → JSON 报告** 的只读 API；任何全管线步骤失败，不得继续声明可发布。
3. `WorkerPipes.Name` 和 `worker.proto` 维持原有本地 ABI；以后如需真实异形远程 Nest，先定义独立 true-shape Job Schema 与 local/server parity Golden，不能直接复用宽高矩形 v1 RPC 做生产。
4. NC 输出、设备发送必须保留**独立的人类批准环节**，并绑定 `sessionRevision`、`inputHash`、`machineId`、`toolCatalogVersion`；批准后任意输入变化自动失效。
5. 车间 CloudApi 文件服务暂不改变、不当作 Agent Compute；未来远程执行另立 Auth / Tenant / Quota / Audit / Worker 契约并实施授权测试。

## 5. DoD / 测试

- 当前 Cab Lab `fixtures/replay/kitchen.cnjob` 及 Omni `tests/testdata/regression/packages/cab_lab_kitchen.cnjob` 可被新 Facade 导入，输出稳定机器可读摘要，Golden 保持绿。
- 至少覆盖：非法/空 ZIP、1.1 schema 错误、双面盲加工、未加工 Feature、缺失刀具、无可排板、进程中止、版本冲突、恶意路径和被篡改批准哈希。
- `ExportVerifier` 报错、`NcPreflight` 报错都返回非零，且不留下可直接交付车间的 NC 文件。
- `dotnet test` Domain/Package/Verify + Windows Desktop/Worker CI 全绿；任何真实 NC 位置/刀路差异交 Troy 确认，不得静默更新 Golden。

## 6. 开发顺序

`Omni Facade 只读核心` → `VerifyJob JSON/退出码纠正` → `真实 cnjob 双仓集成` → `独立 Nest/CAM preview（有需求再加）` → `人工审批与远程化（以后）`。

# Intranet Cloud — Commercial Readiness Plan (Phase 2)

起点：`feature/intranet-cloud-poc @ 4063903`，PoC 验收 14 PASS / 1 FAIL（客户包仍含算法程序集）/ 若干 NOT_RUN。
目标：把"PoC 通过"变成"车间可以每天用、我们敢收钱"。下面每一项都有可自动验证的完成标准；顺序按**商业阻断程度**排，不按技术趣味。

## 0. 商业可用的定义（验收口径）

| # | 门槛 | 为什么是硬门槛 |
|---|---|---|
| G1 | 车间管理员能自助管理操作员与设备（建号、停用、重置密码、踢掉丢失的电脑），操作员能改自己的密码 | 现在只有 bootstrap 管理员一个账号，第二个人就无法上岗 |
| G2 | 内网模式的排版结果与本机**功能等价**：异形精排（NFP）、余料/多规格大板、禁排区、纹理、parts-in-part | 现在内网只跑矩形 BLF 并"披露降级"，车间不会接受精排变粗排 |
| G3 | 刀路（CAM）与 NC 后处理也在服务器计算，Desktop 内网模式全流程不依赖本机算法 | 否则客户包无法去掉算法程序集，也谈不上"核心算法不出服务器" |
| G4 | 客户包严格验证 PASS（无 `CabinetNC.Domain.Compute`/worker/密钥/PDB）；受保护构建通过四项验收 | 这是 PoC 唯一 FAIL 的 gate |
| G5 | 运维可交付：备份/恢复演练、双 worker、健康检查含依赖、日志保留、升级回滚、监控告警 | 车间没有 SRE，必须一键化 |
| G6 | 第二台 LAN 机器 + 车间 CA 证书的端到端验证；真实工单规模的性能与 parity 数据 | 单机数据不能作为交付依据 |

## 1. 工作项与顺序

| 顺序 | 工作项 | 覆盖门槛 | 完成标准 | 状态 |
|---|---|---|---|---|
| P2-1 | **管理 API**：`/api/v1/admin/users`（增/列/停用/启用/重置密码/改角色）、`/api/v1/admin/devices`（列/吊销=撤销全部 refresh token）、`/api/v1/auth/password`（本人改密并撤销其他会话）；密码策略 ≥ 12；全部写审计 | G1 | API 测试覆盖每个端点的成功、越权（operator 403、跨 tenant 404）、最后一个 admin 不能被停用；runbook 给 curl 示例 | **DONE** `683f8ce` |
| P2-2 | **Nest 契约 v2（真形）**：`POST /api/v1/jobs/nest/v2` 接受完整轮廓（多边形 + 通孔）、纹理/允许角度、完整大板队列（余料、禁排区、按边余量、材料/厚度、纹理）、`NestSettings`、引擎偏好与超时；worker 运行与 Desktop 本机**同一个** `NestEngineRouter`；v1 矩形契约保留为子集 | G2 | parity 测试：同一请求本机 `NestEngineRouter.Run` 与服务器结果逐字段相同；Desktop 内网模式 `intranet_contract` 降级清单为空；UI smoke 06 在 NFP 引擎下通过 | **DONE**（本 commit） |
| P2-3 | **Operations（CAM）云 job**：characterization → `IOperationsRunner` 入 Compute.Core → 本机 gRPC 变 adapter → `jobs/operations` → Desktop 内网模式刀路走服务器 | G3 | op 数量/零件-特征关联/大板坐标/钻孔-轮廓语义 parity；本机回归不变 | |
| P2-4 | **Post（NC）云 job**：golden NC → `IPostProcessorRunner` → `jobs/post` → Desktop 内网 NC 预览/导出走服务器 | G3 | 本机/服务器 NC 归一化后逐行相同，且**不归一化掉有意义差异**；导出流程 UI smoke 通过 | |
| P2-5 | **Domain 拆分**：`CabinetNC.Domain.Model`（几何、零件、包、设置、校验）/ `CabinetNC.Domain.Compute`（BLF/NFP/Guillotine/CAM/Post）；Desktop 客户版只引用 Model；`verify-customer-package.ps1` 严格模式 PASS，删除 `-PoC` | G4 | 全量回归、UI smoke（开发版+客户版）、严格验证 PASS | |
| P2-6 | **运维包**：`backup.sh/restore.sh`（pg_dump + MinIO 卷）并演练；compose 默认 2 worker、poll 0.2 s；`/api/v1/health` 深检（DB/MinIO）与 `/api/v1/health/live`；日志轮转；升级/回滚步骤；Prometheus 指标（job 计数/时延/失败） | G5 | 恢复演练：备份→清库→恢复→job 与用户可查；杀掉一个 worker 后 job 由另一个完成 | |
| P2-7 | **受保护构建**：Eazfuscator.NET（或 Dotfuscator Pro）试用版在客户包上试跑 → 全量测试 + UI smoke + 严格验证 + ILSpy 检查 | G4 | 四项验收全绿；否则记录 FAIL 与原因 | 需采购/试用许可 |
| P2-8 | **现场验证**：第二台 LAN 机器、车间 CA 证书、真实工单（去标识）性能与 parity | G6 | 数据写入 `PERFORMANCE_RESULTS.md` §"现场" | 需运维/现场 |

P2-1 与 P2-2 可以立刻开始且互不依赖；P2-3/4 依赖 P2-2 的契约基础设施；P2-5 依赖 P2-3/4；P2-7/8 需要外部条件。

## 2. 不变的纪律

- 每项：先写测试 → 实现 → `dotnet test dotnet/CabinetNC.slnx -c Release` 全绿 → UI smoke 不退化 → `IMPLEMENTATION_NOTES.md` 记录 → 单独 commit。
- 结果只写 PASS / FAIL / BLOCKED / NOT_RUN；性能只写实测。
- 客户端永远不放服务端密钥；TLS 校验不可关闭；内网失败不静默回退本机。

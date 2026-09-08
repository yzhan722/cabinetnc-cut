# Intranet Cloud PoC — Acceptance

日期 2026-09-06 · 分支 `feature/intranet-cloud-poc` · 基线 `sprint/14d-rc @ 5e410d5` · 终点 `828ec11`（本文件的 commit 在其后）。
结果只允许 `PASS / FAIL / BLOCKED / NOT_RUN`；BLOCKED 不写成 PASS。证据文件在 `.handoff/local-evidence/`（git 外，机器 B 本地）与仓库内的测试/文档。

## 1. 验收矩阵

| Gate | Check | Result | Evidence |
|---|---|---|---|
| Baseline | Full .NET regression | **PASS** | `dotnet test dotnet/CabinetNC.slnx -c Release` → **735 / 0 / 0**（9 个测试程序集；`task12-dotnet-test-release.log`）。基线时 554，新增 181 个云端/客户端测试 |
| UI | Local UI smoke | **PASS** | 开发版 5/5 场景（01–05）通过，06/07 无 API 时记 skipped（`task12-ui-smoke-local.log`）；有 API 时 6/6（`task9b-ui-smoke-all.log`） |
| Auth | Login | **PASS** | `AuthEndpointTests` 23/23；经 Caddy HTTPS 的真实登录 200、错密码 401 `invalid_credentials`（`task8-https-smoke.txt`）；Desktop 登录窗口 → 场景 06/07 |
| Auth | 15 min access token | **PASS** | 登录响应 `accessTokenExpiresInSeconds=900`（smoke）；API 用注入时钟做 `LifetimeValidator`，`Access_token_expiry_during_polling_is_refreshed_transparently` 在 +16 min 时得到 401 `token_expired` 并自动刷新 |
| Auth | Refresh rotation/reuse protection | **PASS** | `AuthEndpointTests`：刷新轮换、旧 token 重用 → 401 `refresh_reuse_detected` 且整族撤销、family advisory lock 并发测试；logout 后 refresh → 401（smoke）；DPAPI 存储只有 refresh token（`WindowsTokenStoreTests`） |
| Tenant | Cross-tenant blocked | **PASS** | `A_tenant_cannot_read_another_tenants_job`、`Cross_tenant_reads_are_blocked_for_the_client_too`、`Admins_of_other_tenants_see_nothing` → 404 `job_not_found`；login 的 tenant slug 只用于查找，JWT `tenant_id` 来自数据库 |
| Job | Async submit/status/result | **PASS** | `POST /jobs/nest` 202 → `GET /jobs/{id}` → `GET /jobs/{id}/result`（`JobEndpointTests` 11、端到端 3）；Desktop 网关 `submit → poll 1/2/5 s → result`（`IntranetComputeGatewayTests` 9）；真实栈 smoke：Succeeded，durationMs 40–42 |
| Job | Idempotency | **PASS** | 同 key → 同 JobId、库中 1 行（顺序 + 6 并发）；同 key 不同 payload → 409 `idempotency_conflict`；输入落盘失败后同 key 自愈；`ON CONFLICT DO NOTHING` 实现 |
| Worker | Crash recovery | **PASS** | `Worker_crash_after_claim_is_recovered_through_lease_expiry`（领取后消失 → 租约 5 min 到期 → 另一 worker 重领 `AttemptCount=2` → Desktop 同一调用拿到结果）；`Losing_the_lease_mid_run_discards_the_completion`（fencing）；3 次尝试后 `Failed`；`FOR UPDATE SKIP LOCKED` 并发领取测试 |
| Storage | Input/result hash | **PASS** | `InputSha256` = 客户端可复算的 canonical JSON SHA-256；worker 校验输入哈希（篡改 → `storage_failed` 不重试）；`ResultSha256` = 存储字节哈希，`GET result` 每次重算比对（篡改一位 → 500）；两次独立运行同输入 → 同 input/result 哈希（`task8-https-smoke.txt`、`task9b-audit-query.txt`、`PERFORMANCE_RESULTS.md`） |
| Nest | Local/server parity | **PASS（限矩形契约）** | 同一 `INestingRunner`：Task 2 新旧本机 worker 11 条 gRPC 请求逐字节一致；`Server_result_equals_running_the_shared_runner_directly` 与端到端测试逐字段相等。**范围说明**：parity 是"本机 gRPC worker ↔ 云 worker（同一 runner，矩形 AABB 契约）"；Desktop 本机默认的 NFP 异形精排是另一引擎，内网模式会把输入降级为矩形并在 UI 列出每一项降级（`intranet_contract`） |
| Perf | 50/100/300/500 | **PASS（已测，单机）** | `PERFORMANCE_RESULTS.md`：60 个 job 全部 Succeeded；compute 中位 0–4 ms；server e2e 中位 770–910 ms（worker 轮询 1 s）/ 294–376 ms（0.2 s）；2/5 并发 1 个 worker 1.4 s 内完成；worker 峰值内存 ≤ 88 MiB。**未测**：第二台 LAN 机器往返、真实工单 |
| Logs | Diagnose from JobId | **PASS** | `GET /api/v1/admin/jobs/{id}/diagnostics`（admin、tenant 作用域）返回 user/device/time/hash/engine/duration/error/correlation + 审计链；`AdminDiagnosticsTests` 5/5 且响应不含任何哈希/令牌；SQL 核对 `task9b-audit-query.txt`（submitted→claimed→started→succeeded，带邮箱与设备名）；API/worker JSON 日志带 jobId/correlationId |
| Customer build | No core compute engine | **FAIL** | `verify-customer-package.ps1` 严格模式：ComputeWorker/Compute.Core/云端程序集/PDB/源码 **均不存在**，但 `CabinetNC.Domain.dll`（含 BLF/NFP/Guillotine/CAM/NC 算法）仍在包内——Desktop 的模型、校验、CAM 叠加、NC 预览依赖它。`-PoC` 模式 PASS（记为已知缺口）。客户版功能：只能经内网排版（场景 07）。修复路线见 `docs/security/CLIENT_CODE_PROTECTION.md` §3 |
| Security | No embedded secrets | **PASS** | 脚本扫描客户包（`.env*`、连接串、`CABINETNC_JWT_SIGNING_KEY=`、`*_PASSWORD=`、AccessKey 模式，文本与二进制）无命中；服务端 secret 只经环境变量；`.env.example` 全占位符；git 中无 `.env`；refresh token 只在 DPAPI 文件 |

## 2. 计划范围之外 / 未做

| 项 | 状态 |
|---|---|
| Task 11 CAM/Post 迁移 | **NOT_RUN**（有意：先闭合 Nest PoC 验收；路线已定） |
| 第二台 LAN 机器验证 HTTPS 与延迟（Task 8） | **NOT_RUN**（机器 B 只有一台机器；runbook §4.5 给了命令与记录字段） |
| 混淆 / 反编译验收（Task 12） | **NOT_RUN**（评估完成：Obfuscar 不支持完整 WPF；Eazfuscator.NET / Dotfuscator Pro 为候选） |
| 真实 CNC 执行 | **NOT_RUN**（未授权，本 PoC 不含） |
| 公网云 / Azure / Kubernetes | 不在范围 |

## 3. Cursor final report

```markdown
# Intranet Cloud PoC Result

## Baseline
- Branch: feature/intranet-cloud-poc
- Start commit: 5e410d5 (sprint/14d-rc; 554/554 regression, 5/5 UI smoke re-verified on machine B as 3a5ef1c)
- End commit: 828ec11 (+ this document)

## Implemented
- Auth: env-only bootstrap admin; POST login/refresh/logout; HS256 JWT 15 min (iss cabinetnc-cloud, aud cabinetnc-desktop, 60 s skew); PBKDF2 via PasswordHasher; per-IP fixed-window rate limits; ApiError codes; correlation ids; audit table
- Token rotation: opaque 30-day refresh tokens stored as SHA-256, rotated per use, family reuse detection revokes the family, family-level advisory lock; Desktop stores only the refresh token (DPAPI CurrentUser), access token in memory, single-flight refresh, 401 → one refresh → one replay
- Device identity: per-install random GUID (device-id file), sent as deviceId; Devices table upsert; never hardware-derived
- Job queue: PostgreSQL ComputeJobs with FOR UPDATE SKIP LOCKED claims, 5-min leases, AttemptCount ≤ 3, fenced completion by worker id, ON CONFLICT idempotency, expired-lease reaper
- Nest cloud compute: shared INestingRunner (Compute.Core) used by local gRPC worker and cloud worker; API validates the rectangular contract, stores input.json (+SHA-256) in MinIO, worker verifies input hash, runs, stores result.json (+SHA-256), records EngineVersion (git revision) and compute ms; GET result re-verifies the hash
- Logs/audit: JSON console logs with jobId/correlationId; audit events job.submitted/claimed/started/succeeded/failed/retry plus auth events; admin diagnostics endpoint from JobId
- Desktop remote mode: 本机计算 / 内网计算 selector, login window (https required except loopback), settings + DPAPI token + device id under %LocalAppData%\CabinetNC, Intranet branch of RunNestAsync with contract-downgrade disclosure, no silent fallback; engine badge shows the server session
- CAM: NOT RUN
- Post: NOT RUN
- Customer package protection: CustomerBuild publish (no worker, no Compute.Core, no PDBs, Intranet-only), verify-customer-package.ps1, protection document; Domain.dll gap documented; obfuscation evaluated, not applied

## Tests
- .NET: 735 passed / 0 failed / 0 skipped (Release), Testcontainers suites skip honestly without Docker (verified with DOCKER_HOST=tcp://127.0.0.1:1)
- UI smoke: developer build 5/5 (+ intranet scenario 06 passes against the live stack: 6/6); customer build scenario 07 passes
- Cloud E2E: 7-scenario reliability suite through the real Desktop client (duplicate submit, worker crash/lease recovery, API restart, object-store outage, token expiry while polling, cross-tenant read, client reconnect) + 3 HTTP end-to-end tests + real-stack HTTPS smoke through Caddy
- Local/cloud parity: byte-identical gRPC A/B (Task 2, 11 requests); server result == shared runner direct output (worker + API tests); identical hashes across independent runs — within the rectangular contract
- Performance: docs/cloud/PERFORMANCE_RESULTS.md (observed only)

## Server measurements (machine B, Docker Desktop WSL2 VM 12 CPU / 7.7 GB, single worker, synthetic cases)
- CPU/RAM: worker peak memory 72–88 MiB; container CPU ≤ ~0.2 s per job incl. polling/JIT
- 50 panels: compute median 1 ms; server e2e median 911 ms (poll 1 s) / 376 ms (poll 0.2 s); 9 sheets
- 100 panels: compute median 0 ms; server e2e median 774 ms / 369 ms; 17 sheets
- 300 panels: compute median 1–2 ms; server e2e median 891 ms / 294 ms; 51 sheets
- 500 panels: compute median 2–4 ms; server e2e median 769 ms / 297 ms; 81 sheets
- 2 concurrent (100 panels): all Succeeded, batch wall 1347 ms / 824 ms
- 5 concurrent (100 panels): all Succeeded, batch wall 1368 ms / 1399 ms, max queue wait 1.1 s / 0.87 s

## Failures / Blockers
- Customer build still ships CabinetNC.Domain.dll containing the nesting/CAM/post algorithms (FAIL on "no core compute engine"); requires Domain.Model/Domain.Compute split + Task 11 migrations
- Second-LAN-machine HTTPS/latency check not run (single machine available)
- CAM/Post migration (Task 11) not started; obfuscation not applied

## Readiness
- Intranet PoC: PASS for the Nest path (auth, tenancy, async jobs, idempotency, lease recovery, hashes, diagnostics, performance); FAIL for customer-package code protection — overall: PASS WITH ONE FAILED GATE (customer build)
- Public cloud staging: NOT READY (no TLS termination beyond intranet CA, no backup/restore drill, no multi-worker soak, no public-cloud identity/secret management)
- Real CNC validation: NOT RUN (not authorized; out of PoC scope)
```

## 3b. Phase 2 更新（商业可用阶段，见 `COMMERCIAL_READINESS_PLAN.md`）

| 日期 | 项 | 变化 |
|---|---|---|
| 2026-09-06 | P2-1 管理 API | 新增 gate G1：管理员建号/停用/重置/吊销设备、操作员改密——**PASS**（`AdminUserTests` 8/8） |
| 2026-09-06 | P2-2 Nest 契约 v2 | "Nest · Local/server parity" 由 **PASS（限矩形契约）** 升级为 **PASS（真形）**：服务器与本机运行同一 `NestEngineRouter`，`NestingRunnerV2Tests` 在 blf/nfp 下逐字段相等；Desktop 内网模式不再有 `intranet_contract` 降级 |
| 2026-09-08 | P2-3/P2-4 刀路与 NC 上云 | 新增 gate G3：服务器 ops 与 NC 与本机逐字节相同（`CamRunnersTests`、API 端到端）；Desktop 内网模式刀路/NC/导出全部走服务器（UI smoke 06）——**PASS** |
| 2026-09-08 | P2-5 Domain 拆分 | "Customer build · No core compute engine" 由 **FAIL** 变为 **PASS**：`CabinetNC.Domain.Compute.dll` 不在客户包内，任何已发布程序集中无算法类型名，严格验证 `RESULT: PASS`；客户版 UI smoke 07 全流程经服务器通过 |
| 2026-09-08 | P2-6 运维包 | 新增 gate G5：就绪探测、双 worker 接管、备份→全毁→恢复演练——**PASS**（Prometheus 指标待做） |
| 2026-09-06 | P2-1 管理 API | 新增 gate G1——**PASS** |

| 2026-09-08 | P2-7 混淆基线 | Obfuscar：worker 镜像全符号重命名 + 字符串隐藏，客户包私有成员重命名 + 字符串隐藏；ilspycmd 探针混淆前 10/10 命中 → 混淆后 0；混淆后整栈 UI smoke（开发版 + 客户版）通过——**PASS（基线）**；控制流混淆/反篡改 NOT_RUN（需商业工具或 Native AOT） |

**Phase 2 之后矩阵中不再有 FAIL 项**；仍为 NOT_RUN 的：第二台 LAN 机器验证、商业级混淆（控制流/反篡改）、真实 CNC。

## 4. 声明

以上全部来自自动化测试与本机（机器 B）实测；**不因此宣称 Production Ready**。进入客户现场前至少还需要：第二台机器的 HTTPS 验证、备份/恢复演练、双 worker 长时间运行、Domain 拆分后的客户包严格验证、以及 Task 11 的 CAM/Post 迁移与 golden 对比。

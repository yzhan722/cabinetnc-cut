# CabinetNC Intranet Cloud & Client Protection Design

**Date:** 2026-09-05  
**Repo:** `yzhan722/cabinetnc-cut`  
**Baseline:** `sprint/14d-rc @ 5e410d554e17ba77d2dbb8deb3ff1967154d0c67`

## 1. 目标

把当前 `Desktop + local ComputeWorker` 演进成可验证的 `Desktop + Intranet Cloud API + Server Compute Worker`，支撑三个商业化要求：

1. 客户正式发行版不携带可复用的 Nest/CAM/Post 核心制造算法。
2. 客户关键计算通过后台执行、返回，并形成完整 Job 日志。
3. “动态密码”采用标准短时效认证：15 分钟 Access Token + 30 天 rotating Refresh Token。

本阶段仅做 **Intranet PoC**，不是公网 Production。

## 2. 当前必须保留的架构边界

当前源码已经具备正确的分层方向：

```text
CabinetNC.Desktop (WPF)
  -> gRPC / Named Pipe
CabinetNC.ComputeWorker
  -> CabinetNC.Domain
  -> Nest / Ops / Post
```

已有 `dotnet/protos/worker.proto` 定义 `WorkerHealth`、`Nesting`、`Operations`、`PostProcessor`；现有 Desktop 的 `WorkerProcessHost` 负责启动本地 worker 并建立 Named Pipe gRPC。

PoC 不删除本地链路，而是增加：

```text
ComputeMode.Local
ComputeMode.Intranet
```

用来做结果一致性与回归测试。

## 3. 目标架构

```text
Windows Desktop
   | HTTPS + Bearer Token
   v
CabinetNC.Cloud.Api
   |-- Auth / Device / Token
   |-- Job submit/status/result
   |-- Audit
   |
   +--> PostgreSQL
   |      tenants/users/devices/refresh_tokens/compute_jobs/audit_events
   |
   +--> MinIO (S3-compatible)
          job input/result JSON

CabinetNC.Cloud.Worker
   | claim queued job
   v
CabinetNC.Compute.Core
   v
CabinetNC.Domain Nesting
```

PoC 队列采用 PostgreSQL `compute_jobs` + `FOR UPDATE SKIP LOCKED`，不额外引入 RabbitMQ/Kafka。后续通过 `IJobRepository/IJobQueue` 抽象迁移 Azure Service Bus。

## 4. 内网服务器基准

```text
OS:      Ubuntu Server 24.04 LTS
CPU:     8 vCPU minimum
RAM:     32 GB
Disk:    500 GB NVMe
Network: 1 Gbps LAN
GPU:     none
```

Docker Compose：

```text
postgres
minio
cabinetnc-api
cabinetnc-worker
reverse-proxy
```

## 5. 新项目边界

### `CabinetNC.Compute.Core`

Transport-neutral compute orchestration。允许引用 `CabinetNC.Domain`；禁止引用 WPF、ASP.NET Controller、gRPC `ServerCallContext`、PostgreSQL、MinIO。

核心接口：

```csharp
public interface INestingRunner
{
    NestingOutput Run(NestingInput input);
}
```

本地 gRPC Worker 和 Cloud Worker 必须调用同一个 runner，禁止复制两份 Nest 逻辑。

### `CabinetNC.Cloud.Contracts`

Desktop ↔ Cloud API DTO。不能包含 DB entity，也不能依赖 WPF。

### `CabinetNC.Cloud.Api`

负责认证、设备、Token、Job submission/status/result、audit、health。禁止直接执行长 Nest。

### `CabinetNC.Cloud.Infrastructure`

负责 PostgreSQL、MinIO、Job leasing、持久化。

### `CabinetNC.Cloud.Worker`

Claim Job -> load input -> `INestingRunner` -> store result -> update status/audit。

## 6. 动态认证设计

```text
Password login       -> initial auth only
Access Token TTL     -> 15 minutes
Refresh Token TTL    -> 30 days
Refresh rotation     -> every successful refresh
Clock skew           -> <= 60 seconds
```

JWT claims：

```text
sub
tenant_id
device_id
role
jti
```

Refresh Token：

- 使用 cryptographic RNG；
- 客户端只收到明文一次；
- DB 只存 SHA-256 hash；
- 每次 refresh 都 revoke old token 并发新 token；
- revoked token 再次使用视为 reuse，撤销同 device/token family 的 active refresh tokens。

Windows Desktop：

- 不保存 password；
- Access Token 只放内存；
- Refresh Token 使用 DPAPI CurrentUser 加密后保存；
- DeviceId 首次启动生成 GUID 并持久化；
- 不从 MAC address 派生；
- 不内置永久 client secret。

## 7. 多租户与授权

PoC 第一版就要求：

```text
TenantId
UserId
DeviceId
JobId
CorrelationId
```

TenantId 必须来自 verified token claims，不能信任 Desktop 任意传入的 TenantId。

## 8. Job 数据模型

状态仅允许：

```text
Queued
Running
Succeeded
Failed
```

关键字段：

```text
Id
TenantId
UserId
DeviceId
JobType
Status
IdempotencyKey
CorrelationId
InputObjectKey
InputSha256
ResultObjectKey
ResultSha256
EngineVersion
AttemptCount
LockedBy
LockedUntilUtc
CreatedAtUtc
StartedAtUtc
CompletedAtUtc
ErrorCode
ErrorMessage
DurationMs
```

`POST /api/v1/jobs/nest` 必须要求 `Idempotency-Key`。同 `TenantId + UserId + IdempotencyKey` 重复提交返回同一 JobId。

## 9. Nest Cloud Contract

Request 至少包含：

```text
parts(panelId,width,height,mayRotate,material,thickness)
sheetWidthMm
sheetLengthMm
spacingMm
borderMm
allowRotation
```

Result：

```text
engine
engineVersion
placements
sheetCount
unplaced
warnings
inputSha256
resultSha256
durationMs
```

Local/Server parity：确定性时 exact equality；若算法合法非确定，则至少验证 sheet count、placed/unplaced set、collision-free、material/thickness constraints、rotation constraints。

## 10. Object Storage

PoC 用 MinIO，接口 provider-neutral：

```csharp
public interface IObjectStore
{
    Task PutAsync(string key, Stream data, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
}
```

Key：

```text
tenant/{tenantId}/jobs/{jobId}/input.json
tenant/{tenantId}/jobs/{jobId}/result.json
```

数据库只放 metadata/hash/object key，不长期塞大型 JSON/NC/DXF/BMP。

## 11. Logging / Audit

Runtime logs(JSON stdout)：

```text
timestamp level service correlationId jobId message exception
```

Audit DB 至少记录：

```text
auth.login.success
auth.login.failed
auth.refresh.success
auth.refresh.failed
auth.logout
job.submitted
job.claimed
job.started
job.succeeded
job.failed
job.retry
```

绝对禁止日志记录 password、access token、refresh token、signing key、DB password。

## 12. API 错误码

统一格式：

```json
{"code":"job_not_found","message":"Job was not found.","correlationId":"..."}
```

至少支持：

```text
invalid_credentials
invalid_device
token_expired
refresh_invalid
refresh_reuse_detected
unauthorized
invalid_request
idempotency_conflict
job_not_found
job_not_ready
compute_failed
storage_failed
```

## 13. 网络与失败行为

Desktop：

```text
connect timeout: 5s
normal API timeout: 15s
job compute: async
poll: 1s -> 2s -> max 5s
```

必须是 `submit -> JobId -> poll -> fetch result`，禁止 HTTP request 挂几分钟等 Nest。

Worker lease：

- claim 时 `Running + LockedUntilUtc + AttemptCount++`；
- worker crash 后 lease 到期可 reclaim；
- retryable error 最多 3 attempts；
- storage write 失败时不得标记 Succeeded。

## 14. 客户端代码保护

PoC 保留 Local mode，仅用于 A/B。

正式 Customer build 目标：

```text
Desktop.exe
NO local ComputeWorker.exe
NO CabinetNC.Compute.Core / Nest/CAM/Post core assembly
NO embedded server secrets
NO unapproved PDB/source
```

之后再做 .NET/WPF 兼容的混淆与 anti-tamper。成功标准不是“绝对不可逆向”，而是客户安装包不含可复用核心制造算法，且剩余 client code 反编译价值显著降低。

## 15. CAM/Post 迁移顺序

只在 Nest Cloud PoC 通过后：

```text
Nesting -> Operations/CAM -> Preflight -> PostProcessor/NC
```

每迁一个模块必须先 Local characterization，再抽 transport-neutral runner，再 cloud job，再 parity/golden regression。

## 16. Acceptance Gates

A. 现有 .NET regression 通过，Windows UI smoke 不退化。  
B. Auth：15min access、rotating refresh、reuse blocked、tenant isolation。  
C. Nest：submit/status/result + Local/Server parity。  
D. Reliability：API restart、worker lease recovery、duplicate submit、network reconnect。  
E. Observability：只凭 JobId 可查 user/device/time/hash/engine/duration/result/correlation。  
F. Performance：50/100/300/500 panels 记录真实 CPU/RAM/queue/compute/end-to-end 数据。

## 17. Out of scope

Billing、public signup、customer portal、Azure production、Kubernetes、GPU、multi-region、真实 CNC execution 都不属于本 PoC。

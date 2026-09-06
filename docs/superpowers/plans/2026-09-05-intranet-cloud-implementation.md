# CabinetNC Intranet Cloud Implementation Plan

> **For agentic workers:** Execute task-by-task. Use a fresh review checkpoint after every task. Tests first where behavior changes.

**Goal:** Build an intranet-hosted backend that authenticates users/devices, issues rotating short-lived credentials, runs Nesting asynchronously on the server, records auditable Jobs, and gives Desktop an explicit Local/Intranet compute switch.

**Spec:** `docs/superpowers/specs/2026-09-05-intranet-cloud-design.md`

## Global constraints

- Base from `sprint/14d-rc`; plan baseline commit is `5e410d554e17ba77d2dbb8deb3ff1967154d0c67`.
- Work in `feature/intranet-cloud-poc`.
- Preserve manufacturing semantics.
- Preserve Local mode until parity is demonstrated.
- No CNC execution, no real customer data unless separately approved.
- No secrets in Git.
- No Kubernetes/GPU.
- Do not rewrite all of `MainWindow.xaml.cs`.
- Every task ends with tests + commit + implementation note.

---

## Task 1 — Baseline and branch

**Create:** `docs/cloud/IMPLEMENTATION_NOTES.md`, `docs/cloud/INTRANET_POC_RUNBOOK.md`

1. Run:

```bash
git status --short
git branch --show-current
git rev-parse HEAD
git log -5 --oneline
```

2. If RC has moved, record actual HEAD and inspect all touched files; do not reset.
3. Create/switch `feature/intranet-cloud-poc`.
4. Locate current compute call sites:

```bash
rg -n "WorkerProcessHost|GetNestingClient|StartNesting|GenerateOperations|GenerateNc" dotnet/src
```

5. Baseline:

```bash
dotnet test dotnet/CabinetNC.slnx -c Release
```

6. On a Windows-capable machine:

```powershell
pwsh dotnet/tests/ui-smoke/run-all.ps1
```

If environment cannot run WPF, record `BLOCKED_ENVIRONMENT`, not PASS.
7. Commit: `docs: establish intranet cloud poc baseline`.

**Gate:** No unexplained baseline failures.

---

## Task 2 — Extract shared Nesting runner

**Create:**

```text
dotnet/src/CabinetNC.Compute.Core/CabinetNC.Compute.Core.csproj
dotnet/src/CabinetNC.Compute.Core/Nesting/NestingInput.cs
dotnet/src/CabinetNC.Compute.Core/Nesting/NestingOutput.cs
dotnet/src/CabinetNC.Compute.Core/Nesting/INestingRunner.cs
dotnet/src/CabinetNC.Compute.Core/Nesting/NestingRunner.cs
dotnet/tests/CabinetNC.Compute.Core.Tests/*
```

**Modify:**

```text
dotnet/CabinetNC.slnx
dotnet/src/CabinetNC.ComputeWorker/CabinetNC.ComputeWorker.csproj
dotnet/src/CabinetNC.ComputeWorker/Services/NestingServiceImpl.cs
dotnet/src/CabinetNC.ComputeWorker/Program.cs
```

### Interfaces

```csharp
public sealed record NestingPartInput(string PanelId,double WidthMm,double HeightMm,bool MayRotate,string? Material,double ThicknessMm);
public sealed record NestingInput(IReadOnlyList<NestingPartInput> Parts,double SheetWidthMm,double SheetLengthMm,double SpacingMm,double BorderMm,bool AllowRotation);
public sealed record NestingPlacementOutput(string PanelId,int SheetIndex,double OffsetX,double OffsetY,double RotationDeg);
public sealed record NestingWarningOutput(string Code,string Message,string? PanelIdA,string? PanelIdB,int? SheetIndex);
public sealed record NestingOutput(bool Ok,string Engine,IReadOnlyList<NestingPlacementOutput> Placements,int SheetCount,IReadOnlyList<string> Unplaced,IReadOnlyList<NestingWarningOutput> Warnings,string? Error);
public interface INestingRunner { NestingOutput Run(NestingInput input); }
```

### Tests first

Add tests:

```text
Run_places_two_rectangles_without_overlap
Run_honors_no_rotation_part
Run_returns_unplaced_for_oversized_part
Run_preserves_material_and_thickness_behavior
Run_is_deterministic_for_same_input
```

### Implementation rules

Move orchestration only from current `NestingServiceImpl` into `NestingRunner`; keep current defaults and current `NestEngineRouter/GroupedBlfNester/NestValidator`. Do not reimplement the algorithm.

Local gRPC becomes adapter:

```text
proto request -> NestingInput -> INestingRunner.Run -> proto reply
```

Register:

```csharp
builder.Services.AddSingleton<INestingRunner, NestingRunner>();
```

Run focused + full tests. Commit: `refactor: extract transport-neutral nesting runner`.

**Gate:** Existing local gRPC Nest behavior is unchanged.

---

## Task 3 — Cloud contracts

**Create:**

```text
dotnet/src/CabinetNC.Cloud.Contracts/
dotnet/tests/CabinetNC.Cloud.Contracts.Tests/
```

Required DTOs:

```text
LoginRequest/LoginResponse
RefreshRequest/RefreshResponse
SubmitNestJobRequest/SubmitNestJobResponse
JobStatusResponse
NestJobResult
ApiError
```

Required API semantics:

```http
POST /api/v1/auth/login
POST /api/v1/auth/refresh
POST /api/v1/auth/logout
POST /api/v1/jobs/nest
GET  /api/v1/jobs/{jobId}
GET  /api/v1/jobs/{jobId}/result
GET  /api/v1/health
```

Write JSON round-trip tests and pin camelCase JSON names. Add project(s) to `CabinetNC.slnx`.

Commit: `feat: add cloud api contracts`.

---

## Task 4 — PostgreSQL persistence + Job leasing

**Create:** `CabinetNC.Cloud.Infrastructure` and tests.

Entities:

```text
TenantEntity
UserEntity
DeviceEntity
RefreshTokenEntity
ComputeJobEntity
AuditEventEntity
```

Unique constraints:

```text
User: TenantId + Email
Device: TenantId + DeviceKey
Job: TenantId + UserId + IdempotencyKey
RefreshToken: TokenHash
```

`ComputeJobEntity` includes all fields from the design, especially `AttemptCount`, `LockedBy`, `LockedUntilUtc`, hashes, engine version, timestamps, error code/message.

### Job repository

```csharp
public interface IJobRepository
{
    Task<ComputeJobEntity> CreateOrGetByIdempotencyKeyAsync(...);
    Task<JobLease?> TryClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct);
    Task MarkSucceededAsync(...);
    Task MarkFailedAsync(...);
    Task<ComputeJobEntity?> GetAsync(Guid jobId, Guid tenantId, CancellationToken ct);
}
```

Implement claim using PostgreSQL transaction + `FOR UPDATE SKIP LOCKED`.

Claim atomically:

```text
Queued/expired Running -> Running
AttemptCount++
LockedBy=worker
LockedUntilUtc=now+lease
```

Retry: max 3 attempts; non-retryable validation failures fail immediately.

### Integration tests

Must use real PostgreSQL, not EF InMemory, for locking/idempotency:

```text
duplicate idempotency key -> same logical job
same key across tenants -> allowed
two simultaneous workers -> only one claims same job
expired lease -> reclaimable
third retry -> Failed
```

Create initial migration after API startup project exists. Commit: `feat: add cloud persistence and job leasing`.

---

## Task 5 — MinIO object store

Add provider-neutral:

```csharp
public interface IObjectStore
{
    Task PutAsync(string key, Stream data, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
}
```

PoC implementation uses S3-compatible MinIO.

Reject unsafe keys: `../`, leading `/`, backslash, empty key.

Integration test exact byte round trip for:

```text
tenant/{tenantId}/jobs/{jobId}/input.json
```

Commit: `feat: add intranet object storage`.

---

## Task 6 — Cloud API shell, correlation, auth, dynamic Token

**Create:** `CabinetNC.Cloud.Api` + tests.

### Health/correlation

`GET /api/v1/health` returns 200 JSON and every response contains `X-Correlation-ID`.

### Bootstrap admin

Only from environment:

```text
CABINETNC_BOOTSTRAP_TENANT
CABINETNC_BOOTSTRAP_ADMIN_EMAIL
CABINETNC_BOOTSTRAP_ADMIN_PASSWORD
CABINETNC_JWT_SIGNING_KEY
```

No default real credentials. Never log password.

### Auth

Access token = 15 min. Refresh = 30 days. Refresh token is cryptographically random, DB stores only SHA-256 hash. Every refresh rotates. Reuse of revoked token => `refresh_reuse_detected` and revoke same device/token family.

JWT claims: `sub`, `tenant_id`, `device_id`, `role`, `jti`.

Rate limit at least:

```text
login: 10/min/IP
refresh: 30/min/IP
```

### Auth tests

```text
login success
wrong password
same device not duplicated
access TTL approx 15m
refresh rotates
old refresh reuse blocked
logout revokes
required JWT claims present
raw refresh token absent from DB
```

Commit: `feat: add rotating cloud authentication`.

---

## Task 7 — Nest Job API + Cloud Worker

### API

`POST /api/v1/jobs/nest` requires auth and `Idempotency-Key`.

Validation must reject zero parts, empty/duplicate PanelId, non-positive dimensions/sheet, negative spacing/border, oversized request.

Flow:

```text
validate
-> derive tenant/user/device from token
-> create/get JobId by idempotency
-> canonical serialize input
-> SHA256 stored bytes
-> save input to MinIO
-> mark safely Queued
-> audit job.submitted
-> return 202 JobId
```

Status endpoint enforces tenant isolation. Result endpoint returns `job_not_ready` until Succeeded.

### Worker

Create `CabinetNC.Cloud.Worker` with testable:

```csharp
Task<bool> ExecuteOneAsync(CancellationToken ct)
```

Flow:

```text
claim job
-> audit started
-> load input
-> map to NestingInput
-> INestingRunner.Run
-> measure duration
-> serialize result
-> SHA256 result bytes
-> save MinIO result
-> only then MarkSucceeded
```

Failure policy follows Task 4. Public error never returns stack trace.

### Required tests

```text
unauth submit -> 401
missing idempotency -> 400
duplicate submit -> same JobId
Tenant A cannot read Tenant B
worker executes queued synthetic Nest
result object/hash exists
engine version/duration recorded
storage failure never marks Succeeded
```

Commit API and Worker as separate commits if diff becomes large.

---

## Task 8 — Docker Compose intranet stack

**Create:**

```text
deploy/intranet/docker-compose.yml
deploy/intranet/.env.example
deploy/intranet/reverse proxy config
Cloud.Api Dockerfile
Cloud.Worker Dockerfile
```

Services:

```text
postgres
minio
cabinetnc-api
cabinetnc-worker
reverse-proxy
```

Requirements:

- persistent volumes for postgres/minio;
- health checks;
- API exposed by HTTPS reverse proxy;
- DB/MinIO not unnecessarily exposed to LAN;
- `.env.example` only dummy names/placeholders;
- internal CA/certificate trust documented in runbook;
- do not globally disable TLS validation.

Run:

```bash
docker compose -f deploy/intranet/docker-compose.yml up -d --build
docker compose -f deploy/intranet/docker-compose.yml ps
```

From a second LAN machine verify HTTPS `/api/v1/health` and record latency/cert result.

Commit: `build: add intranet cloud compose stack`.

---

## Task 9 — Desktop login, DPAPI, remote Nest gateway

### Secure local state

Create:

```text
CloudClientOptions
DeviceIdentityStore
WindowsTokenStore
AuthSession
AuthenticatedHttpHandler
CloudApiClient
```

`ComputeMode`:

```csharp
public enum ComputeMode { Local, Intranet }
```

Refresh Token uses Windows DPAPI CurrentUser. Access Token in memory. DeviceId is persistent GUID.

### Auto refresh

- proactively refresh if access expires within 60 sec;
- on 401 token_expired refresh once and retry original request once;
- concurrent 401s must single-flight through one refresh (`SemaphoreSlim` or equivalent);
- refresh failure clears session and requires login; no infinite loop.

### Compute Gateway

Create:

```csharp
public interface IComputeGateway
{
    Task<NestJobResult> RunNestingAsync(SubmitNestJobRequest request, CancellationToken ct);
}
```

Implement:

```text
LocalComputeGateway -> existing WorkerProcessHost/gRPC
IntranetComputeGateway -> submit JobId -> poll -> result
ComputeGatewayFactory -> explicit mode
```

Before editing UI, locate exact call sites:

```bash
rg -n "GetNestingClient|StartNestingAsync|StartNesting" dotnet/src/CabinetNC.Desktop
```

Modify only the Nest call path, not whole MainWindow.

Never silently fall back `Intranet -> Local` after server failure.

Run Local UI smoke and add Intranet smoke: login -> synthetic nest -> succeeded -> render result.

Commit: `feat: add desktop intranet compute mode`.

---

## Task 10 — Reliability, diagnostics, performance

### Diagnostics

Admin-only endpoint:

```http
GET /api/v1/admin/jobs/{jobId}/diagnostics
```

Return JobId, tenant/user/device, correlationId, state, hashes, engine version, attempt count, timestamps, duration, error, audit events. Never return token/password hashes.

### E2E failure suite

Automate:

```text
duplicate submission -> one job
worker crash after claim -> lease recovery
API restart -> job persists
MinIO unavailable -> not Succeeded, retry/fail correctly
token expiry during polling -> auto refresh
cross-tenant read -> blocked
client disconnect after JobId -> reconnect same JobId
```

### Performance harness

Create deterministic synthetic 50/100/300/500 panel cases. Do not label as real shop jobs.

Record 5 runs each plus 2 and 5 concurrent jobs:

```text
queue wait ms
compute ms
end-to-end ms
worker CPU
worker peak working set
sheet count
status
```

Write observed results only to `docs/cloud/PERFORMANCE_RESULTS.md`; never invent numbers.

Use these measurements to recommend final cloud worker size.

Commit: `test: add intranet reliability and performance validation`.

---

## Task 11 — Migrate Operations/CAM, then Post

Do not begin until Nest acceptance is PASS.

### Operations

Follow exact pattern:

```text
characterization tests
-> extract `IOperationsRunner` into Compute.Core
-> local gRPC becomes adapter
-> local regression
-> Cloud JobType Operations
-> server execution
-> Local/Server parity
```

Validate operation count, panel/feature association, sheet coordinates, drill/contour semantics.

### Post

Then:

```text
characterization/golden NC
-> extract `IPostProcessorRunner`
-> local adapter
-> cloud post job
-> local/server normalized golden comparison
```

Do not normalize away meaningful NC differences just to pass.

Each migration is an independent commit/review gate.

---

## Task 12 — Customer build and code protection

### Customer package rule

Create `dotnet/scripts/verify-customer-package.ps1` and `docs/security/CLIENT_CODE_PROTECTION.md`.

Customer package check must fail if it contains deployable core compute components such as:

```text
CabinetNC.ComputeWorker.exe
CabinetNC.Compute.Core.dll / equivalent core compute assembly
server secrets
unapproved PDB/source
```

Customer mode must successfully Nest only through Intranet/Cloud.

### Obfuscation evaluation

Do not blindly add an obsolete obfuscator. Evaluate maintained .NET 10/WPF-compatible candidate(s), documenting version/license/WPF reflection/XAML caveats.

For each protected build:

```text
full .NET tests
Windows UI smoke
customer package verification
ordinary decompiler/reverse-engineering acceptance
```

Acceptance is not “unhackable”. It is:

```text
no core manufacturing engine in client
no embedded secrets
remaining client code materially harder to reuse
protected build stays functional
```

If candidate breaks WPF/runtime, record FAIL and do not sacrifice correctness.

---

## Task 13 — Final acceptance document

Create `docs/cloud/INTRANET_POC_ACCEPTANCE.md` with this matrix:

| Gate | Check | Result | Evidence |
|---|---|---|---|
| Baseline | Full .NET regression |  |  |
| UI | Local UI smoke |  |  |
| Auth | Login |  |  |
| Auth | 15 min access token |  |  |
| Auth | Refresh rotation/reuse protection |  |  |
| Tenant | Cross-tenant blocked |  |  |
| Job | Async submit/status/result |  |  |
| Job | Idempotency |  |  |
| Worker | Crash recovery |  |  |
| Storage | Input/result hash |  |  |
| Nest | Local/server parity |  |  |
| Perf | 50/100/300/500 |  |  |
| Logs | Diagnose from JobId |  |  |
| Customer build | No core compute engine |  |  |
| Security | No embedded secrets |  |  |

Allowed result: `PASS / FAIL / BLOCKED / NOT_RUN`。不得把 BLOCKED 写成 PASS。

### Cursor final report format

```markdown
# Intranet Cloud PoC Result

## Baseline
- Branch:
- Start commit:
- End commit:

## Implemented
- Auth:
- Token rotation:
- Device identity:
- Job queue:
- Nest cloud compute:
- Logs/audit:
- Desktop remote mode:
- CAM:
- Post:
- Customer package protection:

## Tests
- .NET:
- UI smoke:
- Cloud E2E:
- Local/cloud parity:
- Performance:

## Server measurements
- CPU/RAM:
- 50 panels:
- 100 panels:
- 300 panels:
- 500 panels:
- 2 concurrent:
- 5 concurrent:

## Failures / Blockers
- ...

## Readiness
- Intranet PoC: PASS/FAIL/BLOCKED
- Public cloud staging: READY/NOT READY
- Real CNC validation: NOT RUN unless explicitly authorized
```

Do not claim Production Ready solely from automated software tests.

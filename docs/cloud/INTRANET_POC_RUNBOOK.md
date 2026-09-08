# Intranet PoC Runbook

面向“接手的人 / 内网服务器运维”的操作手册。随任务推进追加；每节标注状态：`READY`（已验证可用）/ `DRAFT`（写了但未验证）/ `TODO`（等对应 Task 完成）。

Spec: `docs/superpowers/specs/2026-09-05-intranet-cloud-design.md`
Plan: `docs/superpowers/plans/2026-09-05-intranet-cloud-implementation.md`

---

## 0. 开发机准备 — READY

必需：

| 组件 | 要求 | 备注 |
|---|---|---|
| Windows 10/11 | Desktop 是 WPF (`net10.0-windows`) | UI smoke 只能在 Windows 交互式桌面会话跑 |
| .NET SDK | 10.0.x（已验证 10.0.302） | `dotnet --version` |
| git | 任意近期版本 | |
| Docker Desktop / Docker Engine | Task 4/5/8 起需要（PostgreSQL、MinIO、compose） | Windows 上用 Docker Desktop + WSL 2。要求 Windows 10 22H2 (19045)+ 且 WSL ≥ 2.1.5：先 `wsl --version`，打出帮助文本说明是旧版 inbox WSL，跑 `wsl --update`（不需要管理员）。推荐**按用户模式**安装，不需要 UAC：`"Docker Desktop Installer.exe" install --user --quiet --accept-license --backend=wsl-2 [--installation-dir=D:\Docker\Program --wsl-default-data-root=D:\Docker\wsl]`，装完手动启动一次 Docker Desktop，`docker info` 有输出即可 |
| PowerShell | Windows PowerShell 5.1 即可；有 pwsh 7 更好 | 没有 pwsh 时把计划里的 `pwsh xxx.ps1` 换成 `powershell -NoProfile -ExecutionPolicy Bypass -File xxx.ps1` |
| ripgrep (`rg`) | 可选，计划里的搜索命令用到 | 没有就用 IDE 搜索 |

GitHub 权限：`yzhan722/cabinetnc-cut` 对当前执行账号是 pull-only。要推送请先 fork，或让维护者添加写权限。

## 1. 取得代码并切到工作分支 — READY

从 GitHub：

```powershell
git clone --branch sprint/14d-rc https://github.com/yzhan722/cabinetnc-cut.git cabinetnc-cut
cd cabinetnc-cut
git checkout feature/intranet-cloud-poc   # 若该分支尚未推到远端，则用下面的 bundle 方式
```

从交接包里的 git bundle（包含本地尚未推送的 `feature/intranet-cloud-poc`）：

```powershell
git clone <解压目录>\cabinetnc-cut.bundle cabinetnc-cut
cd cabinetnc-cut
git checkout feature/intranet-cloud-poc
git remote set-url origin https://github.com/yzhan722/cabinetnc-cut.git
git fetch origin
```

核对基线：

```powershell
git rev-parse sprint/14d-rc     # 期望 5e410d554e17ba77d2dbb8deb3ff1967154d0c67
git log --oneline -3
```

若 `origin/sprint/14d-rc` 已经前进：**不得 reset / force-push**。先 `git log 5e410d5..origin/sprint/14d-rc --stat` 看动了哪些文件，把结论写进 `docs/cloud/IMPLEMENTATION_NOTES.md`，再决定是否 rebase 工作分支。

## 2. 构建与回归 — READY

```powershell
# 全量 .NET 回归（Release）。基线 554 → Task 4 后 589 tests / 0 failed
dotnet test dotnet/CabinetNC.slnx -c Release --verbosity minimal

# CabinetNC.Cloud.Infrastructure.Tests 需要真实 PostgreSQL 和 MinIO：
#   - 默认由 Testcontainers 自动起 postgres:17-alpine 与 minio/minio:latest（需要 Docker 在跑，且是 Linux 容器模式）
#   - 或者指定现成的服务：
#       $env:CABINETNC_TEST_PG = "Host=...;Database=cabinetnc_test;Username=...;Password=..."   （表会被 TRUNCATE，只能指向测试库）
#       $env:CABINETNC_TEST_MINIO_ENDPOINT = "host:9000"; $env:CABINETNC_TEST_MINIO_ACCESS_KEY = ...; $env:CABINETNC_TEST_MINIO_SECRET_KEY = ...
#       （每个测试类会新建一个 cabinetnc-test-* bucket，不会清理）
#   - 都没有 → 这些用例显式 SKIP 并打印原因，不算 PASS

# UI smoke 需要 Release 版 Desktop + Worker
dotnet build dotnet/src/CabinetNC.ComputeWorker/CabinetNC.ComputeWorker.csproj -c Release
dotnet build dotnet/src/CabinetNC.Desktop/CabinetNC.Desktop.csproj -c Release

# UI smoke（必须在交互式桌面会话里跑，远程/服务型会话截图会失败）
powershell -NoProfile -ExecutionPolicy Bypass -File dotnet\tests\ui-smoke\run-all.ps1
# 结果：dotnet/artifacts/ui-smoke/results.json + 截图；退出码 = 失败场景数
```

已知现象：在非交互 shell（例如 agent 驱动的终端）里 `shot:` 截图步骤可能抛 `CopyFromScreen 句柄无效`，功能步骤不受影响。看到这种失败先在真正的桌面会话重跑，再判断是否是产品问题。

## 3. 本地 Compute Worker（Local mode）— READY

Desktop 启动时会自动拉起 `CabinetNC.ComputeWorker.exe`（Named Pipe gRPC，pipe 名见 `CabinetNC.Compute.Contracts/WorkerPipes.cs`）做健康自检。手工冒烟：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File dotnet\scripts\smoke-worker.ps1
```

注意：当前 RC 里 Desktop 的排版计算是在 Desktop 进程内直接调 `NestEngineRouter`，并不经过 gRPC worker（详见 IMPLEMENTATION_NOTES §1.6）。

## 4. 内网服务器（Ubuntu 24.04）— READY（Task 8，机器 B 本机验证；第二台 LAN 机器的验证待运维执行）

基准：8 vCPU / 32 GB / 500 GB NVMe / 1 Gbps / 无 GPU。栈定义在 `deploy/intranet/`：

| 服务 | 镜像 | 对外端口 | 数据 |
|---|---|---|---|
| `reverse-proxy` | `caddy:2-alpine` | **443**（+80 仅跳转 https）——唯一发布到 LAN 的端口 | `caddy-data`（含内部 CA）、`caddy-config` |
| `cabinetnc-api` | 本地构建 `dotnet/src/CabinetNC.Cloud.Api/Dockerfile` | 无（只在 `edge` + `backend` 网络） | 无状态 |
| `cabinetnc-worker` | 本地构建 `dotnet/src/CabinetNC.Cloud.Worker/Dockerfile` | 无（只在 `backend`） | 无状态 |
| `postgres` | `postgres:17-alpine` | 无；`backend` 网络 `internal: true`，连主机外网都不通 | `postgres-data` |
| `minio` | `minio/minio:RELEASE.2025-09-07T16-13-09Z` | 无（console 9001 也不发布，维护用 SSH 隧道） | `minio-data` |

启动顺序由健康检查串起来：postgres/minio healthy → api（跑 migration）healthy → worker、proxy。

### 4.1 服务器准备

```bash
# Ubuntu 24.04：Docker Engine + compose 插件
sudo apt-get update && sudo apt-get install -y docker.io docker-compose-v2 git
sudo usermod -aG docker "$USER"   # 重新登录生效
# 防火墙只放 443（和 80）；5432/9000 不需要、也没有发布
sudo ufw allow 443/tcp && sudo ufw allow 80/tcp
```

给服务器一个 LAN 内可解析的 DNS 名（例如 `cabinetnc.shop.local`），Desktop 用它访问，证书也签给它。

### 4.2 配置与启动

```bash
git clone <repo> cabinetnc-cut && cd cabinetnc-cut/deploy/intranet
cp .env.example .env
# 用随机值替换 .env 里每一个 CHANGE_ME：
openssl rand -base64 48          # POSTGRES_PASSWORD / MINIO_ROOT_PASSWORD / CABINETNC_BOOTSTRAP_ADMIN_PASSWORD
openssl rand -base64 64          # CABINETNC_JWT_SIGNING_KEY（API 拒绝 < 32 bytes）
# CABINETNC_PUBLIC_HOST=cabinetnc.shop.local ；SOURCE_REVISION=$(git rev-parse HEAD) 让 EngineVersion 带上 git 版本

docker compose up -d --build     # 首次约 2–3 分钟（拉基础镜像 + 编译）
docker compose ps                # 5 个服务都应为 (healthy)
```

`.env` 已被 `.gitignore` 忽略（只有 `.env.example` 入库，且全是占位符）。首次启动 API 日志里会有**一条**预期的 EF `Failed executing DbCommand ... __EFMigrationsHistory`——那是 EF 在空库上探测迁移历史表，随后立刻 `Applying migration`；之后的启动不会再出现。

### 4.3 证书信任（内部 CA）— 不允许关闭 TLS 校验

默认 `Caddyfile` 用 `tls internal`：Caddy 首次启动生成自己的根证书（`CN=Caddy Local Authority - <year> ECC Root`，10 年有效）。把它导出并安装到**每台 Desktop**：

```bash
docker compose cp reverse-proxy:/data/caddy/pki/authorities/local/root.crt ./cabinetnc-root.crt
```

```powershell
# Windows Desktop（以管理员身份；或加 -user 只装到当前用户）
certutil -addstore -f Root .\cabinetnc-root.crt
# 验证——显式指定 CA，不用 -k / --insecure：
curl.exe --cacert .\cabinetnc-root.crt --ssl-revoke-best-effort https://cabinetnc.shop.local/api/v1/health
```

`--ssl-revoke-best-effort` 只是让 Windows 的 schannel 在内部 CA **没有 CRL/OCSP** 时跳过吊销查询，链校验仍然生效；不加它会报 `CERT_TRUST_REVOCATION_STATUS_UNKNOWN`。.NET Desktop 的 `HttpClient` 默认不做吊销检查，装好根证书即可。Linux：`sudo cp cabinetnc-root.crt /usr/local/share/ca-certificates/ && sudo update-ca-certificates`。

要改用车间自己 CA 签发的证书：把 `tls.crt` / `tls.key` 放进 `deploy/intranet/certs/`，把 `Caddyfile` 的 `tls internal` 换成 `tls /certs/tls.crt /certs/tls.key`，`docker compose restart reverse-proxy`。

### 4.4 首个管理员与收尾

首次启动带着三个 `CABINETNC_BOOTSTRAP_*` 变量；用它登录成功后，**从 `.env` 删除这三行**，`docker compose up -d`（只会重建 API 容器）。没有这三行时 API 不会创建任何默认账号。

### 4.5 第二台机器验证（运维 TODO）

计划要求从另一台 LAN 机器验证并记录：

```powershell
curl.exe --cacert .\cabinetnc-root.crt --ssl-revoke-best-effort -w "http=%{http_code} tls=%{time_appconnect}s total=%{time_total}s`n" https://cabinetnc.shop.local/api/v1/health
```

记录 http 码、TLS 握手/总耗时、证书主题到 `IMPLEMENTATION_NOTES.md` §8。机器 B 只有一台机器，已记录 127.0.0.1 经 proxy 的数据。

### 4.5b Desktop 侧配置（Task 9）

1. 先按 §4.3 在这台 Windows 上信任内部 CA 根证书（车间 CA 签发的证书则无需此步）。
2. 打开 OmniCam → 「3 密排」→ 「内网登录…」：服务器地址 `https://cabinetnc.shop.local`、租户 slug、邮箱、密码 → 登录。**地址必须是 https**；`http://` 只接受 `127.0.0.1` / `localhost`（开发与 UI smoke）。
3. 「计算位置」选「内网计算」。此后「初始密排 / 重新密排」提交到服务器；状态栏显示 `排队中 / 计算中 … job xxxxxxxx`，完成后 `密排完成 · … · 内网 job xxxxxxxx`，「未排 / 警告」里列出服务器 job id、引擎版本、哈希，以及**矩形契约降级说明**（异形按外接矩形、只用第一种大板、忽略禁排区/按边余量、无 parts-in-part）。
4. 服务器不可用、job 失败或未登录时状态栏报 `内网计算失败 [code]` / `内网计算未登录`，**不会自动改用本机**；要用本机就把「计算位置」切回「本机计算」。
5. 本机状态：`%LocalAppData%\CabinetNC\cloud.json`（模式/地址/租户/邮箱，无密钥）、`cloud.token`（DPAPI 加密的刷新令牌，仅本 Windows 账号可读）、`device-id`（首次随机生成的设备号）。「退出内网登录」会撤销服务端的刷新令牌并删除 `cloud.token`。

UI smoke 的内网场景（`dotnet/tests/ui-smoke/scenarios/06-intranet-nest.txt`）需要一个可达的 API：

```powershell
# 开发机：用叠加文件把 API 发布到 loopback，Desktop 走 http://127.0.0.1:8080
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build
$env:CABINETNC_SMOKE_API_URL = 'http://127.0.0.1:8080'
$env:CABINETNC_SMOKE_TENANT = 'shop'; $env:CABINETNC_SMOKE_EMAIL = 'admin@example.internal'
$env:CABINETNC_SMOKE_PASSWORD = Read-Host 'bootstrap admin password'
pwsh dotnet/tests/ui-smoke/run-all.ps1            # 不设 CABINETNC_SMOKE_API_URL 时 06 场景记为 skipped
```

### 4.5c 用户、设备与密码管理（Phase 2 · P2-1）

全部走 API，无需进数据库；admin 角色调用，作用域是自己的租户；每个操作写审计（`user.created / user.updated / user.password_reset / user.sessions_revoked / device.revoked / user.password_changed`，含操作者）。下面用 bash + `curl`（Windows 用 `curl.exe`，加 `--cacert` 与 `--ssl-revoke-best-effort`）：

```bash
API=https://cabinetnc.shop.local
TOKEN=$(curl -sS "$API/api/v1/auth/login" -H 'Content-Type: application/json' \
  -d "{\"tenant\":\"shop\",\"email\":\"admin@example.internal\",\"password\":\"$ADMIN_PW\",\"deviceId\":\"$(uuidgen)\",\"deviceName\":\"admin-cli\"}" | jq -r .accessToken)
AUTH="Authorization: Bearer $TOKEN"

# 新建操作员（密码 ≥ 12 字符，不能等于邮箱；角色 admin | operator）
curl -sS "$API/api/v1/admin/users" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"email":"op1@shop.local","password":"<initial-password>","role":"operator"}'
# 列出用户（含设备数、活动会话数、最后活动时间）
curl -sS "$API/api/v1/admin/users" -H "$AUTH"
# 停用（立即撤销其全部会话；不能停用/降级最后一个活跃 admin → 409 conflict）/ 启用 / 改角色
curl -sS -X PATCH "$API/api/v1/admin/users/<userId>" -H "$AUTH" -H 'Content-Type: application/json' -d '{"isActive":false}'
curl -sS -X PATCH "$API/api/v1/admin/users/<userId>" -H "$AUTH" -H 'Content-Type: application/json' -d '{"role":"admin","isActive":true}'
# 重置密码（撤销该用户所有设备的会话，下次登录用新密码）
curl -sS "$API/api/v1/admin/users/<userId>/password" -H "$AUTH" -H 'Content-Type: application/json' -d '{"newPassword":"<new-password>"}'
# 设备：列出（可按 ?userId= 过滤）；吊销丢失电脑的会话（该电脑仍可用密码重新登录——吊销的是会话不是账号）
curl -sS "$API/api/v1/admin/devices?userId=<userId>" -H "$AUTH"
curl -sS -X POST "$API/api/v1/admin/devices/<deviceId>/revoke" -H "$AUTH"
curl -sS -X POST "$API/api/v1/admin/users/<userId>/revoke" -H "$AUTH"      # 该用户全部设备
```

操作员本人改密码（任何已登录用户；当前设备保留会话，其他设备被登出；与登录同样限流）：

```bash
curl -sS "$API/api/v1/auth/password" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"currentPassword":"<old>","newPassword":"<new>"}'
```

### 4.6 日常运维

```bash
docker compose logs -f cabinetnc-api cabinetnc-worker cabinetnc-worker-2   # JSON 行日志，含 correlationId / jobId；每服务 5×50 MB 轮转
curl -sS https://cabinetnc.shop.local/api/v1/health/ready                  # 就绪：DB/MinIO 真探测 + 队列深度；不就绪返回 503
./backup.sh ./backups                                                      # 备份：pg_dump（custom 格式）+ MinIO 卷 tar + manifest
./restore.sh ./backups/cabinetnc-<UTC 时间戳>                              # 恢复（破坏性：停 api/worker → 重建 schema → pg_restore → 替换 MinIO 数据 → 启动）
git pull && docker compose up -d --build                                   # 升级：API 启动时自动迁移；回滚 = checkout 旧版本 + 同一命令 + 必要时 restore.sh
```

默认跑 **2 个 worker**（`cabinetnc-worker` / `cabinetnc-worker-2`，ID 不同，轮询 0.2 s）：一个挂掉，另一个在租约到期后接管其 job；再加 worker 就复制一段 service 定义并换 `CABINETNC_WORKER_ID`。

备份策略建议：每日 `backup.sh` 到另一台机器/NAS（cron），保留 14 天；升级前手动备份一次。**演练记录（机器 B，2026-09-08）**：造 2 个 job → `backup.sh`（dump 16 KB + MinIO 8 KB）→ `docker compose down -v`（库与对象全部销毁，重建后 0 个 job）→ `restore.sh` → 2 job / 1 用户 / 16 审计全部回来，用恢复前的管理员密码登录成功，`GET /jobs/{id}/result` 返回 200（服务端重算 `ResultSha256` 与恢复出的 MinIO 对象一致），诊断端点显示原审计链与设备名；同轮还验证了停掉 worker-1 后 worker-2 接管并完成 job。

限流按客户端 IP 计数；API 只信任来自 `172.28.100.0/24`（compose 里 `edge` 网络的固定网段）的 `X-Forwarded-For`。若改了该网段，同步改 compose 里 API 的 `CABINETNC_TRUSTED_PROXY_CIDRS`。

## 5. Cloud API 与首次初始化管理员 — READY（Task 6）

API 需要 PostgreSQL 和 MinIO（Task 7 起，job 输入/结果对象存 MinIO）。先创建空数据库；正式部署通过服务管理器/secret store 注入变量。临时本机验证可用 `Read-Host`，让 secret 不进入 PowerShell 命令历史（输入仍会显示在当前控制台）：

```powershell
$env:CABINETNC_DB_CONNECTION = Read-Host 'Paste PostgreSQL connection string'
$env:CABINETNC_JWT_SIGNING_KEY = Read-Host 'Paste random JWT key (32-4096 bytes; recommend 64)'

# 对象存储（MinIO / 任意 S3 兼容）；bucket 不存在时首次使用自动创建
$env:CABINETNC_OBJECTSTORE_ENDPOINT = 'minio.intranet:9000'      # host:port，不带 scheme
$env:CABINETNC_OBJECTSTORE_ACCESS_KEY = Read-Host 'Paste object store access key'
$env:CABINETNC_OBJECTSTORE_SECRET_KEY = Read-Host 'Paste object store secret key'
$env:CABINETNC_OBJECTSTORE_BUCKET = 'cabinetnc'                   # 可省略，默认 cabinetnc
$env:CABINETNC_OBJECTSTORE_USE_SSL = 'false'                      # 内网 TLS 由 Task 8 反向代理/内部 CA 决定

# 只在首次创建 admin 时设置；三项必须全有或全无
$env:CABINETNC_BOOTSTRAP_TENANT = 'shop'
$env:CABINETNC_BOOTSTRAP_ADMIN_EMAIL = 'admin@example.internal'
$env:CABINETNC_BOOTSTRAP_ADMIN_PASSWORD = Read-Host 'Paste bootstrap admin password (12-1024 chars)'

dotnet tool restore
dotnet run --project dotnet/src/CabinetNC.Cloud.Api/CabinetNC.Cloud.Api.csproj -c Release
```

启动时自动执行 EF migration，然后幂等创建 tenant/admin。看到 API 正常、确认能登录后，部署环境里删除三个 `CABINETNC_BOOTSTRAP_*` 变量（全部一起删），保留 DB/JWT 变量并重启。没设置 bootstrap 时**不会生成默认账号**。

```powershell
Invoke-RestMethod http://127.0.0.1:<port>/api/v1/health
# 每个响应 header 都有 X-Correlation-ID；客户端若自带，只允许单个标准 UUID
```

Worker 与 API 使用同一组 `CABINETNC_DB_CONNECTION` / `CABINETNC_OBJECTSTORE_*` 变量，另外可选 `CABINETNC_WORKER_ID`（多实例必须各不相同；容器里 pid 恒为 1，默认值不够用）、`CABINETNC_WORKER_LEASE_SECONDS`（默认 300）、`CABINETNC_WORKER_POLL_SECONDS`（默认 1）。先启动 API（它负责 migration），再启动 worker：

```powershell
$env:CABINETNC_WORKER_ID = 'worker-1'
dotnet run --project dotnet/src/CabinetNC.Cloud.Worker/CabinetNC.Cloud.Worker.csproj -c Release
```

一次 Nest 必须在租约时间内完成（PoC 无心跳续期）；worker 崩溃后 job 在租约到期时被其他 worker 重新领取，最多 3 次尝试。

登录 body 需要 `tenant` slug + email/password/deviceId；slug 只用于查 tenant，JWT 的 `tenant_id` 仍来自数据库。Token 规则：Access 15 分钟；Refresh 30 天且每次使用都会轮换；旧 rotate token 被再次使用会撤销整个 family。DB 只存 refresh token 的 SHA-256，Desktop 只会收到明文一次。任何日志不得出现 password / access token / refresh token / signing key / DB password。

## 6. Desktop 切换 Intranet 模式 — TODO（Task 9）

`ComputeMode.Local | ComputeMode.Intranet`，显式切换，Intranet 失败**不**静默回退 Local。Refresh Token 用 DPAPI CurrentUser 保存，Access Token 只在内存。

## 7. 只凭 JobId 排障 — TODO（Task 10）

`GET /api/v1/admin/jobs/{jobId}/diagnostics`（admin only）。

## 8. 验收记录 — TODO（Task 13）

`docs/cloud/INTRANET_POC_ACCEPTANCE.md`，只允许 `PASS / FAIL / BLOCKED / NOT_RUN`。

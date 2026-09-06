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

## 4. 内网服务器（Ubuntu 24.04）— TODO（Task 8）

基准：8 vCPU / 32 GB / 500 GB NVMe / 1 Gbps / 无 GPU。

将包含：Docker Compose（postgres / minio / cabinetnc-api / cabinetnc-worker / reverse-proxy）、持久卷、健康检查、内部 CA 与证书信任、`.env.example` 说明。**`.gitignore` 含 `.env.*`，提交 `.env.example` 前需加 `!deploy/intranet/.env.example`。**

## 5. 首次初始化管理员 — TODO（Task 6）

只从环境变量读取：`CABINETNC_BOOTSTRAP_TENANT`、`CABINETNC_BOOTSTRAP_ADMIN_EMAIL`、`CABINETNC_BOOTSTRAP_ADMIN_PASSWORD`、`CABINETNC_JWT_SIGNING_KEY`。没有默认凭据；任何日志不得出现密码 / token / signing key。

## 6. Desktop 切换 Intranet 模式 — TODO（Task 9）

`ComputeMode.Local | ComputeMode.Intranet`，显式切换，Intranet 失败**不**静默回退 Local。Refresh Token 用 DPAPI CurrentUser 保存，Access Token 只在内存。

## 7. 只凭 JobId 排障 — TODO（Task 10）

`GET /api/v1/admin/jobs/{jobId}/diagnostics`（admin only）。

## 8. 验收记录 — TODO（Task 13）

`docs/cloud/INTRANET_POC_ACCEPTANCE.md`，只允许 `PASS / FAIL / BLOCKED / NOT_RUN`。

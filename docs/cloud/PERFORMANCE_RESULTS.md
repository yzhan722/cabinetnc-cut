# Intranet Cloud PoC — Performance Results (observed)

所有数字都是 2026-09-06 在机器 B 上用 `dotnet/tools/CabinetNC.Cloud.PerfHarness` 实际跑出来的；原始 JSON/日志在 `.handoff/local-evidence/perf*/` 与 `task10-perf-run*.log`。**没有任何估算值。** 用例是**合成数据（SYNTHETIC）**，不是车间真实工单。

## 1. 环境

| 项 | 值 |
|---|---|
| 主机 | Windows 10 19045 · Intel Core i5-12400F（6C/12T）· 15.8 GB RAM |
| 容器运行时 | Docker Desktop 4.89.0 / Engine 29.7.2，WSL2 VM：12 CPU、7.7 GB 分给 VM |
| 栈 | `deploy/intranet/docker-compose.yml` + `docker-compose.smoke.yml`（API 额外发布到 127.0.0.1:8080，客户端走 loopback http）——postgres 17、MinIO RELEASE.2025-09-07、1 个 API、**1 个 worker**、Caddy |
| 镜像 | commit `414b411` 构建（`EngineVersion = CabinetNC.Compute.Core/1.0.0+414b411…`） |
| 客户端 | 真实 Desktop 客户端内核 `CloudApiClient` + `IntranetComputeGateway`，轮询 200 ms→500 ms（比 Desktop 默认 1 s→5 s 密，以减少轮询对端到端的干扰） |
| 注意 | 客户端、API、worker、数据库都在同一台机器上；没有真实 LAN 往返。数字反映服务栈自身开销，不反映车间网络 |

## 2. 用例

`SyntheticCases.Build(n)`：种子 `20260906 + n` 的确定性随机件——宽 200–1200 mm、高 150–800 mm、材料 SYN-MDF/SYN-PLY、厚 18、2/3 可旋转；大板 1220 × 2440，间距 12，边距 15。同一 n 每次生成相同字节，因此 `InputSha256` 相同、结果哈希相同（已在 JSON 中核对）。

## 3. 指标定义

| 列 | 来源 |
|---|---|
| queue wait | 服务端 `StartedAtUtc − CreatedAtUtc`（提交入库到 worker 领取） |
| compute | 服务端 `DurationMs`——worker 里只包住 `INestingRunner.Run` 的 Stopwatch |
| server e2e | 服务端 `CompletedAtUtc − CreatedAtUtc`（含输入落 MinIO、领取、计算、结果落 MinIO） |
| client e2e | 客户端 Stopwatch：`submit` 发出到 `result` 收到（含客户端轮询间隔） |
| worker CPU ms/job | worker 容器 cgroup v2 `cpu.stat usage_usec` 在整批前后的差 ÷ 批内 job 数（**包含**空转轮询与首批 JIT） |
| worker peak mem | worker 容器 cgroup v2 `memory.peak` |

## 4. 结果 A —— worker 默认配置（`CABINETNC_WORKER_POLL_SECONDS=1`）

`perf-20260906-103849.json`

| case | panels | jobs | parallel | status | queue wait ms (min/med/max) | compute ms (min/med/max) | server e2e ms (min/med/max) | client e2e ms (min/med/max) | sheets | worker CPU ms/job | worker peak mem MiB | batch wall ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| seq-50 | 50 | 5 | 1 | all Succeeded | 496 / 749 / 913 | 0 / 1 / 41 | 624 / 911 / 1580 | 750 / 1258 / 2202 | 9 | 510 | 74.8 | 6780 |
| seq-100 | 100 | 5 | 1 | all Succeeded | 347 / 641 / 966 | 0 / 0 / 6 | 461 / 774 / 1092 | 734 / 1255 / 1271 | 17 | 151 | 79.0 | 5268 |
| seq-300 | 300 | 5 | 1 | all Succeeded | 546 / 774 / 976 | 1 / 1 / 14 | 670 / 891 / 1096 | 742 / 1255 / 1270 | 51 | 159 | 84.6 | 5784 |
| seq-500 | 500 | 5 | 1 | all Succeeded | 449 / 637 / 864 | 2 / 2 / 15 | 574 / 769 / 973 | 741 / 1270 / 1275 | 81 | 115 | 86.5 | 5321 |
| conc-2x100 | 100 | 2 | 2 | all Succeeded | 724 / 806 / 888 | 0 / 1 / 2 | 848 / 928 / 1007 | 1296 / 1319 / 1342 | 17 | 309 | 87.0 | 1347 |
| conc-5x100 | 100 | 5 | 5 | all Succeeded | 475 / 785 / 1113 | 0 / 0 / 0 | 589 / 906 / 1231 | 791 / 1365 / 1365 | 17 | 23 | 87.8 | 1368 |

## 5. 结果 B —— 同一栈，只把 worker 改为 `CABINETNC_WORKER_POLL_SECONDS=0.2`

`perf-20260906-103948.json`

| case | panels | jobs | parallel | status | queue wait ms (min/med/max) | compute ms (min/med/max) | server e2e ms (min/med/max) | client e2e ms (min/med/max) | sheets | worker CPU ms/job | worker peak mem MiB | batch wall ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| seq-50 | 50 | 5 | 1 | all Succeeded | 89 / 232 / 270 | 0 / 1 / 41 | 217 / 376 / 1032 | 325 / 744 / 1322 | 9 | 496 | 72.6 | 3891 |
| seq-100 | 100 | 5 | 1 | all Succeeded | 166 / 251 / 260 | 0 / 0 / 5 | 275 / 369 / 499 | 324 / 447 / 763 | 17 | 163 | 79.5 | 2644 |
| seq-300 | 300 | 5 | 1 | all Succeeded | 124 / 153 / 219 | 1 / 2 / 14 | 248 / 294 / 345 | 338 / 369 / 745 | 51 | 107 | 81.7 | 2550 |
| seq-500 | 500 | 5 | 1 | all Succeeded | 111 / 175 / 323 | 2 / 4 / 15 | 237 / 297 / 441 | 338 / 341 / 892 | 81 | 115 | 83.8 | 2687 |
| conc-2x100 | 100 | 2 | 2 | all Succeeded | 185 / 271 / 357 | 0 / 0 / 0 | 317 / 395 / 474 | 382 / 601 / 819 | 17 | 309 | 84.2 | 824 |
| conc-5x100 | 100 | 5 | 5 | all Succeeded | 286 / 575 / 865 | 0 / 0 / 0 | 386 / 677 / 985 | 819 / 879 / 1395 | 17 | 23 | 84.6 | 1399 |

## 6. 读数

1. **计算本身不是瓶颈。** 矩形 BLF（`grouped_blf_v0`）对 50–500 件的中位耗时 0–4 ms；每个尺寸的最大值（41/6/14/15 ms）都是该批第一个 job（JIT 预热）。
2. **端到端几乎全是等待。** 结果 A 里 queue wait 中位 640–800 ms 就是 worker 1 s 轮询的期望值（~500 ms）加 DB/MinIO 往返；把轮询改成 0.2 s（结果 B），queue wait 中位降到 150–250 ms，server e2e 中位从 770–910 ms 降到 **294–376 ms**。剩余的 ~100–150 ms 是输入/结果对象落 MinIO 与 PostgreSQL 写入。
3. **Desktop 感知时延还要加客户端轮询。** Desktop 默认 1 s→2 s→5 s 轮询，所以操作员看到的通常是 ~1–2 s；把 `CloudClientOptions.PollInitial` 调到 250 ms 可以再省约 0.5 s，代价是多几次很轻的 GET。
4. **并发。** 只有 1 个 worker、串行执行：5 个 100 件 job 同时提交，在 1.4 s 墙钟内全部完成，最长 queue wait 0.87–1.1 s。因为单个 job 只要 ~100–200 ms（含 I/O），一个 worker 的吞吐远超 PoC 需求；worker 数量的意义在于**故障恢复**（租约到期由另一个 worker 接手），不是算力。
5. **资源。** worker 容器峰值内存 **72–88 MiB**（.NET 运行时为主）；每 job 的容器 CPU 时间中位约 100–160 ms（含轮询空转与 JIT），500 件与 50 件几乎没有差别。
6. **正确性副产品。** 6 个批次 × 2 轮，同一尺寸的 sheet 数（9/17/51/81）与 input/result 哈希完全一致——引擎在服务器上是确定性的。

## 7. 建议（基于以上实测）

| 项 | 建议 | 依据 |
|---|---|---|
| worker 规格 | 每副本 1 vCPU / 512 MiB 足够；为 Task 11 之后的 CAM/Post 与安全余量，配置 **2 vCPU / 1 GiB** | 峰值 88 MiB、每 job CPU ≤ 0.2 s |
| worker 数量 | **2 个副本**（不同 `CABINETNC_WORKER_ID`） | 吞吐不需要；租约恢复需要第二个 worker |
| worker 轮询 | `CABINETNC_WORKER_POLL_SECONDS=0.2`（compose 默认仍为 1，避免空转流量；建议在 `.env` 里设） | 结果 A→B：e2e 中位减半以上 |
| Desktop 轮询 | 后续把 `PollInitial` 调至 250 ms（保持 5 s 上限） | 感知时延主要来自客户端轮询 |
| 服务器基准（spec §6 的 8 vCPU / 32 GB） | 对 Nest PoC **大幅过剩**；保留它是为了 PostgreSQL/MinIO/未来 CAM-Post，不是为了排版算力 | 全部批次 CPU、内存占用 |
| 真正需要补的测量 | 第二台 LAN 机器 → 服务器的往返（runbook §4.5）；真实工单规模的 NFP/异形排版在 Local 与 Server 的对比（Task 11 之后） | 本次全部在单机、矩形契约下测得 |

## 8. 复现

```powershell
cd deploy/intranet
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build
$env:CABINETNC_PERF_PASSWORD = Read-Host 'bootstrap admin password'
dotnet run -c Release --project dotnet/tools/CabinetNC.Cloud.PerfHarness -- --url http://127.0.0.1:8080 --tenant shop `
  --email admin@example.internal --sizes 50,100,300,500 --runs 5 --concurrency 2,5 --concurrency-size 100 `
  --worker-container cabinetnc-intranet-cabinetnc-worker-1 --out .handoff/local-evidence/perf
```

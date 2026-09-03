# CabinetNC Cut 产品评估规则（100 分）

> 适用范围：当前 Desktop 产品的发布就绪度。  
> **Loop 停止线：85/100**，且所有硬门槛必须通过。  
> MakerHub 商用品深度单独记录，不用本分数冒充功能对标完成度。

## 硬门槛（任一失败，总评直接 NOT READY）

| Gate | 证据 |
|------|------|
| Desktop 构建成功 | `dotnet build src/CabinetNC.Desktop/...` exit 0 |
| 全 Solution 测试成功 | `dotnet test CabinetNC.slnx` 0 failed（含 `Category=GoldenRegression` 金样） |
| 关键 UIA 冒烟成功 | `tests/ui-smoke/run-all.ps1` 全部场景通过（`artifacts/ui-smoke/results.json`）；`smoke_desktop.py` 已弃用 |
| 示例方案可导入并走完到导出 | 场景 01：状态含 `已载入示例` → 密排 → 计算全部 → 导出出 `.anc` + 平铺 BMP |
| 旋转加工坐标安全 | 90° 单测 + UI NC 不含负 X/Y |
| 导出前预检可执行 | Out 阶段预检 case 通过 |

## A. 功能闭环（35 分）

| ID | 分值 | 评估项 | 满分证据 |
|----|------|--------|----------|
| A1 | 6 | woodjob/cut-package 导入 | 120 板、材料、板材、特征及 checksum 测试 |
| A2 | 8 | 密排 | BLF、缺陷避让、补板队列、锁定、Clipper2 gap 校验 |
| A3 | 8 | CAM/NC | contour/drill/groove、刀补、刀具参数、CAM playhead |
| A4 | 6 | 导出 | NC、DXF、工单 HTML、JSON、bundle |
| A5 | 4 | 持久化 | project.db 与 library.json 回读 |
| A6 | 3 | 七模块 | 生产/补板/设备/路线/原料/工艺/参数均可进入 |

## B. 安全与正确性（25 分）

| ID | 分值 | 评估项 |
|----|------|--------|
| B1 | 6 | 全单元/集成测试通过 |
| B2 | 5 | 旋转 bbox 原点、NC 无负 XY 回归 |
| B3 | 3 | 非法包/校验失败有明确错误反馈 |
| B4 | 4 | 多边形碰撞、间距与缺陷区校验 |
| B5 | 4 | NC 预检覆盖无工序、越界、进给/主轴 |
| B6 | 3 | 无新增高危代码审查项；IDE lint 0 error |

## C. 操作可用性（15 分）

| ID | 分值 | 评估项 |
|----|------|--------|
| C1 | 3 | 冷启动、Worker 启动和状态可见 |
| C2 | 2 | 无方案时步骤 gated；空态 CTA 可用 |
| C3 | 3 | 七模块 UIA 导航可达 |
| C4 | 2 | 成功/失败弹窗含基本信息 |
| C5 | 3 | 手工案例库包含步骤、预期、风险与证据栏 |
| C6 | 2 | 状态栏/告警/预检报告可定位问题 |

## D. 可维护性（10 分）

| ID | 分值 | 评估项 |
|----|------|--------|
| D1 | 3 | Domain 非平凡逻辑有直接测试 |
| D2 | 2 | UIA 冒烟脚本可重复执行并输出 JSON（`tests/ui-smoke`，本地与 Windows CI 同一套） |
| D3 | 2 | 外部依赖用途明确、无专有 MakerHub 二进制 |
| D4 | 3 | 架构、差距、loop 与测试文档一致 |

## E. 交付与回归（15 分）

| ID | 分值 | 评估项 |
|----|------|--------|
| E1 | 5 | Debug Desktop 可构建/启动 |
| E2 | 3 | `pack.ps1` 存在并可形成发布目录 |
| E3 | 4 | 自动冒烟覆盖主路径且 pass@1 |
| E4 | 3 | 手工测试 runbook 能由非开发者执行 |

## 评分规则

1. 自动项只有可复现证据才得分；“代码看起来存在”不计满分。
2. 手工项未执行时最多得该项 50%，执行后记录日期/操作者/证据。
3. 同一根因造成多项失败，各项分别扣分，但报告必须归并根因。
4. 已知高危依赖警告单列风险；若存在可利用路径则触发硬门槛失败。
5. 每轮保存 `artifacts/evaluation-latest.json` 和评估摘要。
6. 运行方式：`pwsh dotnet/scripts/evaluate-product.ps1`（需要 .NET 10 SDK 在 PATH 上；脚本会构建 Release Desktop、跑全部测试项目、再跑 UI 冒烟）。2026-09-03 基线：99/100，硬门全过，B6 因 SkiaSharp/OpenTK 的 NU1701 兼容性警告扣 1 分。

## 双分数

- **Release Readiness（本文）**：本轮目标 ≥85。
- **MakerHub Parity**：见 `docs/MAKERHUB_PARITY_PLAN.md`，当前约 40%，不因测试完善虚增。

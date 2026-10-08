# CabinetNC Cut — Vision

## One sentence

**深度对标 MakerHub 的独立切割站**：Fusion（及其他 CAD）只做上游导出；本产品在交互、工作流、功能深度上系统模仿 MakerHub，用自有内核与 `cabinetnc.woodjob` / cut-package 合同实现。

## Honest status (2026-07-22)

按 MakerHub 商用品深度约 **≈40%**；按 Vite→Desktop 迁入约 **≈85%**。壳与导入较强，Clipper2 多边形校验、刀补与 CAM playhead 已落地；Nest 排放仍为 AABB-BLF（非 NFP），标签/BOM、CSV、机型档案与后置向导仍在残差。

真相来源：`docs/MAKERHUB_PARITY_PLAN.md`（优先于历史里程碑表）。

## North star path

```
载入 woodjob → 原料/设备 → 密排（缺陷避让·补板队列）→ 刀路可开关 → 预检 → 导出 NC/DXF/工单/JSON
```

## Product shape

```text
Fusion / CAD
    ↓ cabinetnc.woodjob (+ legacy cut-package)
CabinetNC Cut  ≈  MakerHub 切割站
    七模块 · 五步生产 · 库 · Nest · CAM · 后置 · 导出
    ↓
NC / DXF / 工单 HTML / JSON → 机床 / 车间
```

## Relation to Fusion

Fusion 插件保持上游导出器；切割站功能不回流进 Fusion palette。

## 2026-09-02 状态与待拍板的方向问题

**上面的"Honest status"是 7 月 22 日写的，之后没有更新过。** 八月发生的事实：

- 产品在 fork 里改名 **OmniCam**，八月全部 14 个功能提交都围绕 Troy 车间那一台 OSAI E4 1325：
  OSAI 单文件后置（含自动 `M6`）、Excitech 贴标 Process 2、`.anc` 反解重切、Fusion `.cnjob` 导入、余料/holding bay、G-code 回放。
- "MakerHub 深度对标"里列的残差（真 NFP、材料去除仿真、后置方言向导、签名 MSI、加密 woodjob）八月没有任何进展，
  也没有人再提。
- Vite 原型（`src/`、`scripts/`）自 8 月 4 日导入后零改动；`npm run check` 仍绿，但它已不是任何人的"验收 oracle"。
- ComputeWorker 289 行，Desktop 直接调 Domain；架构文档里"长计算必须走 Worker"的规则实际不成立。

这不是坏事——一台真机把功能打透，是这类软件最可靠的路。但两份愿景并存的代价是：每加一个 OSAI 特有功能，
upstream 的需求文档、`KNOWN_LIMITATIONS`、验收计划就多一处失真（例：验收计划写"未确认控制器语法前不做自动 M6"，
而 Troy 后置已经每把刀 `M6 T`）。

### 需要拍板的一件事

| 选项 | 含义 | 随之而来的动作 |
|------|------|----------------|
| **A. OmniCam 单机打透（建议）** | 承认产品 = Troy 车间的 OSAI 切割站；通用性以后再说 | `VISION` 北极星改写为"一台 OSAI 上从 `.cnjob` 到贴好标签的板件全流程零手工"；MakerHub 对标表归档；`ARCHITECTURE` 删除或降级 Worker；Vite 原型归档；需求文档以 `POST_CHANGE_CHECKLIST` + `SHOP_LOG` 为验收主线 |
| **B. 保持通用，机床特有走配置** | OmniCam 是一个 profile，不是产品 | 把 `TroyRecipe` 常量、OSAI writer、Process 2 全部挂到 `MachineProfile` 下；Sheet×Tool 后置继续是默认；需要第二台机器（哪怕是模拟器）来证明抽象是对的 |

不拍板的默认结果就是现在这样：代码走 A，文档写 B。

### 无论选哪个都成立的北极星路径（车间任务）

```
Fusion 导出 .cnjob → OmniCam 导入 → 选材料/余料 → 排版 → 刀路可回放 → 预检 → 导出 .anc + BMP → 机床切 + 贴标 → 需要时 .anc 反解重切
```

同一路径能交付上机文件且 `SHOP_LOG.md` 有一条"接受" = 该切片达标。

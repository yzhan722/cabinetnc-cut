# CabinetNC Cut — 产品需求总册（按项目组成）

> **本文件为产品需求总册（按模块）。**  
> 组织方式：按产品/工程模块拆分，**不再按 Day N 推进。**  
> 旧按日报告（`day-NN-report.md` / `log.md` / `current-baseline.md` / `desktop-domain-calls.md`）已删除。  
> 实现快照：`sprint/14d-rc` · merge `cc418c9`（含 Troy `7a03ecf`）· `RC_REPORT.md` / `KNOWN_LIMITATIONS.md`。  
> **状态口径：** Troy 新增功能统一标为「已实现、待验收」；自动测试通过不等于生产验收完成。

---

## 使用说明

1. 下文正文由实现侧根据**当前代码 + 原验收计划 + RC/限制文档**起草，状态多为 **`草稿`**。  
2. Troy / 产品：**改不同意的地方、补待拍板、把模块标成 `冻结`**；不必从零写。  
3. Cursor：**仅对已 `冻结` 的变更项开工**；`草稿` 中标「已交付」的部分默认维护、不重复发明。  
4. 状态：`空白` / `草稿` / `冻结` / `已交付`  
5. 优先级：`P0` 挡出货 · `P1` 主链 · `P2` 增强 · `P3` 体验 · `Later` 明确后置

---

## 总览

> **验收核对：** 2026-08-12 · `dotnet test -c Release` → Domain 85 / Package 31 / Infra 4 全绿。  
> 图例：PASS = 自动/文档证据充分 · PARTIAL = 部分满足或仅手工 · FAIL/PENDING = 未过或未做。详见附录 D。

| # | 模块 | 需求状态 | 优先级 | 实现大致 | 验收 | 依赖 |
|---|------|----------|--------|----------|------|------|
| 1 | 产品范围与非目标 | 草稿 | P0 | 单面条件 RC | PARTIAL 3/4 | — |
| 2 | 数据合同 | 草稿 | P0 | Snapshot v1 已实现、待验收 | PARTIAL | — |
| 3 | 导入与校验 | 草稿 | P0 | `.cnjob` 已实现、待验收 | PARTIAL | 2 |
| 4 | 板件几何与特征编辑 | 草稿 | P1 | 核心已交付；显示增强待验收 | PARTIAL | 2 |
| 5 | Undo / 脏标记 / 会话 | 草稿 | P0 | Snapshot 持久化已实现、待验收 | PARTIAL | 4 |
| 6 | 密排与分组 | 草稿 | P0 | BLF 已交付；NFP/PIP 已实现、待验收 | PARTIAL | 2、3 |
| 7 | Nest 导出硬门 | 草稿 | P0 | true-shape/PIP 门禁已实现、待验收 | PARTIAL | 6 |
| 8 | 刀具与工艺绑定 | 草稿 | P0 | 已交付 | PASS 3/3 | 2 |
| 9 | CAM 安全 / Preflight | 草稿 | P0 | 已交付 | PASS 4/4 | 8、10 |
| 10 | 刀路（Pocket/Groove/Drill/Contour） | 草稿 | P0 | CAM v1 已交付；Groove 显示增强待验收 | PARTIAL | 8、9 |
| 11 | 双面与定位 A/B | 草稿 | P1 | **单面限定** | 当前守门 PASS；双面 0% | 2、9、12 |
| 12 | 后置与导出 Bundle | 草稿 | P0 | 已交付 Sheet×Tool | PARTIAL 3/4 | 7–10 |
| 13 | 标签 / BOM / 工单 | 草稿 | P1 | 已交付核心 | PARTIAL 1/2 | 2、12 |
| 14 | Desktop UI 与生产流程 | 草稿 | P1 | 新 Nest UX 已实现、待验收 | PENDING（人工） | 3–13 |
| 15 | ComputeWorker 一致性 | 草稿 | P1 | 路由/进度接口已增强 | PARTIAL | 6、12 |
| 16 | 测试 / Smoke / 发布 | 草稿 | P0 | 自动 120 项绿；人工待签 | PARTIAL | 全部 |

---

## 1. 产品范围与非目标

- **需求状态：** 草稿  
- **现状摘要：** 条件 RC：单面、混材料/混厚、Nest→CAM→Sheet×Tool 导出；`.cnjob` Snapshot v1、Clipper NFP、PIP、Holding Bay、余料线已实现但未完成生产验收；仍不支持生产双面 WCS、仿真、签名包、自动 M6。

### 目标
成为**独立切割站**：上游 CAD/Fusion 以 `.cnjob`（`cabinetnc.manufacturing-snapshot` v1）交付不可变制造事实；legacy woodjob/cut-package 只读兼容。本产品完成编辑、密排、安全 CAM、可追踪导出，使车间能按 Bundle 空跑/加工，且错误深度/顺序/缺刀/碰撞会挡导出。

### 必须有
1. `.cnjob` / legacy woodjob / cut-package → 可编辑 2.5D 工件  
2. 按材料+厚度分组 Nest，导出前硬校验  
3. 每道工序绑定刀具；安全顺序 + 按板厚切深  
4. Pocket 真清角 v1（非只走边界一圈）  
5. 每 Sheet 的 DXF/manifest + 每 Sheet×Tool 的 NC  
6. Preflight 硬错误挡导出  
7. 无定位策略时禁止反面程序  
8. 标签/BOM 与 WorkpieceId 同源  
9. Desktop 与 Worker 对 Nest 结果可对齐（同输入同权威算法）  
10. 自动测试绿 + 可执行上机检查表  

### 不要做（本阶段 / Campaign 2+）
- 未经人工与边界测试就把 NFP / PIP / Deepnest Preview 宣称为生产可用  
- 材料去除体仿真、后置向导产品化  
- Signed MSI、加密 woodjob 生产强制  
- 完整 DXF/SVG CAD 编辑器  
- 未确认控制器语法前的自动 M6/ATC  
- 把切割站功能回流进 Fusion palette  

### 验收标准
- [x] 混厚、多 Sheet、多刀具作业：错误深度/顺序/缺刀/碰撞会挡导出 — **PASS**（回归/CamSafety/NestGate/Tool/Pocket 测试）  
- [x] 每张板有可追踪程序文件与标签 ID — **PASS**（Bundle + LabelBom）  
- [x] `KNOWN_LIMITATIONS.md` 与宣传口径一致 — **PASS**（双面标 Partial）  
- [ ] 合 `main` 前完成模块 16 约定的人工门禁 — **PENDING**（Smoke/上机未签）  


### 优先级 / 依赖
P0；统领其余模块。

### 待拍板
- [ ] 本阶段对外口径：`条件 RC（单面）` 是否正式采用  
- [ ] 下一阶段优先：NFP/PIP 生产验收还是双面 WCS  

---

## 2. 数据合同（Manufacturing Snapshot / Workpiece / Legacy Package）

- **需求状态：** 草稿  
- **现状摘要：** 新增 `cabinetnc.manufacturing-snapshot` v1 与 `.cnjob` ZIP 容器；原始 Snapshot 不可变并保存到 `project.db`，运行时投影到扁平 `CutPackage.Panels[]`。woodjob v2 / cut-package v1 降为只读兼容输入。

### 目标
以供应商无关的 Snapshot 固化 CAD→车间制造事实，不把 Nest、库存、刀具、Feed/Post/WCS/NC 放进 CAD 数据；legacy 输入仍可导入。

### 必须有
| 字段 | 要求 |
|------|------|
| `schema / schemaVersion / units / jobId` | 必有；v1、mm |
| `workpieceId / materialId / thicknessMm` | 必有；厚度 >0 |
| Geometry quality | 仅 `exact` / `tessellated`；拒绝 `bboxFallback` |
| `outerProfile / nestingPolygon` | 闭合制造几何；不重复末点 |
| Faces A/B | 制造面标签；Snapshot A 永远是加工面 |
| Features | `bore / groove / pocket / throughProfile`；盲特征必须有正深度与 A/B sourceFace |
| Runtime projection | 保留 Face/Through/Group/Purpose/Profile/Decor/Substrate/Color/Quantity/Rotation/Grain |
| Source snapshot | 原 JSON 原样保存在工程数据库 |

### 不要做
- 强制以 SVG 为生产几何权威  
- 本阶段强制 UI 改造成 Project→Module 树  
- 在 Snapshot 中混入 Nest、库存、刀具、Feed/Post、WCS 或 NC  
- 破坏 legacy 只读导入兼容  

### 验收标准
- [x] Snapshot schema/import/projection 自动测试通过 — **PASS**（`ManufacturingSnapshotImporterTests`）  
- [x] 原始 Snapshot 工程持久化测试通过 — **PASS**  
- [x] 缺厚度 → 导入错误 `thickness` — **PASS**  
- [x] legacy cut-package 绿灯 — **PASS**（CutPackageImporter）  
- [ ] `.cnjob` 与 Schema 人工评审 — **PENDING**（新合同待产品冻结）  

### 优先级 / 依赖
P0；被 3/4/6/11/13 依赖。

### 待拍板
- [ ] Snapshot v1 后续 bump 策略：仅 additive vs 允许 breaking（默认 **additive**）  
- [ ] WorkpieceId 是否强制全局唯一  

---

## 3. 导入与校验

- **需求状态：** 草稿  
- **现状摘要：** `.cnjob` 为主输入；woodjob zip/目录、cut-package JSON 继续兼容。新增 Snapshot schema/单面/几何质量/特征深度验证及 4 个样本。

### 目标
一键载入车间包；坏包**失败可见**，不得静默污染当前作业。

### 必须有
1. 支持 `.cnjob`（ZIP：`manifest.json + snapshot.json`）  
2. 支持 `cabinetnc.woodjob` v2 与 `cabinetnc.cut-package` v1 只读兼容  
3. Snapshot：schema、mm、JobId、Workpiece、geometry quality、feature face/depth 全部硬校验  
4. 双面盲特征拒绝；B-only 盲特征归一为 Snapshot A 并告警  
5. woodjob checksum 不匹配 → 失败，明确 SHA-256 mismatch  
6. 导入成功摘要：格式、JobId、板件数、材料/板材规格数、警告列表  
7. 提供单面、门板面、双面拒绝等测试样本  

### 不要做
- 本阶段强制加密容器  
- 把校验失败当警告继续进 Nest  
- 从 relationships 自动猜孔槽并直接生产（实际加工必须进入 features[]）  

### 验收标准
- [x] SMK-010/011：120 板导入成功 — **PASS**（`WoodJobImporterTests` 120 panels）  
- [x] Snapshot / `.cnjob` 导入与单面门禁 — **PASS**（自动测试）  
- [ ] 改 parts 后 checksum 失败 — **PARTIAL**（实现有 SHA-256 校验；**缺专用自动测试**；SMK-013 属手工）  
- [x] 非法空包失败 — **PASS**（`Rejects_empty_panels`；「不替换原作业」偏 Desktop 行为，未见单独自动测）  
- [x] Package 层自动化测试绿 — **PASS**（31）  
- [ ] Desktop 手工打开 4 个 Snapshot/CNJOB 样本 — **PENDING**  

### 优先级 / 依赖
P0；依赖 2。

### 待拍板
- [ ] 生产是否必须 checksum（默认 **必须**）  
- [ ] `.cnjob` 是否立即替代 woodjob 成为对外唯一推荐格式（代码已按此实现）  

---

## 4. 板件几何与特征编辑

- **需求状态：** 草稿  
- **现状摘要：** Feature Inspector；孔/槽可改；镜像/剪贴板；小板警告。新增 Face/Through/Profile 等字段、板件显示标题/分组/明细，以及 Groove profile/中心线显示重建。

### 目标
在 Nest/CAM 前完成必要的 2.5D 修正，保证后续工序基于编辑后几何。

### 必须有
1. 查看/编辑孔（位置、直径、深度）  
2. 查看/编辑槽路径与深度  
3. 外轮廓可见；内开孔/cutout 可识别  
4. 镜像（在 AllowMirror 规则下）  
5. 剪贴板复制/粘贴特征或板件（与现实现一致）  
6. 小板策略警告进入 Preflight（warn）  
7. 编辑后触发模块 5 脏标记  
8. Groove 显示优先 CAD profile；否则仅在宽度明确时由中心线重建，不猜默认 6mm  
9. 板件列表按设计名称/身份显示，并保留 PanelId 可追踪  

### 不要做
- 完整 CAD（布尔、圆弧建模器、尺寸驱动草图）  
- 以 DXF 为唯一主几何源重写内核  

### 验收标准
- [x] 改孔坐标后 Nest/CAM 使用新值 — **PASS**（`PanelEditTests` MoveHole + FeaturesToOps 链路）  
- [x] Mirror 后轮廓/特征一致可测 — **PASS**（`MirrorX_flips_coords_and_edge_banding`）  
- [x] 小板 warn 出现且不误升 error — **PASS**（`small_panel` level=warn）  
- [x] Groove 轮廓重建 / 轴交换保护测试 — **PASS**  
- [x] 板件显示标题/分组测试 — **PASS**  
- [ ] 新标题、颜色/材质、槽轮廓 Desktop 人工检查 — **PENDING**  

### 优先级 / 依赖
P1；依赖 2；驱动 5。

### 待拍板
- [ ] 是否允许在站内新建板件（默认：以导入为主，站内新建 P2）  
- [ ] 圆弧外轮廓：tessellation 精度要求  

---

## 5. Undo / 脏标记 / 会话工程

- **需求状态：** 草稿  
- **现状摘要：** 三类 undo；ManufacturingDirty；Sqlite 工程保存/恢复。新增原始 Snapshot JSON、材料/密排相关状态持久化。

### 目标
防止「改了几何却导出旧 Nest/NC」；会话可中断恢复。

### 必须有
1. Undo/Redo 覆盖特征编辑、镜像、关键布局变更（三类已测范围保持）  
2. 编辑后 Nest/CAM **失效**，导出前必须重算  
3. 工程保存：板件、机型、摆位、NC 相关状态可回读  
4. 脏标记在 UI 上可见  
5. `.cnjob` 原始 Snapshot JSON 与兼容投影同时保留，保存/打开工程不丢信息  

### 不要做
- 无限历史导致内存爆炸（可设合理上限）  
- 脏状态下静默导出成功  

### 验收标准
- [ ] 编辑→导出被拒或提示失效→重 Nest 后可导出 — **PARTIAL**（`ManufacturingDirty` 自动测有；**Desktop 导出门禁属手工 Smoke #2**）  
- [x] Undo 恢复特征与 dirty 行为符合测试 — **PASS**（`ProjectSessionEditTests`）  
- [x] 保存→关闭→打开恢复摆位数 — **PASS**（`SqliteProjectStoreTests` round-trip nest offset）  
- [x] SourceSnapshotJson round-trip — **PASS**  

### 优先级 / 依赖
P0；依赖 4。

### 待拍板
- [ ] Undo 栈深度上限  
- [ ] 工程文件格式是否对外稳定  

---

## 6. 密排（Nest）与分组规则

- **需求状态：** 草稿  
- **现状摘要：** Grouped BLF 保留为稳定基线；新增 Clipper NFP、Deepnest Preview、Parts-in-Part、按材料 Stock override、进度报告、Holding Bay 与余料 Guillotine 预览。以上新增能力均为**已实现、待生产验收**。

### 目标
混材料/混厚作业自动分板密排；同组内可靠摆下；引擎可插拔但**权威结果可复现**。

### 必须有
1. 分组键：`Material + ThicknessMm`（不同厚度不得同 Sheet）  
2. 稳定回退引擎：Grouped BLF；精排候选：Clipper NFP；Deepnest 仅实验预览  
3. 允许旋转时遵守 `AllowedRotations` / grain 约束  
4. 未排下板件列表可见（Unplaced）  
5. 缺陷区避让（现有 AABB punch 能力保持）  
6. 引擎路由：请求高级失败必须明确 fallback，禁止假 NFP 成功  
7. PIP 只允许进入同材料/厚度、明确 through cutout 的可容纳区域  
8. 不同材料组可独立配置 stock、间距、边距、旋转、PIP  
9. 长计算报告进度并支持取消/超时回退  

### 不要做
- 自动测试一绿就宣称 NFP/PIP 可上机  
- 把 Deepnest Preview 当默认生产引擎  
- 将 Holding Bay 中尚未落板的部件视为已排完成  
- Desktop 私有密排算法与 Domain/Worker 分叉  

### 验收标准
- [x] 两厚度同材料 → 不同 Sheet — **PASS**  
- [x] 120 板样本可排完或 Unplaced 可解释 — **PASS**（GoldenExport placed=120；缺料有 `no_stock_for_group`）  
- [x] 同输入 Desktop 路由与 GroupedBlf 一致 — **PASS**（`DesktopWorkerNestParityTests`）  
- [x] fallback 含 `blf_fallback` — **PASS**（`NestEngineRouterTests`）  
- [x] Clipper NFP / PIP / Guillotine 自动测试 — **PASS**  
- [x] `.cnjob` NFP 接受测试 — **PASS**  
- [ ] NFP：异形、旋转、间距、缺陷区人工可视检查 — **PENDING**  
- [ ] PIP：through-only、同组、孔壁/刀具间距人工检查 — **PENDING**  
- [ ] Holding Bay 拖出/放回与跨 Sheet 约束 — **PENDING**  
- [ ] Guillotine 余料线只作预览、不进入 NC — **PENDING**  

### 优先级 / 依赖
P0；依赖 2、3；被 7、12 依赖。

### 待拍板
- [ ] 默认 sheet 规格：1220×2440 vs 1200×2400（样本含两者）  
- [ ] Clipper NFP 是否升级为默认生产引擎（当前建议：待 Smoke 后决定）  
- [ ] PIP 是否默认开启（当前实现按材料卡控制）  
- [ ] Deepnest Preview 是否保留在正式 UI  

---

## 7. Nest 导出硬门

- **需求状态：** 草稿  
- **现状摘要：** `NestExportGate`：间距、碰撞、混组等错误挡导出；新增 true-shape 引擎与合法 PIP pair 的验证适配。

### 目标
任何可能撞刀/混板的摆位不得进入 NC/DXF 打包。

### 必须有
1. 板间 clearance 不足 → error  
2. 多边形/膨胀后相交 → error（Clipper 路径）  
3. 不同材料或厚度混入同一 Sheet → error  
4. Gate 失败时 Desktop **禁止**导出（不可用户覆盖，或仅允许更高权限且默认关）  
5. 仅对 PIP 规划器确认的 parent/child pair 忽略常规重叠；其余重叠仍为硬错误  

### 不要做
- 用「肉眼看图」代替 Gate  
- Gate 失败仍写出正式 Bundle  

### 验收标准
- [x] 故意重叠摆位 → Gate 失败 — **PASS**（`Export_gate_blocks_poly_collision`）  
- [x] 混厚同 Sheet → Gate 失败 — **PASS**（`NestExportGate` `mixed_group_sheet`；分组本身防混排）  
- [x] 自动化 `NestExportGate` 测试绿 — **PASS**  
- [x] NFP true-shape / PIP ignore pair 自动测试 — **PASS**  
- [ ] NFP/PIP 组合后的 Desktop 导出硬门人工验证 — **PENDING**  

### 优先级 / 依赖
P0；依赖 6。

### 待拍板
- [ ] Gate 失败是否允许导出「仅预览 DXF」（默认 **否**）  
- [ ] Holding Bay 非空是否一律阻止导出（建议 **是**）  

---

## 8. 刀具与工艺绑定

- **需求状态：** 草稿  
- **现状摘要：** ToolBinder：contour/pocket→T1，groove→T2，drill→T3；缺 ToolId 硬错误；S/F 来自 ToolCatalog。

### 目标
每道工序有明确刀具；NC 中的转速/进给与目录一致，而非仅注释。

### 必须有
1. 每条 CutOp 导出前必有 `ToolId`  
2. 默认预设（可配置，当前 ASSUMED）：  
   - T1：Ø6.35 轮廓/Pocket · FeedXY 4500 · FeedZ 800 · 18000 rpm  
   - T2：Ø10 槽 · 3500 / 600 / 16000  
   - T3：Ø3 钻 · 1200 / 400 / 6000  
3. 换刀边界输出真实 `S`/`F`（见模块 12）  
4. 缺绑定 → `missing_tool_id`  

### 不要做
- 未确认前自动插入 M6  
- 无目录时静默用机器全局进给冒充「已按刀」  

### 验收标准
- [x] ToolBinder / ToolCatalog NC feed 审计测试绿 — **PASS**  
- [x] 缺 ToolId 作业无法 Bundle — **PASS**（Preflight + Build 抛错）  
- [x] 刀参变更后 NC 头与切削 F/S 跟随 — **PASS**（`ToolCatalogNcFeedAuditTests` / Sheet×Tool 头）  

### 优先级 / 依赖
P0；依赖 2；驱动 9、10、12。

### 待拍板
- [ ] 店内刀号是否与 T1/T2/T3 一致（需映射表则加）  
- [ ] 是否需要按材料覆盖进给（默认本阶段否）  
- [ ] M6：等控制器样例后再做（默认 **不做**）  
- [ ] 默认机型已改为 `OSAI E4 1325`；SafeZ=8 / XY=4000 / Z=800 / RPM=18000 是否符合实机  

---

## 9. CAM 安全（顺序 / 切深 / Preflight）

- **需求状态：** 草稿  
- **现状摘要：** OrderSafe：sheet→rank→panel→tool→feature；ThroughAllowance=0.5；SpoilboardAllow=1.0；硬错误不可覆盖。

### 目标
错误顺序或危险切深不得出程序。

### 必须有
1. 全 Sheet 顺序：Drill → Pocket → Groove → Inner Contour → Outer Profile（不得因 PanelId 打乱全局序）  
2. 外轮廓深度 = 板厚 + ThroughAllowance（默认 0.5）  
3. Groove 超板厚 → `groove_too_deep`（禁止 ApplyPanelDepths 静默 clamp 掉证据）  
4. 切深超板厚+spoil → `depth_spoilboard`  
5. Pocket：`pocket_depth_missing`、`pocket_too_small_for_tool`  
6. Preflight error → Bundle `enforcePreflight` 抛错；Desktop 对硬错误不可 Yes 强出  

### 硬错误码（不可覆盖）
`missing_tool_id` · `pocket_depth_missing` · `pocket_too_small_for_tool` · `groove_too_deep` · `depth_spoilboard` · `no_registration` · Nest Gate 类错误  

### 不要做
- 为过测试删除安全检查  
- 用警告代替上述硬错误  

### 验收标准
- [x] 跨板：drill/groove 早于外轮廓 — **PASS**（`CamSafetyOrderAuditTests`）  
- [x] 过深槽 Preflight FAIL — **PASS**  
- [x] 缺 Pocket 深度 FAIL 且不默认切穿 — **PASS**  
- [x] CamSafety / Preflight / PocketSafety 测试绿 — **PASS**  

### 优先级 / 依赖
P0；依赖 8、10。

### 待拍板
- [ ] ThroughAllowance 默认 0.5 是否冻结  
- [ ] SpoilboardAllow 默认 1.0 是否冻结  
- [ ] 是否允许「仅 warn」的深度策略白名单  

---

## 10. Pocket / Groove / Drill / Contour 刀路

- **需求状态：** 草稿  
- **现状摘要：** Pocket：inset+分段 zigzag+finish；段间 G0；Drill peck；Contour stepdown；刀补 Clipper。Groove 新增 profile/中心线显示几何，但加工仍使用中心线。

### 目标
主特征类型生成可加工刀路；Pocket 为区域清除而非描边。

### 必须有
| 类型 | 要求 |
|------|------|
| Contour 外/内 | 闭合；外轮廓带 through allowance；可 stepdown |
| Drill | 点位；支持 peck；深度明确 |
| Groove | 开放路径；深度受模块 9 约束 |
| Pocket | 明确 DepthMm；刀具可加工；多 scan 不得连成假连续轮廓；finish 单独闭合 |

### 不要做
- 摆线/自适应刀路本阶段  
- 材料去除仿真代替 Preflight  
- Pocket 过小时静默跳过该特征  

### 验收标准
- [x] Pocket 路径点数显著多于边界 — **PASS**  
- [x] 段间无跨区切削 G1；不错误闭合 zigzag — **PASS**  
- [x] 合法 Pocket 分段清料；非法挡导出 — **PASS**  
- [x] 旧 Pocket/Cam 测试不回退 — **PASS**（本次全绿）  
- [x] Groove 显示轮廓不改变 CAM 中心线语义 — **PASS**（自动测试）  
- [ ] 从 `.cnjob` 导入 groove 后核对「显示宽度」与「NC 中心线」 — **PENDING**  

### 优先级 / 依赖
P0；依赖 8、9。

### 待拍板
- [ ] Onion skin 默认 0.5 mm、stepover 40%Ø 是否冻结  
- [ ] 内轮廓与 cutout 是否统一走 contour  

---

## 11. 双面与定位（A/B）

- **需求状态：** 草稿（产品范围为**单面限定**）  
- **现状摘要：** Snapshot v1 明确只支持单面：双面盲特征拒绝；B-only 盲特征归一到 Snapshot A 并告警。Legacy CutOp 仍有 `no_registration` 门禁；无生产翻板 WCS 文件对。

### 目标（完整版，待拍板后实施）
B 面加工仅在定位策略明确后允许；导出成对程序与原点约定可执行。

### 必须有（当前最低）
1. Snapshot 中 A/B 两面均有盲特征 → `double_side_unsupported` 硬错误  
2. B-only 盲特征 → 交换 A/B 面并归一为 Snapshot A，加 warning  
3. Legacy 中存在 B 面工序且无 registration → **硬挡导出**  
4. 文档与 UI 不得暗示「已支持生产双面」  

### 完整版必须有（拍板后）
1. 翻板轴、翻后原点、默认策略写入合同  
2. 产出可区分的 A/B 程序（命名与 WCS 注释）  
3. 人工/夹具步骤写入工单  

### 不要做（拍板前）
- 猜测翻板数学并出可上机 B 程序  
- 把 `MirrorLocal` 数学演示当成车间 WCS  

### 验收标准（当前）
- [x] 无 registration 的 B 作业 Preflight FAIL — **PASS**  
- [x] Snapshot 双面盲特征拒绝、B-only 归一测试 — **PASS**  
- [x] RC 报告标 Partial — **PASS**  
- [ ] Desktop 导入双面拒绝样本并检查错误文案 — **PENDING**  

### 验收标准（完整版，未来）
- [ ] 给定策略后 A/B 程序原点符合拍板定义 — **PENDING**（待拍板）  
- [ ] 空跑检查表含翻板步骤 — **PENDING**（清单仅 Stop/escalate 提示，无完整翻板步骤）  

### 优先级 / 依赖
当前 P1（守门）；完整版 P0 但 **阻塞于待拍板**。依赖 2、9、12。

### 待拍板（阻塞开发）
- [ ] 翻板轴：绕 X 还是 Y  
- [ ] 翻后原点：与 Sheet SW 关系  
- [ ] 默认定位策略名称与字段  
- [ ] 是否需要销钉/靠山尺寸进入数据合同  

---

## 12. 后置与导出 Bundle

- **需求状态：** 草稿  
- **现状摘要：** `{job}_S{n}_{Tn}.nc` + `{job}_S{n}.dxf` + manifest；单刀头含直径与 Feed/RPM；无 M6。MachineCatalog 当前默认收敛为 `OSAI E4 1325`（generic/M2）；Fanuc-like Post 类仍保留但无默认机型预设。

### 目标
车间按 Sheet、按刀装载程序；文件可追溯；不在未确认语法下伪造换刀宏。

### 必须有
1. 输出策略：`sheet_x_tool_nc`  
2. 每个 NC **仅一把刀**的工序  
3. NC 头明确：Sheet、ToolId、刀径、FeedXY、FeedZ、RPM、原点说明  
4. Manifest 列出 programs[]  
5. Root `bundle.json`：sheetCount、各 sheet 文件清单  
6. 未绑定 ToolId → 禁止导出  
7. 预留 `IToolChangePost`；默认 `NullToolChangePost` 返回 null  
8. 默认方言：OSAI E4 1325 的 generic/M2；Fanuc-like/M30 作为显式 Post 能力保留  

### 文件命名（冻结草案）
```text
{job}_S{n}_{Tn}.nc
{job}_S{n}.dxf
{job}_S{n}.manifest.json
{job}.bundle.json
{job}_bom.csv
{job}_labels.html
{job}_sheet.html
```

### 不要做
- 混合多刀于同一 NC 仅靠 `(tool Tn)` 注释换刀  
- 发明控制器 M6 串  

### 验收标准
- [x] 2 Sheet × 3 Tool → 6 个 NC，内容单刀 — **PASS**  
- [x] Feed/RPM 与 ToolCatalog 一致 — **PASS**  
- [ ] WriteToDirectory 文件集完整 — **PARTIAL**（`Build`/`manifest` 测绿；**无** `WriteToDirectory` 落盘专用测试）  
- [x] SheetToolSplit / Bundle 测试绿 — **PASS**  

### 优先级 / 依赖
P0；依赖 7–10。

### 待拍板
- [ ] `OSAI E4 1325` 是否正式默认机型；generic/M2 是否与控制器一致  
- [ ] 命名是否允许客户模板  
- [ ] 拿到 M6 样例后是否升级为「可选 Post」  

---

## 13. 标签 / BOM / 工单

- **需求状态：** 草稿  
- **现状摘要：** Labels HTML、BOM CSV、JobSheet HTML；WorkpieceId 同源。

### 目标
现场贴标、备料、对程序三者 ID 一致。

### 必须有
1. 每块已排板标签含 WorkpieceId（及 Sheet/Panel 必要信息）  
2. BOM CSV 与标签 ID 集合一致（子集关系可测）  
3. 工单含机型、预检摘要、利用率/未排数（现有字段级）  
4. 随一键打包写出  

### 不要做
- 标签 ID 与程序 manifest panel 脱节  
- 本阶段完整条码打印机驱动  

### 验收标准
- [x] LabelBom 测试：标签 ID ⊆ 作业工件 ID — **PASS**  
- [ ] 打包目录含 bom/labels/sheet — **PARTIAL**（Bundle 内存字段有；落盘+Desktop 属 Smoke #10）  

### 优先级 / 依赖
P1；依赖 2、12。

### 待拍板
- [ ] 标签纸尺寸/字段强制列表  
- [ ] BOM 列集是否对接 ERP  

---

## 14. Desktop UI 与生产流程

- **需求状态：** 草稿  
- **现状摘要：** 七模块壳 + 五步生产；新增按材料 Stock 卡、NFP/Deepnest 选择、初次密排空态、Sheet 导航、进度条、Holding Bay 拖放、部件缩略图、Guillotine 余料线与增强 Canvas。全部新 UX 为**已实现、待人工验收**。

### 目标
操作员不打开 IDE 即可完成主链；步骤门禁清晰。

### 必须有
1. 无方案时后步禁用  
2. 五步：载入 → 板材/设备 → 密排 → 刀路/加工档 → 导出  
3. 预检结果可读；硬错误不可强出  
4. 一键打包写 Bundle 目录并提示 Sheet×Tool 策略  
5. 打开示例 / 打开文件 / 保存工程  
6. 每材料 Stock 卡独立配置板材、间距、边距、旋转、PIP  
7. Nest 运行显示进度；结果支持多 Sheet 导航  
8. Holding Bay 支持将板件移出/放回同材料 Sheet，并明确未排状态  
9. NFP/PIP/Guillotine 图形预览与状态文字可读  

### 不要做
- 在 UI 层实现权威 Nest/CAM 算法副本  
- 为演示关闭硬门禁  

### 验收标准
- [ ] SMK 启动/导航/无方案门禁 — **PENDING**（手工 / UIA 未签）  
- [ ] 主链点通 demo120 — **PENDING**（GoldenExport 管线可跑；**Desktop UI 主链未签**）  
- [ ] 人工 Smoke 清单可执行 — **PENDING**（`MANUAL_SMOKE_10MIN.md` 全 MANUAL PENDING）  
- [ ] `.cnjob` 四样本导入、警告与拒绝文案 — **PENDING**  
- [ ] NFP/Deepnest/PIP/Holding/Guillotine 专项 Smoke — **PENDING**  
- [ ] 中文界面无乱码、截断、不可点击控件 — **PENDING**  

### 优先级 / 依赖
P1；依赖 3–13。

### 待拍板
- [ ] 是否要对齐 MakerHub 全部七模块深度（默认：生产五步 P0，其余 P2）  
- [ ] 跨项目导入向导是否本阶段（默认 P2）  

---

## 15. ComputeWorker / 桌面一致性

- **需求状态：** 草稿  
- **现状摘要：** Worker 走 NestEngineRouter；Domain 新增 NFP/PIP/进度/取消接口，Desktop 直接调用同一 Domain 路由。是否强制 RPC 仍未冻结。

### 目标
长计算算法权威在 Domain（可被 Worker 调用）；禁止 UI 私有分叉。

### 必须有
1. Nest 权威：`NestEngineRouter`；实现可为 Grouped BLF / Clipper NFP / Deepnest Preview  
2. 同输入 parity 测试保持绿  
3. 新增重算法不得只写在 Desktop code-behind  
4. Worker 健康状态在 UI 可感知（可用或明确错误）  
5. NFP/PIP/进度与取消语义不得在 Desktop 和 Worker 分叉  

### 不要做
- 「Desktop 一套、Worker 一套」双实现  
- 要求每一次点击 Nest 都必须 RPC（允许本地调同一 Domain，但结果等价）  

### 验收标准
- [x] `DesktopWorkerNestParityTests` 绿 — **PASS**  
- [ ] Desktop→Domain 调用与权威算法无分叉 — **PARTIAL**（parity 绿；原 `desktop-domain-calls.md` 已删，待按 NestEngineRouter 重写调用图）  
- [ ] NFP/PIP 的 Desktop/Worker parity — **PENDING**（当前新增测试偏 Domain）  

### 优先级 / 依赖
P1；依赖 6、12。

### 待拍板
- [ ] 哪些步骤强制 Worker（Nest only / Nest+Ops / 全部）  
- [ ] 离线无 Worker 是否允许降级本地（默认 **允许**，需提示）  

---

## 16. 测试、Smoke、上机与发布门禁

- **需求状态：** 草稿  
- **现状摘要：** 合并后自动测试 Domain 85 / Package 31 / Infrastructure 4 全绿；新增 NFP、PIP、Guillotine、Snapshot、Groove、DisplayTitle、UsageLog 测试。UIA、专项 Nest UX Smoke 与机床清单仍未签；PR #1 未合 main。

### 目标
可回归、可人工签、可上机检查；发布口径诚实。

### 必须有
1. CI/本地：`dotnet test -c Release`（Domain/Package/Infra）全绿  
2. Desktop Release build 0 error  
3. 回归覆盖：多材料、混厚、多 Sheet×Tool、Pocket 安全、顺序、槽深、120 应力、parity、Snapshot、NFP、PIP、Guillotine  
4. 人工：`MANUAL_SMOKE_10MIN.md` 签核（不得用挂掉的 UIA 冒充 PASS）  
5. 上机：`MACHINE_DRYRUN_CHECKLIST.md` 至少空跑 S1_T1  
6. 发布物：tag + RC_REPORT + KNOWN_LIMITATIONS 同步  
7. 离线诊断 UsageLog 可写入 `%LocalAppData%/CabinetNC/logs`，敏感数据与日志膨胀受控  

### 合 main 最低条件（草案）
- [x] 上表自动门禁绿 — **PASS**（85 / 31 / 4）  
- [ ] 人工 Smoke 签字 — **PENDING**  
- [ ] RC_REPORT / KNOWN_LIMITATIONS 与新 NFP/PIP/Snapshot 状态一致 — **PENDING**（仍是 audit2 旧口径）  
- [x] 双面仍 Partial 写进发布说明 — **PASS**（RC_REPORT）  

### 不要做
- 删测试保绿  
- Partial 写成 Done  
- force-push 改写已发布 tag  

### 验收标准
- [x] 自动套件绿有日志 — **PASS**（Domain 85 / Package 31 / Infra 4）  
- [ ] Smoke 清单无未解释的 MANUAL PENDING — **PENDING**（10/10 仍 MANUAL PENDING）  
- [ ] 新 tag 指向含 Troy 更新、需求重写和 Smoke 结果的 commit — **PENDING**（现 tag 停在 `bab9103`）  
- [x] UsageLog 自动测试 — **PASS**  

### 优先级 / 依赖
P0；依赖全部模块的可测声明。

### 待拍板
- [ ] 合 main 是否强制机床空跑签字（建议 **要**）  
- [ ] UIA 是否纳入 CI（建议：暂不强制，以人工 Smoke 为准）  

---

## 附录 A — 与旧按日计划的关系

旧 Day 1–14 报告与里程碑表已删除。大致对应关系（仅追溯）：

| 旧里程碑 | 本册模块 |
|----------|----------|
| M0 基线 | 16 |
| M1 数据合同 | 2、3 |
| M2 可撤销编辑 | 4、5 |
| M3 Nest | 6、7 |
| M4 刀具与安全 CAM | 8、9、10 |
| M5 可追踪输出 | 11、12、13 |
| M6 RC | 14、15、16 |

执行方式：**模块冻结 → 小切片实现 → 模块验收**。

---

## 附录 B — 相关文档索引

- `docs/sprint/RC_REPORT.md`  
- `docs/sprint/KNOWN_LIMITATIONS.md`  
- `docs/sprint/MANUAL_SMOKE_10MIN.md`  
- `docs/sprint/MACHINE_DRYRUN_CHECKLIST.md`  
- `docs/sprint/workpiece-contract.md`  
- `docs/sprint/export-bundle-layout.md`  
- `docs/sprint/nest-engine-decision.md`  
- `docs/testing/SMOKE_CASE_LIBRARY.md`  
- `docs/VISION.md` · `docs/FEATURE_GAP.md`  
- `docs/manufacturing-snapshot-v1.md`  
- `docs/manufacturing-snapshot-v1.schema.json`  

---

## 附录 C — 建议你优先拍板的清单（浓缩）

1. 对外口径是否确认「条件 RC · 单面」  
2. `.cnjob` 是否冻结为主要交付格式  
3. Clipper NFP / PIP 是否在专项 Smoke 后升为生产默认  
4. Deepnest Preview 是否保留在正式 UI  
5. T1/T2/T3 与店内刀号映射  
6. OSAI E4 1325 / generic M2 / SafeZ / Feed 参数是否符合实机  
7. ThroughAllowance=0.5 / Spoil=1.0 是否冻结  
8. 合 main 是否强制空跑签字  
9. 双面：翻板轴 / 原点 / 策略（阻塞双面完整版）  
10. 是否做 M6（建议等控制器样例）  

---

## 附录 D — 验收核对记录（2026-08-12 · Troy 更新后）

**证据命令：** `dotnet test -c Release` → Domain **85** / Package **31** / Infra **4** 全绿。  
**分支：** `sprint/14d-rc` · merge `cc418c9`（Troy `7a03ecf` + 本地需求提交 `d8e71b5`）。  
**Tag：** 现有 `rc-14d-audit2-20260805` 仍指向旧 `bab9103`，不能代表本次更新。

### 汇总

| 结果 | 模块数（按模块验收列） |
|------|------------------------|
| 稳定旧能力 PASS | 8, 9, 12 的核心；Pocket/CAM 审计链 |
| 新实现、待验收 | 2, 3, 4, 5, 6, 7, 10, 14, 15, 16 |
| 明确 PENDING | NFP/PIP/Holding/Guillotine/Desktop 专项 Smoke；双面完整版；新 RC tag；合 main |

自动测试数由 81 增至 **120**；新增测试说明实现面扩大，**不能据此把新增功能直接标为生产 PASS**。

### 缺口（建议补测试或补签）

| 缺口 | 建议 |
|------|------|
| checksum 篡改失败 | 加 Domain/Package 自动测试（实现已有） |
| `WriteToDirectory` 落盘文件集 | 加临时目录写出断言 |
| `.cnjob` 产品合同 | 人工评审 Schema、单面约定与 legacy 迁移口径 |
| NFP / PIP | 增加异形、旋转、孔壁、缺陷区、同材料边界样本与人工可视验收 |
| Holding Bay / Sheet 导航 | 验证未排件、跨 Sheet、同材料约束与导出阻断 |
| Guillotine | 确认只作余料预览，不进入 NC |
| Desktop 脏导出 / 五步 / 新 Nest UX | 扩充并签 `MANUAL_SMOKE_10MIN.md` |
| `Desktop→Domain` 调用图缺失 | 按 NestEngineRouter / 现 MainWindow 调用重写短文档 |
| OSAI E4 1325 | 实机确认原点、M2、SafeZ、Feed/RPM |
| RC 文档与 tag | 更新 RC_REPORT / KNOWN_LIMITATIONS，并创建新 tag（不移动旧 tag） |
| 机床空跑 | 签 `MACHINE_DRYRUN_CHECKLIST.md` |
| 合 `main` | 至少 Smoke 签字后再议 |

### 结论

- **原 audit2 制造主链（CAM 安全/Sheet×Tool）自动验收仍保持通过。**  
- **Troy 新增 `.cnjob`、NFP、PIP、Holding Bay、Guillotine 已实现且自动测试通过，但仍待生产验收。**  
- **发布级验收未达标：** 新 UX Smoke、机型确认、上机、RC 文档/tag、合 main 门禁仍开着。  
- **不要**把当前状态标成「全部验收通过」。

---

## 修订记录

| 日期 | 变更 |
|------|------|
| 2026-08-12 | 由按日计划改为 16 模块骨架 |
| 2026-08-12 | 按现状与原 RC 定义补全各模块草稿 |
| 2026-08-12 | 删除过期按日报告与过时调用图/基线文档；更新引用 |
| 2026-08-12 | 按 Troy `7a03ecf` 重写：Snapshot v1、NFP/PIP、新 Nest UX、OSAI 默认机；统一标为已实现待验收 |

# Troy 车间代码：补齐单元测试与回归测试

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改车间行为（尤其不改 U/V 贴标坐标、不改 Troy 已汇报的bmp目录问题）的前提下，把 Troy 当前 `sprint/14d-rc`（HEAD `4b448a8`）里**已实现但测试锁不住**的能力补成可重复的 Domain/Package/Infrastructure 测试，并加一条全量回归门。

**Architecture:** 只测 Domain / Package / Infrastructure 的纯函数与往返。Desktop 只保留现有 UIA smoke 的**对照清单**（OmniCam 改版后易碎，不作为本计划主交付）。发现测试失败时：先确认是回归还是测试写错；确属回归再做最小修复，禁止借测试改工艺。

**Tech Stack:** .NET 10、xUnit、现有 `CabinetNC.Domain.Tests` / `Package.Tests` / `Infrastructure.Tests`。命令一律 `dotnet test -c Release`。

## Global Constraints

- 分支：`sprint/14d-rc`，跟 Troy，不 force-push。
- 不改 U/V 贴标算法；不改 `D:\Label` / `D:\CNC` 导出目录（已口头汇报）。
- 不新增大功能（NFP 竞赛、双面 WCS、MSI）。
- 先写失败测试，再改产品代码（仅当测试证明现有行为违反已声明不变量）。
- 每个 Task 结束必须：相关测试绿 + 一次小 commit。
- 命名跟现有测试：`*_still_*` / `*_round_trips_*` / 明确错误码字符串。

## 现状（不要重复造轮子）

已经较完整（本计划不重写）：

| 区域 | 代表测试 | 大约用例 |
|------|----------|----------|
| 密排门禁 / NFP / PIP / 拖拽 | `NestP0SafetyGateTests` 等 | 40+ |
| 轮廓桥 | `ProfileBridgePlannerTests` | 22 |
| 板稳定 | `SheetStabilityOptimizerTests` | 19 |
| OSAI-Troy NC | `NcEmitterTroyTests` | 13 |
| 贴标避让 / Process2 文本 | `LabelAnchorFinderTests` + `LabelExportTests` | 16 |
| Pocket 安全 + 螺旋填充 | `PocketSafetyGateTests` + `PocketNcSegmentAuditTests` | 6 |
| 工程 CAM/桥往返 | `SqliteProjectStoreTests.Round_trips_session_cam_bridges_and_ops` | 1 |
| RC 抽检 | `RcRegressionCoverageTests` | 4 |

**明确缺口（本计划要补）：**

1. CAM 顺序未锁 **Tongue=1 先于普通 Groove/Pocket=2**（`CamSafetyTests` 只断言 drill/groove 在外轮廓前）。
2. `ClimbCut` 没有独立测试（只在圆弧拟合里顺带断言）。
3. `NestSheetSpec.LeftoverAtOrigin` 无测试（余料第一张的边距规则只在 Desktop 调用）。
4. `NestStockOverrides` 无测试。
5. `NcPreflight` 新错误码没有集中回归（pocket/groove 窄槽/出板）。
6. 工程会话未覆盖 **Holding / PIP / LabelAnchors / 余料板尺寸**。
7. Worker 仍是矩形 BLF：缺少「不要声称 NFP parity」的合同测试。
8. 全量 `dotnet test -c Release` 未作为本 HEAD 的书面基线。
9. Desktop UIA `smoke_desktop.py` 仍按旧按钮走，**本计划只列缺口，不把 UIA 当关门条件**。

## 文件地图

- Create: `dotnet/tests/CabinetNC.Domain.Tests/ClimbCutTests.cs`
- Create: `dotnet/tests/CabinetNC.Domain.Tests/LeftoverSheetSpecTests.cs`
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NestStockOverridesTests.cs`
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NcPreflightGateTests.cs`
- Create: `dotnet/tests/CabinetNC.Domain.Tests/WorkerNestContractTests.cs`
- Modify: `dotnet/tests/CabinetNC.Domain.Tests/CamSafetyTests.cs`
- Modify: `dotnet/tests/CabinetNC.Domain.Tests/RcRegressionCoverageTests.cs`
- Modify: `dotnet/tests/CabinetNC.Infrastructure.Tests/SqliteProjectStoreTests.cs`
- Modify: `docs/sprint/KNOWN_LIMITATIONS.md`（仅补一句：Worker 无 NFP 合同）
- 不修改：`LabelExport` 输出目录、`LabelAnchorFinder` 几何、`NcEmitter.Troy` 头尾（已有测试）

---

### Task 1: 记下本 HEAD 测试基线

**Files:** 无代码。记录到本计划文末「基线」节。

**Interfaces:** 无。

- [ ] **Step 1: 跑全量 Release 测试**

```powershell
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
Set-Location C:\Users\yino\Projects\cabinetnc-cut\dotnet
dotnet test -c Release --verbosity minimal
```

Expected: 记录 Domain / Package / Infrastructure 的 Passed 数量。若有失败：先停，把失败当成 Task 1b 修测试或最小产品修复，不要继续加新用例。

- [ ] **Step 2: 把数字写进本文件文末**

格式：`YYYY-MM-DD HEAD=4b448a8 Domain=N Package=N Infra=N`

- [ ] **Step 3: Commit**（仅当文末基线有改）

```bash
git add docs/superpowers/plans/2026-08-24-troy-missing-unit-regression-tests.md
git commit -m "docs: record Troy HEAD test baseline before gap fill"
```

---

### Task 2: 锁 CAM 顺序 Drill → Tongue → Clearance → Inner → Outer

**Files:**
- Modify: `dotnet/tests/CabinetNC.Domain.Tests/CamSafetyTests.cs`
- Modify: `dotnet/tests/CabinetNC.Domain.Tests/RcRegressionCoverageTests.cs`（加一条同序）

**Interfaces:**
- Consumes: `CamSafety.SequenceRank(CutOp)`、`CutOp.IsTongue`
- Produces: 失败则产品 `CamSafety.cs` 不得改顺序语义去迁就测试

- [ ] **Step 1: 写失败测试**

在 `CamSafetyTests` 增加：

```csharp
[Fact]
public void Tongue_ranks_before_clearance_groove_and_pocket()
{
    var drill = new CutOp { Op = "drill", PanelId = "A" };
    var tongue = new CutOp { Op = "groove", PanelId = "A", IsTongue = true };
    var groove = new CutOp { Op = "groove", PanelId = "A", IsTongue = false };
    var pocket = new CutOp { Op = "pocket", PanelId = "A" };
    var inner = new CutOp { Op = "contour", PanelId = "A", FeatureId = "CUT1" };
    var outer = new CutOp { Op = "contour", PanelId = "A" };

    Assert.Equal(0, CamSafety.SequenceRank(drill));
    Assert.Equal(1, CamSafety.SequenceRank(tongue));
    Assert.Equal(2, CamSafety.SequenceRank(groove));
    Assert.Equal(2, CamSafety.SequenceRank(pocket));
    Assert.Equal(3, CamSafety.SequenceRank(inner));
    Assert.Equal(4, CamSafety.SequenceRank(outer));

    var ordered = CamSafety.OrderSafe([outer, pocket, tongue, drill, inner, groove]).ToList();
    Assert.Equal(["drill", "groove", "pocket", "groove", "contour", "contour"], ordered.Select(o => o.Op));
    Assert.True(ordered[1].IsTongue);
    Assert.False(ordered[3].IsTongue);
    Assert.Equal("CUT1", ordered[4].FeatureId);
    Assert.True(string.IsNullOrWhiteSpace(ordered[5].FeatureId));
}
```

注意：`OrderSafe` 在同 rank 时按 `PanelId` 再 `ToolId` 再 `FeatureId`。上面列表同一 PanelId，pocket vs 非 tongue groove 同为 rank 2，顺序由 `FeatureId`/`ToolId` 决定。**写测试时用明确 FeatureId 排先后**，避免依赖未文档化的稳定排序。更稳写法：

```csharp
var ordered = CamSafety.OrderSafe([outer, tongue, drill, inner]).Select(o => (o.Op, o.IsTongue, o.FeatureId)).ToList();
Assert.Equal("drill", ordered[0].Op);
Assert.True(ordered[1].IsTongue);
Assert.Equal("CUT1", ordered[2].FeatureId);
Assert.True(string.IsNullOrWhiteSpace(ordered[3].FeatureId));
```

- [ ] **Step 2: 跑测试确认失败或已绿**

```powershell
dotnet test tests/CabinetNC.Domain.Tests -c Release --filter FullyQualifiedName~Tongue_ranks_before
```

若已绿：不要改 `CamSafety.cs`。若失败：核对 `IsTongue` 是否在 `OpsPlanner` 打标，再决定是补打标还是只测 `SequenceRank`。

- [ ] **Step 3: 最小实现（仅当 SequenceRank 与注释不一致）**

只允许改 `CamSafety.SequenceRank` 以匹配文件头注释：`Drill → Tongue → Clearance → Inner → Outer`。

- [ ] **Step 4: 再跑 CamSafetyTests + RcRegressionCoverageTests**

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add dotnet/tests/CabinetNC.Domain.Tests/CamSafetyTests.cs dotnet/tests/CabinetNC.Domain.Tests/RcRegressionCoverageTests.cs
git commit -m "test: lock CAM order drill, tongue, clearance, profile"
```

---

### Task 3: `ClimbCut` 独立单元测试

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/ClimbCutTests.cs`
- Test against: `dotnet/src/CabinetNC.Domain/Manufacturing/ClimbCut.cs`

**Interfaces:**
- Consumes: `ClimbCut.OrientClosed(path, inner)`、`ClimbCut.SignedArea`、`ClimbCut.StartAtLongestEdge`
- Produces: 外轮廓 CW、内轮廓 CCW、下刀点在最长边

- [ ] **Step 1: 写失败测试**

```csharp
public class ClimbCutTests
{
    static readonly (double X, double Y)[] UnitSquare =
        [(0, 0), (10, 0), (10, 4), (0, 4), (0, 0)];

    [Fact]
    public void Outer_climb_is_clockwise()
    {
        var oriented = ClimbCut.OrientClosed(UnitSquare, inner: false);
        Assert.True(ClimbCut.SignedArea(oriented) < 0);
    }

    [Fact]
    public void Inner_climb_is_counterclockwise()
    {
        var oriented = ClimbCut.OrientClosed(UnitSquare, inner: true);
        Assert.True(ClimbCut.SignedArea(oriented) > 0);
    }

    [Fact]
    public void Starts_on_longest_edge()
    {
        var oriented = ClimbCut.OrientClosed(UnitSquare, inner: false);
        var a = oriented[0];
        var b = oriented[1];
        var len0 = Math.Hypot(b.X - a.X, b.Y - a.Y);
        Assert.True(len0 >= 9.9, $"first edge {len0}");
    }
}
```

- [ ] **Step 2: 跑测试**

```powershell
dotnet test tests/CabinetNC.Domain.Tests -c Release --filter FullyQualifiedName~ClimbCutTests
```

- [ ] **Step 3: 仅当失败时改 `ClimbCut.cs`（保持 ArtCAM 外 CW / 内 CCW）**

- [ ] **Step 4: 确认 PASS**

- [ ] **Step 5: Commit**

```bash
git add dotnet/tests/CabinetNC.Domain.Tests/ClimbCutTests.cs
git commit -m "test: lock climb-cut orientation for outer vs inner"
```

---

### Task 4: 余料第一张边距 `LeftoverAtOrigin`

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/LeftoverSheetSpecTests.cs`
- Source: `NestSheetSpec.LeftoverAtOrigin` in `BlfNester.cs`

**Interfaces:**
- Consumes: `NestSheetSpec.LeftoverAtOrigin(leftoverW, leftoverH, fullW, fullH, edgeMm, style)`
- Produces: 贴原点的余料：仍是整板外沿的边保留边距，切开的边边距为 0

- [ ] **Step 1: 写测试**

```csharp
public class LeftoverSheetSpecTests
{
    [Fact]
    public void Partial_leftover_keeps_origin_edges_and_drops_cut_edges()
    {
        var style = new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12, AllowRotation = true };
        var spec = NestSheetSpec.LeftoverAtOrigin(600, 800, 1220, 2440, 15, style);
        Assert.Equal(600, spec.WidthMm);
        Assert.Equal(800, spec.LengthMm);
        Assert.Equal(15, spec.InsetLeftMm);
        Assert.Equal(15, spec.InsetBottomMm);
        Assert.Equal(0, spec.InsetRightMm);
        Assert.Equal(0, spec.InsetTopMm);
        Assert.Equal(12, spec.SpacingMm);
    }

    [Fact]
    public void Full_size_leftover_keeps_all_insets()
    {
        var style = new NestSheetSpec { SpacingMm = 10, AllowRotation = false };
        var spec = NestSheetSpec.LeftoverAtOrigin(1220, 2440, 1220, 2440, 15, style);
        Assert.Equal(15, spec.InsetRightMm);
        Assert.Equal(15, spec.InsetTopMm);
        Assert.False(spec.AllowRotation);
    }
}
```

- [ ] **Step 2–4: 跑测；失败则只改 `LeftoverAtOrigin` 以符合注释；PASS 后 commit**

```bash
git commit -m "test: lock leftover-at-origin inset rules"
```

---

### Task 5: `NestStockOverrides.ForGroup`

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NestStockOverridesTests.cs`

**Interfaces:**
- Consumes: `NestStockOverrides.ForGroup(NestSettings global, NestSheetSpec stock)`
- Produces: 组内边距/间距/旋转来自 stock，全局 GrainLock 等保留

- [ ] **Step 1: 测试**

```csharp
[Fact]
public void ForGroup_takes_stock_border_spacing_rotation_keeps_grain()
{
    var global = new NestSettings
    {
        MarginMm = 99, ClearanceMm = 99, AllowRotation = false,
        GrainLock = true, AllowedRotations = [0, 180],
    };
    var stock = new NestSheetSpec { BorderMm = 15, SpacingMm = 12, AllowRotation = true };
    var g = NestStockOverrides.ForGroup(global, stock);
    Assert.Equal(15, g.MarginMm);
    Assert.Equal(12, g.ClearanceMm);
    Assert.True(g.AllowRotation);
    Assert.True(g.GrainLock);
    Assert.Equal(new[] { 0d, 180d }, g.AllowedRotations);
}
```

- [ ] **Step 2–5: 跑测、必要时最小修复、commit**

```bash
git commit -m "test: lock per-stock nest overrides"
```

---

### Task 6: `NcPreflight` 错误码集中回归

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NcPreflightGateTests.cs`
- Source: `NcPreflight.Check` / `PocketSafetyIssues` / `GrooveClearIssues`

**Interfaces:**
- Consumes: 错误码 `no_ops`、`pocket_depth_missing`、`pocket_too_small_for_tool`、`out_of_sheet`、`missing_tool_id`
- Produces: 导出前门与 `PocketSafetyGateTests` 不重复造数据，但把「无工序 / 出板」补上

- [ ] **Step 1: 测试（最小 CutOp）**

```csharp
[Fact]
public void Empty_placed_ops_is_no_ops()
{
    var report = NcPreflight.Check([], MachineCatalog.Get("nesting_router_6"), 1220, 2440);
    Assert.False(report.Ok);
    Assert.Contains(report.Issues, i => i.Code == "no_ops");
}

[Fact]
public void Point_outside_sheet_is_out_of_sheet()
{
    var op = new CutOp { Op = "drill", Placed = true, Enabled = true, ToolId = "T3", SheetX = 5000, SheetY = 10 };
    var report = NcPreflight.Check([op], MachineCatalog.Get("nesting_router_6"), 1220, 2440);
    Assert.False(report.Ok);
    Assert.Contains(report.Issues, i => i.Code == "out_of_sheet");
}
```

Pocket 缺深已有 `PocketSafetyGateTests`：本文件只 `Assert.Contains` 一次以免两套断言漂移。把现有 pocket 测试方法名写进本文件注释即可。

- [ ] **Step 2–5: 跑 `NcPreflightGateTests`+`PocketSafetyGateTests`；commit**

```bash
git commit -m "test: centralize NcPreflight no-ops and out-of-sheet gates"
```

---

### Task 7: 工程会话补 Holding / PIP / LabelAnchors / 余料板

**Files:**
- Modify: `dotnet/tests/CabinetNC.Infrastructure.Tests/SqliteProjectStoreTests.cs`
- Types: `ProjectSessionState.Holding`、`PartInPart`、`LabelAnchors`、`StockKinds` leftover 尺寸字段（以源码属性名为准）

**Interfaces:**
- Consumes: `ProjectSessionCodec.Serialize/Deserialize`
- Produces: 打开工程后拖标、套裁、余料卡不能丢

- [ ] **Step 1: 在现有 `Round_trips_session_cam_bridges_and_ops` 旁新增 Fact**

打开 `ProjectSessionState` 确认属性名后填写（不要猜不存在的字段）：

```csharp
[Fact]
public void Round_trips_holding_pip_label_anchors_and_leftover_stock()
{
    // 构造 session：Holding 一条、PartInPart 一条、LabelAnchors 一条、
    // StockKinds 带 LeftoverX/Y（若 DTO 无此字段则只断言 Width/Length）
    // Save/Load/Deserialize 后 Assert.Equal 原值
}
```

若 DTO 没有 leftover 字段：不要改 Desktop；只测已有 DTO。把「余料尺寸未进 session」写进 `KNOWN_LIMITATIONS.md` 一句。

- [ ] **Step 2: 跑 Infrastructure.Tests**

- [ ] **Step 3: 若反序列化丢失：最小修复 `ProjectSessionCodec`，禁止改 UI**

- [ ] **Step 4: PASS**

- [ ] **Step 5: Commit**

```bash
git commit -m "test: round-trip holding, PIP, and label anchors in project session"
```

---

### Task 8: Worker 合同 — 不声称 NFP/PIP parity

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/WorkerNestContractTests.cs`
- 对照: `INestingEngine` / ComputeWorker 请求是否只有宽高

**Interfaces:**
- Consumes: Worker proto 或 `NestEngineRequest` 在 Worker 侧的构造
- Produces: 测试证明 Worker 路径 `EnginePreference` 强制/实际为 `blf`，或文档化「无多边形字段」

先读 `dotnet/src/CabinetNC.ComputeWorker` 里组 `NestEngineRequest` 的代码，再写断言。示例方向：

```csharp
[Fact]
public void Worker_request_does_not_carry_polygon_for_nfp()
{
    // 从 Worker proto 或映射函数：Assert 没有 OutlinePoints / 只有 WidthMm HeightMm
}
```

不要为了让测试绿去实现 Worker NFP。

- [ ] **Step 1–5: 读代码 → 写断言 → 跑测 → 必要时只改测试或 limitations → commit**

```bash
git commit -m "test: document worker nest contract is BLF rectangles only"
```

---

### Task 9: 回归门 — 一条 Troy 主链

**Files:**
- Modify: `dotnet/tests/CabinetNC.Domain.Tests/RcRegressionCoverageTests.cs`

**Interfaces:**
- Consumes: `OpsPlanner` + `CamSafety.OrderSafe` + `NcEmitter.OpsToNc(..., recipe: PostRecipe)` + `SheetBundleBuilder`
- Produces: 同一组输入上：**Troy 单文件可含换刀**；**Sheet×Tool 每文件仍单刀**（两套策略并存，都要绿）

- [ ] **Step 1: 增加 Fact**

```csharp
[Fact]
public void Troy_recipe_and_sheet_tool_split_remain_distinct()
{
    // 1 板：孔 + 槽 + 外轮廓，已排
    // bundle = SheetBundleBuilder.Build(...) 每个 NcText 只出现一个 (tool
    // troy = NcEmitter.OpsToNc(ops, profile, recipe: new PostRecipe { ... 与测试里已有 Troy 夹具一致 })
    // troy 允许 M6 或 T 切换（按 NcEmitterTroyTests 现有约定断言），但不得破坏 bundle 单刀文件
}
```

复制 `NcEmitterTroyTests` 里已通过的 `PostRecipe` 构造，不要发明新 dialect。

- [ ] **Step 2: 跑 RcRegressionCoverageTests + NcEmitterTroyTests + SheetToolSplitNcTests**

- [ ] **Step 3: 冲突时优先改本回归测试的断言，使其与「两套输出策略」一致，禁止删 Sheet×Tool**

- [ ] **Step 4: PASS**

- [ ] **Step 5: Commit**

```bash
git commit -m "test: regression that Troy post and sheet-tool split stay distinct"
```

---

### Task 10: 全量回归并更新限制说明

**Files:**
- Modify: `docs/sprint/KNOWN_LIMITATIONS.md`（Worker NFP、UIA 过期各一句）
- Modify: 本文文末基线

- [ ] **Step 1: 全量测试**

```powershell
dotnet test -c Release
dotnet build src\CabinetNC.Desktop\CabinetNC.Desktop.csproj -c Release
```

Expected: 0 failed；Desktop 0 error（NU1701 可保留）。

- [ ] **Step 2: 更新 KNOWN_LIMITATIONS：Worker 矩形 BLF；Desktop UIA 未跟 OmniCam 重录**

- [ ] **Step 3: Commit**

```bash
git commit -m "docs: note worker BLF contract and stale desktop UIA after OmniCam"
```

---

## 明确不做

- 改贴标 bmp 目录 / 提示文案（已汇报 Troy）。
- 改 U/V、避让槽灯铰链。
- 重写 `smoke_desktop.py` 当关门（可另开计划）。
- 恢复你们被 hard reset 掉的 Nest 矩阵测试文件（除非 Task 1 基线显示缺覆盖且能无冲突迁回）。
- 为测而实现 Worker NFP。

## Desktop / UIA（另开，不挡本计划）

`smoke_desktop.py` 仍点旧控件（如 `OpsCalcBtn` 可能已无 `x:Name`）。OmniCam 主链人工对照：打开方案 → 板材初始密排 → 计算全部 → 导出。不写入本计划 commit 门。

## 基线

- Date: 2026-08-24
- HEAD before fill: `4b448a8` — Domain **214** / Package **33** / Infrastructure **5**
- After fill: Domain **226** / Package **33** / Infrastructure **6** (+12 Domain, +1 Infrastructure)

---

## Spec coverage

| 缺口 | Task |
|------|------|
| Tongue 顺序 | 2 |
| ClimbCut | 3 |
| 余料边距 | 4 |
| 分材料板参数 | 5 |
| Preflight 空工序/出板 | 6 |
| 工程 Holding/PIP/锚点 | 7 |
| Worker 合同 | 8 |
| 双后置策略回归 | 9 |
| 全绿 + 文档 | 1, 10 |

## Placeholder scan

无 TBD。Task 7/8 依赖打开源码确认 DTO/Worker 字段名后再填字面量——执行时读文件，不在计划里编造不存在的属性。

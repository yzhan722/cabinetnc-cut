# 发版金样回归（规范化 NC）

Date: 2026-08-24  
Status: draft for review  
Repo: cabinetnc-cut (`CabinetNC.*`)  
Branch: `sprint/14d-rc`

## 问题

每次版本更新需要一条可重复的回归门。现有 `dotnet test -c Release`（Domain / Package / Infrastructure 单测）继续当安全网，但缺少「同一组作业 → 规范化 NC 不变」的长期锁。散装 `[Fact]` 锁不住整份刀路；Desktop UIA 在 OmniCam 改版后已过期，不能当关门。

## 目标

发版官方门槛：

1. `dotnet test -c Release` 全绿（含金样）。
2. 少量固定作业的 **规范化 NC 金样** 一致。
3. 仓库预留夹具目录，以后丢真实包不必改比较规则。

成功标准：换版本后若刀路、刀号、换刀或分板规则变了，金样失败并给出可 diff 的规范化文本；若只改注释、空行、N 行号、界面文案，金样仍绿。

## 非目标

- 不改 U/V 贴标几何；不锁 `D:\Label` / `D:\CNC` 路径；不把 BMP/HTML 标签当金样。
- 不重录 `smoke_desktop.py`。UIA 不是本门。
- 不实现 Worker NFP / PIP / 双面 WCS。
- 不删现有单测（含 `RcRegressionCoverageTests`）。金样是附加层。
- 不引入 Verify 等第三方快照库。
- 不把规范化器放进生产 `CabinetNC.Domain`（只放测试程序集）。

## 架构

| 层 | 职责 |
|----|------|
| 全量单测 | 现有 Domain / Package / Infrastructure。行为点、错误码、往返。 |
| 金样马具 | 合成（或将来夹具）作业 → `GroupedBlfNester` → `OpsPlanner` → `NcPreflight` → 后置 → **规范化** → 与仓库金样逐文件比较。 |
| 夹具目录 | `dotnet/tests/testdata/regression/jobs/` 本阶段只有 README + schema，不扫描、不跑。 |

发版命令只有一条：在 `dotnet/` 下

```powershell
dotnet test -c Release
```

可选快跑（不替代发版）：

```powershell
dotnet test tests/CabinetNC.Domain.Tests -c Release --filter Category=GoldenRegression
```

## 规范化规则（一次定死）

输入：一份 NC 字符串。输出：UTF-8、LF、末尾恰好一个 `\n`。

1. `\r\n` / `\r` → `\n`。
2. 去掉每行行首 `N` + 十进制数字 + 空白（OSAI 行号；插入一行会全盘移位，不进金样）。
3. 每行 `TrimEnd`；丢掉空行。
4. **丢掉装饰注释**（整行、大小写不敏感，匹配前缀）：
   - `(cabinetnc-cut`
   - `(wcs:`
   - `(cam safety:`
   - `(origin:`
5. **保留机床/刀号语义**：
   - Troy：`(UAO,`、`(DLY,`、`M6 T`、`G`/`M`/`T`/`S`/`F`/`X`/`Y`/`Z`
   - Sheet×Tool：`(tool `、`(sheet `
6. 不改坐标/进给数字精度（沿用发射器原文）。精度策略变更视为回归。
7. 全文 `Trim` 后再保证末尾 `\n`。

配套文本金样（同一作业目录）：

| 文件 | 规则 |
|------|------|
| `preflight-codes.txt` | 只锁 `PreflightIssue.Code`，每行一个，排序去重。预检 Ok → **0 字节文件**。不锁中英文句子。 |
| `layout.txt` | `{panelId}\t{sheetIndex}\t{material}\t{thicknessMm}` 按 `panelId` 排序。不单独锁 XY（XY 在 NC 里）。 |

不进金样：DXF、工单 HTML、BOM、Labels、Worker gRPC。

## 马具（测试程序集）

项目：`dotnet/tests/CabinetNC.Domain.Tests`（不新建 csproj）。  
目录：`dotnet/tests/CabinetNC.Domain.Tests/Regression/`。

```csharp
static class NcTextNormalizer
{
    public static string Normalize(string nc);
}

sealed record GoldenArtifact(string RelativePath, string Utf8Text);

sealed class GoldenJob
{
    public required string Id { get; init; }
    public required string Post { get; init; } // "sheet_tool" | "troy"
    public required IReadOnlyList<Panel> Panels { get; init; }
}

static class GoldenJobRunner
{
    public static IReadOnlyList<GoldenArtifact> Run(GoldenJob job);
    public static void AssertMatchesGoldens(string jobId, IReadOnlyList<GoldenArtifact> actual);
}
```

固定环境（三作业共用，禁止测试里另起一套）：

- 机器：`MachineCatalog.Get(MachineCatalog.DefaultId)`（当前 `osai_e4_1325`）。不要写 `Get("nesting_router_6")` 这类会静默回落到 `All[0]` 的别名。
- 刀具：`ToolCatalog.DefaultMap()`。
- 板材：一张 `NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12, AllowRotation = true }`。
- 密排：`GroupedBlfNester.Pack(panels, new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true }, [sheet], GroupedBlfNester.SizeOfOutline)`。
- CAM：`var ops = ToolBinder.BindAll(OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(panels), nest.Placements));`
- 预检：`NcPreflight.Check(ops, profile, 1220, 2440, panels.ToDictionary(p => p.PanelId))`。
- 包：`new CutPackage { SchemaName = CutPackage.Schema, JobId = job.Id, Panels = panels }`。

后置：

- `sheet_tool`：`SheetBundleBuilder.Build(pkg, nest.Placements, ops, profile, enforcePreflight: false)`。每个 `ToolNcProgram` → `nc/S{SheetIndex + 1}_{ToolId}.nc.norm`。
- `troy`：`NcEmitter.OpsToNc(ops, profile, recipe: PostRecipe.TroyDefault())` → `nc/program.nc.norm`。

预检失败时：仍写出 `preflight-codes.txt` 与 `layout.txt`，**不发射 NC**。第一批三作业必须预检 Ok 且有 NC。

金样根目录：`dotnet/tests/testdata/regression/goldens/{jobId}/`。  
用源码相对路径定位（`[CallerFilePath]` 或从测试程序集向上找到 `tests/testdata/regression`），不要只靠 `bin/Release` 拷贝，以便回写进 git。

缺金样文件 = 失败（除非正在 update）。失败信息必须带 jobId、相对路径、expected vs actual（xUnit `Assert.Equal`）。

xUnit：`[Trait("Category", "GoldenRegression")]`，三个 `[Fact]`（失败名即作业名）。

## 第一批合成作业

板件几何在 `Regression/GoldenFixtures.cs` 内构造，矩形 `Outline`，风格对齐 `RcRegressionCoverageTests` 的矩形夹具，但 **不得引用 Rc 类**。

### 1. `sheet_tool_single_panel`

- 1 板：`oak` / 18 mm / 200×150，竖直孔 + 槽 + 外轮廓。孔深 `max(1, th-2)`，槽深 `min(6, th-1)`（与现 RC 夹具同一深度规则）。
- Post：`sheet_tool`。
- 期望：预检 Ok；`layout.txt` 一行且 `sheetIndex` 为 0；`nc/` 下至少两个单刀 `.nc.norm`；每个规范化后恰好一行 `(tool …)`。

### 2. `multi_material_no_share`

- 2 板：`oak` 18 mm 400×300；`mdf` 18 mm 350×280；均带孔。
- Post：`sheet_tool`。
- 期望：两行 layout 的 `sheetIndex` 不同、材料不同；两套 `nc/` 文件。

### 3. `troy_single_file_atc`

- 板件与作业 1 相同。
- Post：`troy`。
- 期望：唯一 `nc/program.nc.norm`；规范化后含 `M6 T`；允许同一文件多把刀。

三作业几何固定。当前 BLF 若改算法导致坐标变，金样应红，由人回写，不当静默。

## 夹具目录（本阶段只留合同）

路径：`dotnet/tests/testdata/regression/jobs/`。

本阶段 **不扫描、不跑**（避免空扫描死代码）。README 写死以后接入格式，比较规则不得另起一套：

```json
{
  "id": "shop_example",
  "post": "sheet_tool",
  "machine": "osai_e4_1325",
  "source": "woodjob",
  "path": "source.woodjob"
}
```

以后 `source=woodjob` 由 Package 导入再交给同一个 `GoldenJobRunner`。在那之前不为金样去实现 Worker NFP。

## 更新金样

环境变量 `CABINETNC_UPDATE_GOLDENS=1` 时：创建缺失目录、按 `RelativePath` 写文件、删除金样目录里 **本次没产出** 的旧 `.norm` / `layout.txt` / `preflight-codes.txt`（避免僵尸刀号文件一直绿）。Update 后仍 Assert 绿。

日常发版 **不得** 开这个变量。刀路有意变更时：开变量跑一遍，人工看 diff，再提交金样。

## 与现有门的关系

| 已有 | 关系 |
|------|------|
| `RcRegressionCoverageTests` | 语义抽检保留；金样锁规范化全文。 |
| `NcEmitterTroyTests` / Sheet×Tool 单测 | 方言细节保留；金样锁主链作业。 |
| `docs/testing/PRODUCT_EVALUATION_RULES.md` | 「全 Solution 测试成功」已覆盖金样。加一句：含 `Category=GoldenRegression`。不把过期 UIA 改成真关门。 |
| `docs/sprint/KNOWN_LIMITATIONS.md` | 补一句：发版金样不含贴标/UIA。 |

## 约束

- 分支 `sprint/14d-rc`，不 force-push。
- 不改贴标导出目录与 U/V。
- 不改车间 NC 发射器行为去迁就金样；金样跟现行发射器。

## 验收

1. 无 `CABINETNC_UPDATE_GOLDENS` 时，三作业与已提交金样逐文件相等。
2. 人为改作业尺寸后测试失败。
3. `--filter Category=GoldenRegression` 只跑这三作业。
4. 全量 `dotnet test -c Release` 仍 0 failed。
5. `jobs/` 仅 README，不引入额外失败。

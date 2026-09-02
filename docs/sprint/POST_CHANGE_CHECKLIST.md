# 后处理变更检查单 + 上机证据协议

适用范围：任何改变机床**运动**或**程序结构**的提交——`NcEmitter*.cs`、`PostRecipe.cs`、`ProfileBridge.cs`、
`OuterProfileOrder.cs`、`CamSafety.OrderSafe`、`LabelExport.EmitPro2/WrapCutWithLabelProcess`、
`LabelAnchorFinder`、`NestTransform.ToSheet`。改注释、改文案、改 N 行号不算。

背景：`7f486c3`（2026-08-31）一个提交 2 500 行，去掉换刀后回原点、把 XY 快移与 Z 下降合成一条
`G0 X.. Y.. Z30`、重排外轮廓顺序。这是机床上真实发生的运动变化，fork 上没有任何东西拦它，
upstream 只有一条 golden 字符串不等。这份检查单让这类改动带着证据进来。

## 一、提交前（写代码的人）

- [ ] **改动一句话**：这次让刀具动作有什么不同？（例："换刀后不再回 X0Y0，直接快移到第一个下刀点"）
- [ ] **安全不变量**：`dotnet test tests/CabinetNC.Domain.Tests --filter FullyQualifiedName~NcSafetyInvariantTests` 全绿。
      如果为了通过而改了不变量本身，在 PR 里单独说明为什么旧规则错了。
- [ ] **golden**：`--filter FullyQualifiedName~ReleaseGoldenRegressionTests`。红了就贴 diff，逐行说明每处变化是预期的；
      然后 `CABINETNC_UPDATE_GOLDENS=1` 只跑受影响的作业重生成，不要顺手刷新别的 golden。
- [ ] **反解往返**：`--filter FullyQualifiedName~NcReverseRoundTripTests`。后置变了反解也要能读回来。
- [ ] **真实程序**：`--filter FullyQualifiedName~ShopAncFixtureTests`。
- [ ] **假设清单**：改动依赖控制器哪些行为？写进 `KNOWN_LIMITATIONS.md` 对应后置段。当前已登记的假设：
  - OSAI `M6` 宏结束时 Z 在换刀高度，因此换刀后第一条 `G0 X.. Y.. Z30` 可以 XY 与 Z 同时动。
  - `(UAO,1)` 原点 = 板料 SW 角，Z0 = 垫板上表面（Troy 后置）。
  - `(DLY,3)` 足够让主轴到速。
- [ ] **PR 尺寸**：≤ 1 500 行；后置改动不要和 UI 改动混在一个 PR。

## 二、上机前（Troy）

- [ ] 用 CI 绿的 SHA 打包；SHA 写在工单上。
- [ ] 按 `MACHINE_DRYRUN_CHECKLIST.md` 空跑，重点看本次"改动一句话"里描述的动作。
- [ ] 第一块用废板或抬高 Z 一个板厚。

## 三、上机后（证据，让没有机床的人也能审）

每次上机在 `docs/sprint/SHOP_LOG.md` 追加一段（模板见文末），至少包含：

| 字段 | 例子 |
|------|------|
| 日期 / SHA / 后置 | 2026-09-03 / `e64eee8` / Troy `.anc` |
| 作业 | lounge divider recut，2 张 18 mm 白板 |
| 观察到的运动 | 换刀后直接斜向快移到 (25,25,30)，未碰压板 |
| 操作员干预 | 进给 80%；无 Z 补偿 |
| 结果 | 接受 / 返工 / 中止 + 一句原因 |
| 证据 | 照片 2 张（首刀、tab）；`.anc` 已入 `shop-anc/` |

没有这一段的后置改动，upstream 有权不合。

## 四、什么情况必须停下来先问

- 要改 `SafeZMm`、`ThroughZMm`、`LastPassLeaveMm`、`BridgeLeaveMm` 任何一个数值。
- 要让快移在安全高度以下发生（哪怕是在口袋内部）。
- 要改 `M6`/`M3`/`M5`/`M30` 的顺序或删除 `(DLY,3)`。
- 要改 Process 2 的 `E41/E42` 循环。
- 要改 U/V 贴标坐标算法（见 `LABELING_REQUIREMENTS.md` L5）。

## SHOP_LOG.md 条目模板

```markdown
## 2026-MM-DD · SHA `xxxxxxx` · Troy .anc
- 作业：
- 改动一句话（来自 PR）：
- 观察：
- 干预：
- 结果：
- 证据：照片 N 张；shop-anc/文件名.anc
```

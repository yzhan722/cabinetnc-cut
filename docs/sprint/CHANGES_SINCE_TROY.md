# yzhan722/sprint/14d-rc 相对 Trojanes/sprint/14d-rc 的变化（给 Troy 的合并说明）

基线：Troy 最后一次提交 `e64eee8`（2026-09-02，draft nested panels / grain-aware nest / stock kinds）。
本分支在其之上 24 个提交，两条 CI（Linux Regression、Windows Desktop）每次推送均绿。
合并方式：`git pull upstream sprint/14d-rc`（upstream = yzhan722）；没有改动 OSAI 后置输出，只有 `troy_single_file_atc`
金样因行程优化重排（差异只在 G0 顺序，见 `989c9d7`）。

## 一眼看懂

| 主题 | 提交 | 对车间的意义 |
|---|---|---|
| 反推 / 安全不变量 / 贴标收尾 | `989c9d7` | PR #2 红灯的根因（几何配对 vs 行程优化）修掉；`NcSafetyInvariantTests`（含变异测试）、`NcReverseRoundTripTests`、`shop-anc` 夹具入口；缺 BMP 变红、机床目录成为设置项 |
| UI/UX 两轮 + CAD/CAM 惯例 | `989c9d7` `a1dd8a2` `daafaed` | 菜单栏、五步流程标签、作废横幅、通知卡、视口缩放平移/DRO/代码联动、未保存模型、最近文件、图层开关、反推对照卡 |
| Desktop.Core（无 WPF，可在 Linux 测） | `a616c38` `8079c3d` `baafc76` `d34b09b` | 状态推断 / 最近文件 / 未保存指纹 / 仿真时间线 / 视口数学 / 导出决策 / 五步流程规则 / 反推审计 / 快捷键表 → **72 个测试**；顺手修掉"仿真停在段起点时下一段不动"的 bug |
| UI 冒烟（UIA）进仓库并上 CI | `e425160` `3e0e27f` `d34b09b` `064d53d` `7d3cb1c` | `tests/ui-smoke` 5 个场景（示例→导出、作废横幅、.anc 反推→重切→导出、库文件损坏恢复、损坏工程）；托管 Windows Runner 上 5/5，已改为 **阻塞** 步骤；`evaluate-product.ps1` 重新可用（99/100） |
| 首次使用 | `626c526` | 内置示例原来过不了预检硬门（Ø5 孔），改为 Ø4；`pocket_too_small_for_tool` 文案说清哪把刀进不去、怎么改 |
| 车间健壮性 | `064d53d` `7d3cb1c` | `library.json` 原子写入 + `.bak` 自动恢复（断电不再静默清空补板库）；损坏的 `project.db` / 保存失败 / 处理器异常不再让程序崩掉 |
| 打开文件 | `3e0e27f` | 命令行参数、拖放到窗口都能打开 job / project.db / .anc |
| 文档 | `c5380bf` 等 | `MANUAL_SMOKE_10MIN` 重写为 23 步；`KNOWN_LIMITATIONS`、`OWNERS`、`DESKTOP_TESTABILITY_PLAN`、`PRODUCT_EVALUATION_RULES` 同步 |

## 需要 Troy 决定的事（我做不了主）

1. **Ø5 系统孔**：预置刀具 T1 Ø6.35 / T2 / T3 Ø3 加工不了 Ø5–约 Ø7.5 的孔（<Ø5 用 T3 钻，≥Ø5 走口袋）。要么在工艺模版加 Ø5 钻并告诉我刀位号，要么接受这段尺寸被预检拒绝。
2. **缺 BMP 是拦截还是警告**（`LABELING_REQUIREMENTS` L3）：现在是红色警告 + 导出完成；改成硬拦截只需改一行，但要你拍板。
3. **双面加工**：`DUAL_FACE_QUESTIONNAIRE.md` 的问题没有答案前，B 面继续被阻断。
4. **OSAI E41/E42 重试**（L4）：需要控制器手册确认报警码语义。
5. **真实机床程序**：`dotnet/tests/testdata/regression/shop-anc/` 仍是空的；放几份跑过的 `.anc` 进去，`ShopAncFixtureTests` 会自动接管。

## 运行方式速查

```powershell
cd dotnet
dotnet test CabinetNC.slnx -c Release                    # 554 个测试（Domain 431 / Package 40 / Infrastructure 11 / Desktop.Core 72）
dotnet build src\CabinetNC.Desktop -c Release
pwsh tests\ui-smoke\run-all.ps1                          # 5 个 UIA 场景，截图在 artifacts\ui-smoke
pwsh scripts\evaluate-product.ps1                        # 产品评估打分（基线 99/100）
```

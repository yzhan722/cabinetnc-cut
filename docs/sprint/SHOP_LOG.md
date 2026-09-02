# 上机记录（append-only）

每次在真机上跑 OmniCam 产出的程序，追加一段。格式见 `POST_CHANGE_CHECKLIST.md` 第三节。
没有机床的一方靠这份记录审查后置改动；有机床的一方靠它记住哪个 SHA 在哪块板上验证过。

## 2026-08-19 · SHA 未记录 · Troy .anc（补录）
- 作业：22'6 Club Lounge，`22_OHC_Divider_Recut.anc`，含 Process 2 贴标
- 改动一句话：首次上机跑 Process 2
- 观察：`M701` 后进入 `P2M701`，`@I54=0` 循环；标签软件报 `Socket.Bind(localEP)` 地址无效
- 干预：停机
- 结果：中止 — 标签 PC 网卡不是 `192.168.0.4`；软件侧 BMP 写在 `label\` 子目录且提示 `D:\Label`，与现场 `D:\CNC` 不一致
- 证据：见 `LABELING_INCIDENT_HANDOVER_2026-08-19.md`（视频在机床 PC 本地，未入库）

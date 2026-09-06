# CabinetNC 内网云化 PoC — Cursor 执行入口

你负责执行 CabinetNC 的“内网后台 + 服务器 Nesting + 动态 Token + 日志 + 后续 CAM/Post 云化 + 客户端代码保护”计划。

## 开始前

1. 阅读 `docs/superpowers/specs/2026-09-05-intranet-cloud-design.md`。
2. 阅读 `docs/superpowers/plans/2026-09-05-intranet-cloud-implementation.md`。
3. 仓库：`yzhan722/cabinetnc-cut`。
4. 计划基线：`sprint/14d-rc @ 5e410d554e17ba77d2dbb8deb3ff1967154d0c67`。
5. 如果 `sprint/14d-rc` 已前进，不得 reset/force-push；先比较差异并记录到 `docs/cloud/IMPLEMENTATION_NOTES.md`。
6. 新建 `feature/intranet-cloud-poc`，不得直接在 RC 分支开发。
7. 先跑现有 .NET tests；Windows 环境再跑 UI smoke。
8. 每个任务：先测试、再实现、再回归、再 commit。

## 禁止事项

- 不修改 Nest/CAM/Post 制造语义来“适配云端”。
- 不运行真实 CNC。
- 不使用未批准的真实客户数据。
- 不把密码、JWT key、refresh token、数据库密码提交到 Git。
- 不在客户端内置永久 server client secret。
- 不一次性重写 `MainWindow.xaml.cs`。
- 不引入 Kubernetes/GPU。
- 不合并与本任务无关的 Troy 大功能分支。
- PoC 阶段保留 Local Compute，用于 Local/Server A/B 对比。

## 本轮完成标准

- Desktop 可登录内网后台。
- Access Token 15 分钟，Refresh Token 自动轮换。
- Desktop 可提交 Nest Job，拿 JobId，查询状态和结果。
- Nest 在服务器执行，不在 API HTTP 请求线程内长时间同步运行。
- Job 有幂等、重试、worker lease、日志、input/output hash、engine version。
- Local/Server Nest parity 通过。
- 断网、API 重启、Worker 崩溃有明确恢复行为。
- 50/100/300/500 panels 有真实性能数据。
- 后续 CAM/Post 可以沿同一 compute boundary 迁移。
- Customer build 最终可排除本地核心 Compute engine。

# Desktop smoke / manual QA

## 自动冒烟（当前）：`tests/ui-smoke`

PowerShell + UI Automation，驱动 Release 版 Desktop 走真实流程，断言状态栏 / 标题 / 导出文件并截图。
Windows CI（`windows-desktop.yml`）把同一套脚本作为阻塞步骤运行，截图作为工件 `ui-smoke-screenshots` 上传。

```powershell
cd d:\project\cabinetnc-cut\dotnet
dotnet build src\CabinetNC.Desktop\CabinetNC.Desktop.csproj -c Release
pwsh tests\ui-smoke\run-all.ps1            # 全部场景；退出码 = 失败场景数
pwsh tests\ui-smoke\run-all.ps1 -Only stale  # 只跑名字含 stale 的场景
```

- 场景文件在 `tests/ui-smoke/scenarios/*.txt`，一行一步（`invoke:` / `tab:` / `menu:` / `ctx:` / `assert-status:` / `assert-file:` / `shot:` …），
  步骤语法见 `ui-smoke.ps1` 头部注释。
- 导出步骤靠环境变量 `OMNICAM_AUTO_EXPORT_DIR`（由 `run-all.ps1` 设为临时目录）绕过原生对话框；不设该变量时 Desktop 行为不变。
- `run-all.ps1` 同时把 `OMNICAM_LIBRARY_PATH` 指向每个场景独立的临时 `library.json`，冒烟不会改动操作员真实的参数库；`pre-copy:` 步骤可在启动前放置库文件夹具（见场景 04 的损坏恢复）。
- 截图输出到 `dotnet/artifacts/ui-smoke/`（已 gitignore）。

## 自动冒烟（旧）：`smoke_desktop.py`

面向 OmniCam 改版前的控件名，**已不再作为门禁**；保留只为 `scripts/evaluate-product.ps1` 的历史引用。需要时按新控件名重录，或直接改用上面的场景文件。

## 手工案例

10 分钟手工冒烟（当前界面）：`docs/sprint/MANUAL_SMOKE_10MIN.md`

完整案例与结果记录模板：`docs/testing/SMOKE_CASE_LIBRARY.md`

非法输入 fixture：`tests/manual/fixtures/invalid_empty.cut.json`

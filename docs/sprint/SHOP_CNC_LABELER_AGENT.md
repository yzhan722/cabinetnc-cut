# OmniCam 贴标 — 机床电脑检查手册（给 Agent）

把本文件拷到机床控制电脑（U 盘 / 网盘均可），在 Cursor 里 **@ 这个文件**，然后发一句：

> 按 `SHOP_CNC_LABELER_AGENT.md` 做只读检查。先不要改网卡、不要改宏、不要改贴标软件配置。做完用文末「回报格式」写结论。

对话可以继续用现有的 OmniCam 贴标云端上下文。**真正跑命令必须在这台机床电脑的本机 Agent 上**（能看到 `D:\`、能跑 PowerShell）。若你是 Cursor 云端虚拟机、看不到 `D:\CNC` 或 ping 不通 `192.168.0.2`，立刻停，告诉用户：请在本机打开 Agent，不要用云端 VM。

---

## 0. 你是谁、目标是什么

你在 **KEYANG / Excitech 血统开料机** 的 Windows 控制电脑上排查 **Process 2 贴标**。软件是 OmniCam（CabinetNC-Cut）导出的 OSAI `.anc`。

**本次目标（按顺序，未通过不要跳）：**

1. 确认标签软件能否绑定本机 `192.168.0.4`（2026-08-19 的第一故障）。
2. 确认打印路径里有与 `LS11` 同名的 **平铺** `.bmp`。
3. 确认 bmp 是 **600×600、1 bpp 单色、300 dpi**（不是 24 位彩图）。旧样张 236×157 能对上文件名，但在 60×60 纸上会偏小。
4. 只有 1–3 都过，才让操作员再跑 Process 2，观察 `M701` 是否返回、是否执行到 `G0 V… U…`。

不要把 U/V 对错当成今天的阻断。2026-08-19 **根本没执行到定位块**。

---

## 1. 禁止事项（硬规则）

未得到操作员当面同意，禁止：

- 改任何网卡 IP / 禁用或启用网卡 / 加第二 IP
- 改 OSAI 宏（尤其 `P2M701`–`P2M704`）或 AMP / PLC
- 改 Excitech Label Printing 的 IP、路径（除非只读打开看一眼）
- 删、覆盖 `D:\CNC` 或 `D:\Label` 里别人的图（核对缺失可以 **复制** 进去，不要删现场图）
- 关 OSAI、关贴标软件、重启电脑
- 改 Cursor / Windows 默认路由，以免贴标网卡抢上网

只读检查、收集证据、写结论。要改配置：列出命令，等人说「改」再动。

---

## 2. 机器与协议（事实，不要重新发明）

| 项 | 值 |
|---|---|
| 控制 | OSAI OPENcontrol，贴标在 **Process 2**（U/V 轴） |
| 贴标软件 | **Excitech Label Printing V3.0** |
| 网络意图 | 控制器 `.2`，打印机 `.3`，本机标签网卡 **`.4`**，网段 `192.168.0.0/24` |
| NC 握手 | `LS11='文件名'`（无路径、无扩展名、不能有 `'`）→ `M701` → 宏 `P2M701` 置 `@G240=1`，等光电 **`@I54=1`** → 再 `G90 G0 V… U…` → `M702` |
| `LS11` 拼路径 | `{PrintPicturePath}\{LS11}.bmp`，**不能**再套一层 `label\` |
| 2026-08-19 打印路径 | 当天软件里是 **`D:\CNC`**（不是更早假设的 `D:\Label`）。**两个目录都要查。** |
| OmniCam 导出 | bmp 在 NC 旁边的 `label\` 子目录；拷到机床必须 **摊平** 到打印路径根目录 |

### 2026-08-19 已证实

- ANC 能进 Process 2，`LS11` 能解析，`M701` 能进 `P2M701`。
- 卡在 `P2M701`：画面 `@I54=0`，循环等待。
- Excitech 报错：`The requested address is not valid in its context` / `Socket.Bind(localEP)`，要绑 **`192.168.0.4`**。
- **未执行** `G90 G0 V218.491 U226.266` 和 `M702`。
- 当天 NC：`C:\Users\azqrv\Documents\Rouge\22'6 Club Lounge\NC Files\22_OHC_Divider_Recut.anc`
- 当天 `LS11`：`OHC_OH_D0_2`，第二张 `OHC_OH_D1_2`
- 需要的图：`D:\CNC\OHC_OH_D0_2.bmp` 和 `D:\CNC\OHC_OH_D1_2.bmp`（平铺）

### 尚未用实机验证（不要当已经过）

- 1bpp bmp 能否被 V3.0 打印（OmniCam 现已改为 1bpp，需现场图确认）
- U/V 对调（MakerHub 模板是 `V=X U=Y`；OmniCam 目前写 `V{SheetY} U{SheetX}`）。**等 M701 返回后再单步看。**

`P2M701` 会多次 `(GTO,CON,@I54=1)`。没纸、没吸到标、`@I54` 一直 0，看起来像死循环。不要改宏去跳过光电。

---

## 3. 先确认你在机床电脑上

在 PowerShell 跑：

```powershell
hostname
$env:COMPUTERNAME
$env:USERNAME
pwd
Test-Path 'D:\CNC'
Test-Path 'D:\Label'
Get-NetIPAddress -AddressFamily IPv4 |
  Select-Object InterfaceAlias, IPAddress, PrefixLength |
  Format-Table -AutoSize
```

**通过：** 能看到车间盘符（`D:\CNC` 或 `D:\Label` 至少一个存在），或 IPv4 里出现 `192.168.0.x`。

**失败（你在云端 VM）：** `D:\CNC` 不存在、没有 `192.168.0.x`、hostname 不像工控机。停止检查，让用户在本机 Cursor 打开本文件重来。

---

## 4. 检查 A — 标签网卡 `192.168.0.4`

```powershell
Write-Host '===== ipconfig ====='
ipconfig /all

Write-Host '===== 是否拥有 .4 ====='
Get-NetIPAddress -AddressFamily IPv4 |
  Where-Object { $_.IPAddress -eq '192.168.0.4' } |
  Format-List InterfaceAlias, IPAddress, PrefixOrigin, AddressState

Write-Host '===== ping 控制器 / 打印机（各 2 次）====='
Test-Connection -ComputerName 192.168.0.2 -Count 2 -ErrorAction SilentlyContinue |
  Select-Object Address, Status, Latency
Test-Connection -ComputerName 192.168.0.3 -Count 2 -ErrorAction SilentlyContinue |
  Select-Object Address, Status, Latency
```

判据：

| 结果 | 含义 |
|---|---|
| 没有任何已启用适配器是 `192.168.0.4` | 与 08-19 `Socket.Bind` 一致。**不要擅自加 IP。** 记下来，等人同意再配静态 `192.168.0.4/24`（掩码 `255.255.255.0`，不要网关抢默认路由） |
| `.4` 在，但 AddressState 不是 Preferred | 网卡禁用 / 断开 / 重复地址 |
| ping `.2` 失败 | 控制器网不通 |
| ping `.3` 失败 | 打印机网不通（可能仍能连控制器，打印仍会失败） |

把 `ipconfig /all` 全文保存到本机桌面或 `D:\OmniCam-shop-check\ipconfig.txt`（文件夹不存在就建）。

---

## 5. 检查 B — 打印路径和 bmp 是否平铺

同时查 `D:\CNC` 和 `D:\Label`。再搜 Excitech 配置里的路径（只读）：

```powershell
$stems = @('OHC_OH_D0_2', 'OHC_OH_D1_2')
$dirs  = @('D:\CNC', 'D:\Label')

Write-Host '===== 目录是否存在、根下 bmp ====='
foreach ($d in $dirs) {
  Write-Host "`n--- $d ---"
  if (-not (Test-Path $d)) { Write-Host 'MISSING'; continue }
  Get-ChildItem $d -File -ErrorAction SilentlyContinue |
    Select-Object Name, Length, LastWriteTime
  Get-ChildItem $d -Directory -ErrorAction SilentlyContinue |
    Select-Object Name
}

Write-Host '`n===== 当天两张图（根目录，不要 label 子目录）====='
foreach ($d in $dirs) {
  foreach ($s in $stems) {
    $p = Join-Path $d ($s + '.bmp')
    $nested = Join-Path $d (Join-Path 'label' ($s + '.bmp'))
    [pscustomobject]@{
      Path = $p
      Exists = Test-Path $p
      NestedWrongPlace = Test-Path $nested
    }
  }
}

Write-Host '`n===== 在常见位置搜 Excitech / PrintPicturePath ====='
$hints = @(
  "$env:APPDATA",
  "$env:LOCALAPPDATA",
  "$env:ProgramData",
  'C:\Program Files',
  'C:\Program Files (x86)',
  'C:\Excitech',
  'D:\'
)
Get-ChildItem $hints -Recurse -Include *.ini,*.xml,*.config,*.json,*.txt -ErrorAction SilentlyContinue |
  Select-String -Pattern 'PrintPicture|Print Picture|D:\\CNC|D:\\Label|192\.168\.0\.4' -SimpleMatch:$false |
  Select-Object -First 40 Path, Line
```

判据：

- 软件当天路径是 `D:\CNC`：`D:\CNC\OHC_OH_D0_2.bmp` 必须存在。
- `D:\CNC\label\OHC_OH_D0_2.bmp` **不算数**（多一层，V3.0 拼不出）。
- 若图只在 NC 旁边的 `label\`（例如 `C:\Users\azqrv\Documents\Rouge\...\label\`），记为「未摊平」，列出源路径。**不要自动覆盖打印目录，除非用户说拷进去。**

再解析当天 ANC 的全部 `LS11`：

```powershell
$anc = "C:\Users\azqrv\Documents\Rouge\22'6 Club Lounge\NC Files\22_OHC_Divider_Recut.anc"
if (-not (Test-Path -LiteralPath $anc)) {
  Get-ChildItem -LiteralPath 'C:\Users\azqrv\Documents' -Recurse -Filter '*.anc' -ErrorAction SilentlyContinue |
    Select-Object -First 30 FullName, LastWriteTime
} else {
  Select-String -LiteralPath $anc -Pattern "LS11='([^']+)'" |
    ForEach-Object { $_.Matches[0].Groups[1].Value }
}
```

每个 stem 都必须在 **当前 Print Picture Path 的根目录** 有 `{stem}.bmp`。

---

## 6. 检查 C — BMP 是否 1 位单色 600×600

不要用「用画图打开看看」。读文件头：

```powershell
function Get-BmpInfo([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) {
    return [pscustomobject]@{ Path = $Path; Exists = $false }
  }
  $b = [IO.File]::ReadAllBytes($Path)
  if ($b.Length -lt 30 -or [char]$b[0] -ne 'B' -or [char]$b[1] -ne 'M') {
    return [pscustomobject]@{ Path = $Path; Exists = $true; ValidBmp = $false; Bytes = $b.Length }
  }
  $bpp    = [BitConverter]::ToInt16($b, 28)
  $width  = [BitConverter]::ToInt32($b, 18)
  $height = [BitConverter]::ToInt32($b, 22)
  $off    = [BitConverter]::ToInt32($b, 10)
  [pscustomobject]@{
    Path     = $Path
    Exists   = $true
    ValidBmp = $true
    Bytes    = $b.Length
    Width    = $width
    Height   = [Math]::Abs($height)
    BitCount = $bpp
    OffBits  = $off
    ShopLike = ($width -eq 600 -and [Math]::Abs($height) -eq 600 -and $bpp -eq 1)
  }
}

$candidates = @()
foreach ($d in @('D:\CNC','D:\Label')) {
  if (Test-Path $d) {
    $candidates += Get-ChildItem $d -File -Filter '*.bmp' -ErrorAction SilentlyContinue
  }
}
foreach ($s in @('OHC_OH_D0_2','OHC_OH_D1_2')) {
  foreach ($d in @('D:\CNC','D:\Label')) {
    $candidates += Get-Item -LiteralPath (Join-Path $d ($s + '.bmp')) -ErrorAction SilentlyContinue
  }
}
$candidates | Sort-Object FullName -Unique | ForEach-Object { Get-BmpInfo $_.FullName } |
  Format-Table -AutoSize
```

判据：

| BitCount | 含义 |
|---|---|
| `1` 且 600×600 | 当前 OmniCam 目标（300 dpi，约 50.8 mm 画在 60×60 纸上） |
| `1` 且 236×157 | 旧车间样张。文件大约 **5086 字节**，能对上 `LS11`，打印可能偏小 |
| `24` | 旧彩图。工程师说热转印可能拒读。记为格式失败 |
| 其它尺寸 | 记下来，不要改文件 |

若打印目录里还是 24 位，而 OmniCam 新导出的 1bpp 还在 U 盘 / `Documents\...\label\`：报告「新图未摊平到打印路径」，列出两边路径和 BitCount。

---

## 7. 检查 D — Excitech 是否仍报 Bind（只观察）

若贴标软件开着：看窗口里的 Controller IP、本机绑定地址、Print Picture Path。截图路径记下来（用户可自己截图丢进对话）。

**不要点** 改 IP、不要点 Save Settings，除非用户明确要求。

若日志在软件目录，只读最近错误里是否还有：

`The requested address is not valid in its context`  
`Socket.Bind`

---

## 8. 操作员复测（你只指导，不替他按循环启动）

仅当 **A：本机已有 Preferred 的 `192.168.0.4`**，且 **B+C：打印路径根下有对应 1bpp bmp** 时，才请操作员：

1. Excitech 能 Connect Controller，无 `Socket.Bind`。
2. OSAI 选同一份 ANC，Process 2。
3. 看 `M701` 之后 `@I54` 是否变成 1，程序是否离开 `P2M701`。
4. 若离开：单步或慢放，看是否执行 `G90 G0 V… U…`，再看 `M702`。
5. **仍不要改 U/V NC。** 只记录实际运动方向（板宽 / 板长）和落点。

若 A 未过：不要跑 Process 2 浪费一张板；结论就是「仍是网卡绑定」。

---

## 9. 回报格式（必须按此粘贴）

```
## 环境
- 主机名 / 用户:
- 是否本机（非云端 VM）:
- 检查时间:

## A 网络
- 是否存在 192.168.0.4（适配器名 / AddressState）:
- ping 192.168.0.2:
- ping 192.168.0.3:
- ipconfig 关键摘录（标签网卡那一段）:

## B 路径与文件
- Excitech Print Picture Path（读到的）:
- D:\CNC 是否存在 / 根下是否有 OHC_OH_D0_2.bmp、OHC_OH_D1_2.bmp:
- D:\Label 是否存在 / 根下同名 bmp:
- 是否误放在 label\ 子目录:
- ANC 路径与全部 LS11:

## C BMP
- 每张：路径、宽、高、BitCount、字节数、是否 ShopLike:

## D 软件
- 是否仍 Socket.Bind:
- Connect Controller 当前状态:

## 结论（选一个）
- [ ] 仍卡在 .4 绑定 — 不要测 U/V
- [ ] 网络已通，但图不在打印路径根目录
- [ ] 图在，但是 24bpp / 尺寸不对
- [ ] 网络+图都过，可以让操作员再跑 Process 2 看 I54 / M701
- [ ] 已跑到 G0 V/U（记下坐标与实际方向）

## 下一步（只列建议，未授权不要执行）
- ...
```

把这份回报发回开这份手册的对话。改 OmniCam 代码的人需要的是 **A/B/C 的原始结果**，不是「好像网断了」这种概括。

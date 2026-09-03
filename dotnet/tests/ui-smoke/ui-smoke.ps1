# OmniCam UI smoke driver — launches the Release Desktop, drives it through UI Automation,
# captures screenshots and checks assertions. Windows only (WPF + UIA). Used by run-all.ps1
# and by the Windows CI job (non-blocking). Replaces the stale tests/manual/smoke_desktop.py.
#
# Steps file: one step per line, UTF-8.
#   invoke:<button name>        click a Button by its automation name
#   tab:<tab header>            select a TabItem
#   menu:<top>><item>           expand a top-level menu and click an item ('*' = leave open)
#   ctx:<button>><item>         click a button that opens a ContextMenu, then an item in it
#   wait:<ms>                   sleep
#   dismiss                     close any dialog window owned by the app (MessageBox etc.)
#   shot:<file.png>             screenshot the main window
#   assert-status:<substring>   status bar text must contain substring
#   assert-title:<substring>    window title must contain substring
#   assert-file:<path>          file must exist
#   assert-nofile:<path>        file must not exist
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$StepsFile,
    [string]$ShotDir = (Join-Path $PSScriptRoot '..\..\artifacts\ui-smoke'),
    [string]$AutoExportDir = '',
    [int]$StartupTimeoutSec = 90
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SmokeWin32 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[SmokeWin32]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$m) { $script:failures.Add($m); Write-Host "FAIL  $m" -ForegroundColor Red }
function Ok([string]$m) { Write-Host "ok    $m" }

if ($AutoExportDir) { $env:OMNICAM_AUTO_EXPORT_DIR = $AutoExportDir; New-Item -ItemType Directory -Force -Path $AutoExportDir | Out-Null }
$p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
$deadline = (Get-Date).AddSeconds($StartupTimeoutSec)
while ((Get-Date) -lt $deadline) { $p.Refresh(); if ($p.MainWindowHandle -ne 0 -and $p.MainWindowTitle -like 'OmniCam*') { break }; Start-Sleep -Milliseconds 300 }
$p.Refresh()
if ($p.MainWindowHandle -eq 0) { Fail 'main window did not appear'; if (-not $p.HasExited) { $p.Kill() }; exit 1 }
Start-Sleep -Milliseconds 2000
$h = $p.MainWindowHandle
[SmokeWin32]::ShowWindow($h, 9) | Out-Null
[SmokeWin32]::SetForegroundWindow($h) | Out-Null
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)

function Cond([string]$name, $type) {
    New-Object System.Windows.Automation.AndCondition (
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
}
function Find-ByName([string]$name, $type, [int]$timeoutMs = 6000) {
    $until = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $name $type))
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $until)
    return $null
}
function Find-RootByName([string]$name, $type) {
    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $name $type))
}
function Status-Text {
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'StatusText')
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($null -eq $el) { return '' }
    return $el.Current.Name
}
function Shot([string]$file) {
    [SmokeWin32]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 300
    $r = New-Object SmokeWin32+RECT
    [SmokeWin32]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    if ($w -le 0 -or $hh -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $path = Join-Path $ShotDir $file
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    Ok "shot $file"
}

$steps = Get-Content -Path $StepsFile -Encoding UTF8 | Where-Object { $_.Trim() -ne '' -and -not $_.StartsWith('#') }
foreach ($s in $steps) {
    $kind, $arg = $s -split ':', 2
    try {
        switch ($kind.Trim()) {
            'invoke' {
                $el = Find-ByName $arg ([System.Windows.Automation.ControlType]::Button)
                if ($null -eq $el) { Fail "button '$arg' not found"; continue }
                $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900; Ok "invoke $arg"
            }
            'tab' {
                $el = Find-ByName $arg ([System.Windows.Automation.ControlType]::TabItem)
                if ($null -eq $el) { Fail "tab '$arg' not found"; continue }
                $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 900; Ok "tab $arg"
            }
            'menu' {
                $top, $child = $arg -split '>', 2
                $el = Find-ByName $top ([System.Windows.Automation.ControlType]::MenuItem)
                if ($null -eq $el) { Fail "menu '$top' not found"; continue }
                $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600
                if ($child -and $child -ne '*') {
                    $sub = Find-RootByName $child ([System.Windows.Automation.ControlType]::MenuItem)
                    if ($null -eq $sub) { Fail "menu item '$child' not found"; continue }
                    $sub.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900
                }
                Ok "menu $arg"
            }
            'ctx' {
                $bn, $item = $arg -split '>', 2
                $el = Find-ByName $bn ([System.Windows.Automation.ControlType]::Button)
                if ($null -eq $el) { Fail "button '$bn' not found"; continue }
                $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700
                $mi = Find-RootByName $item ([System.Windows.Automation.ControlType]::MenuItem)
                if ($null -eq $mi) { Fail "menu item '$item' not found"; continue }
                $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700; Ok "ctx $arg"
            }
            'wait' { Start-Sleep -Milliseconds ([int]$arg) }
            'dismiss' {
                $dc = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
                foreach ($d in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $dc)) { try { $d.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch {} }
                Start-Sleep -Milliseconds 500
            }
            'shot' { Shot $arg }
            'assert-status' {
                $t = Status-Text
                if ($t -like "*$arg*") { Ok "status contains '$arg'" } else { Fail "status '$t' does not contain '$arg'" }
            }
            'assert-title' {
                $p.Refresh(); $t = $p.MainWindowTitle
                if ($t -like "*$arg*") { Ok "title contains '$arg'" } else { Fail "title '$t' does not contain '$arg'" }
            }
            'assert-file' {
                $f = [Environment]::ExpandEnvironmentVariables($arg)
                if (Test-Path $f) { Ok "file exists $f" } else { Fail "missing file $f" }
            }
            'assert-nofile' {
                $f = [Environment]::ExpandEnvironmentVariables($arg)
                if (-not (Test-Path $f)) { Ok "no file $f" } else { Fail "unexpected file $f" }
            }
            default { Fail "unknown step '$s'" }
        }
    }
    catch {
        Fail "step '$s' threw: $($_.Exception.Message)"
    }
}

try { $p.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800 } catch {}
# The unsaved-work prompt may hold the window open; the smoke never keeps state.
if (-not $p.HasExited) { try { $p.Kill() } catch {} }
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) failure(s)" -ForegroundColor Red; exit 1 }
Write-Host 'all steps passed' -ForegroundColor Green
exit 0

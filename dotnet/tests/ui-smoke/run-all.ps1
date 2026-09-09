# Runs every scenario in ./scenarios against the Release Desktop build.
# Usage: pwsh dotnet/tests/ui-smoke/run-all.ps1 [-Exe path] [-ShotDir path]
# Exit code = number of failed scenarios. Screenshots land in dotnet/artifacts/ui-smoke.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\CabinetNC.Desktop\bin\Release\net10.0-windows\CabinetNC.Desktop.exe'),
    [string]$ShotDir = (Join-Path $PSScriptRoot '..\..\artifacts\ui-smoke'),
    [string]$Only = ''
)
$ErrorActionPreference = 'Continue'
$Exe = [IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $Exe)) { Write-Error "Desktop exe not found: $Exe (build with: dotnet build src/CabinetNC.Desktop -c Release)"; exit 1 }
Get-Process CabinetNC.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$work = Join-Path ([IO.Path]::GetTempPath()) ("omnicam-ui-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$exportDir = Join-Path $work 'export'
# Each scenario gets a private library so the operator's real library.json is never touched
# and scenarios cannot leak recent files / display toggles into each other.
$libDir = Join-Path $work 'lib'
$scenarios = Get-ChildItem (Join-Path $PSScriptRoot 'scenarios') -Filter '*.txt' | Sort-Object Name
if ($Only) { $scenarios = $scenarios | Where-Object { $_.Name -like "*$Only*" } }
$failed = 0
$resultDir = Join-Path $ShotDir 'results'
New-Item -ItemType Directory -Force -Path $resultDir | Out-Null
Get-ChildItem $resultDir -Filter '*.json' -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($sc in $scenarios) {
    Write-Host "=== $($sc.Name) ===" -ForegroundColor Cyan
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $exportDir, $libDir | Out-Null
    $env:OMNICAM_LIBRARY_PATH = Join-Path $libDir 'library.json'
    & (Join-Path $PSScriptRoot 'ui-smoke.ps1') -Exe $Exe -StepsFile $sc.FullName -ShotDir $ShotDir -AutoExportDir $exportDir `
        -ResultJson (Join-Path $resultDir ($sc.BaseName + '.json'))
    if ($LASTEXITCODE -ne 0) { $failed++ }
    Get-Process CabinetNC.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Remove-Item Env:\OMNICAM_LIBRARY_PATH -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

# One summary file for evaluate-product.ps1 and CI: which scenarios passed.
$summary = [pscustomobject]@{
    schema = 'cabinetnc.ui-smoke'
    ranAt = (Get-Date).ToString('o')
    exe = $Exe
    passed = ($failed -eq 0)
    scenarios = @(Get-ChildItem $resultDir -Filter '*.json' | Sort-Object Name | ForEach-Object {
        $r = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        [pscustomobject]@{ scenario = $r.scenario; passed = $r.passed; failures = $r.failures }
    })
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $ShotDir 'results.json') -Encoding UTF8
Write-Host ("{0}/{1} scenario(s) passed" -f ($scenarios.Count - $failed), $scenarios.Count) -ForegroundColor ($(if ($failed -eq 0) { 'Green' } else { 'Red' }))
exit $failed

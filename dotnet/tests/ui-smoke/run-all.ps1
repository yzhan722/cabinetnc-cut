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
$skipped = @()
foreach ($sc in $scenarios) {
    Write-Host "=== $($sc.Name) ===" -ForegroundColor Cyan
    # Intranet scenarios need a live API (Docker stack); without one they are skipped, never faked.
    if ($sc.Name -like '*intranet*' -and -not $env:CABINETNC_SMOKE_API_URL) {
        Write-Host 'skipped: CABINETNC_SMOKE_API_URL not set (no intranet API available)' -ForegroundColor Yellow
        $skipped += $sc.BaseName
        continue
    }
    # Customer-build scenarios only make sense against the customer executable (Local mode compiled out).
    if ($sc.Name -like '*customer*' -and $env:CABINETNC_SMOKE_CUSTOMER_BUILD -ne '1') {
        Write-Host 'skipped: CABINETNC_SMOKE_CUSTOMER_BUILD is not 1 (not a customer build)' -ForegroundColor Yellow
        $skipped += $sc.BaseName
        continue
    }
    if ($sc.Name -notlike '*customer*' -and $env:CABINETNC_SMOKE_CUSTOMER_BUILD -eq '1') {
        Write-Host 'skipped: scenario assumes the developer build (Local mode / mode switch); customer build has neither' -ForegroundColor Yellow
        $skipped += $sc.BaseName
        continue
    }
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
    skipped = $skipped
    scenarios = @(Get-ChildItem $resultDir -Filter '*.json' | Sort-Object Name | ForEach-Object {
        $r = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        [pscustomobject]@{ scenario = $r.scenario; passed = $r.passed; failures = $r.failures }
    })
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $ShotDir 'results.json') -Encoding UTF8
Write-Host ("{0}/{1} scenario(s) passed, {2} skipped" -f ($scenarios.Count - $skipped.Count - $failed), ($scenarios.Count - $skipped.Count), $skipped.Count) -ForegroundColor ($(if ($failed -eq 0) { 'Green' } else { 'Red' }))
exit $failed

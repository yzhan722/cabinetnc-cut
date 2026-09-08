# Publishes the customer (intranet-only) Desktop build and verifies the package (strict).
#   pwsh dotnet/scripts/publish-customer.ps1 [-OutDir dist/CabinetNC-Cut-Customer]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot '..\..\dist\CabinetNC-Cut-Customer'),
    [switch] $PoC   # kept for compatibility; the verification has a single strict mode now
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }

Write-Host "publishing customer build -> $OutDir" -ForegroundColor Cyan
& dotnet publish (Join-Path $repo 'dotnet\src\CabinetNC.Desktop\CabinetNC.Desktop.csproj') -c Release -o $OutDir `
    -p:CustomerBuild=true -p:DebugType=none -p:DebugSymbols=false --verbosity minimal
if ($LASTEXITCODE -ne 0) { Write-Error 'publish failed'; exit 1 }

& (Join-Path $PSScriptRoot 'verify-customer-package.ps1') -PackageDir $OutDir @(if ($PoC) { '-PoC' })
exit $LASTEXITCODE

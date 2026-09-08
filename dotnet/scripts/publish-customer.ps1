# Publishes the customer (intranet-only) Desktop build, obfuscates the non-UI assemblies and verifies the package.
#   pwsh dotnet/scripts/publish-customer.ps1 [-OutDir dist/CabinetNC-Cut-Customer] [-SkipObfuscation]
# Requires the Obfuscar global tool:  dotnet tool install --global Obfuscar.GlobalTool
# The rename map is written to dist/obfuscation-maps/ (git-ignored) — keep it: it de-obfuscates customer stack traces.
param(
    [string] $OutDir = (Join-Path $PSScriptRoot '..\..\dist\CabinetNC-Cut-Customer'),
    [switch] $SkipObfuscation,
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

if (-not $SkipObfuscation) {
    $obfuscar = Get-Command obfuscar.console -ErrorAction SilentlyContinue
    if (-not $obfuscar) { Write-Error 'obfuscar.console not found: dotnet tool install --global Obfuscar.GlobalTool'; exit 1 }
    $template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\obfuscation\customer.obfuscar.xml'))
    $config = Join-Path ([IO.Path]::GetTempPath()) "cabinetnc-customer-obfuscar-$([Guid]::NewGuid().ToString('N')).xml"
    $obfDir = Join-Path $OutDir 'obfuscated'
    $configText = $template.Replace('<Var name="InPath" value="." />', "<Var name=`"InPath`" value=`"$OutDir`" />")
    $configText = $configText.Replace('<Var name="OutPath" value=".\obfuscated" />', "<Var name=`"OutPath`" value=`"$obfDir`" />")
    [IO.File]::WriteAllText($config, $configText)
    Write-Host 'obfuscating Desktop.Core / NestContract / Domain (public API kept, private members renamed, strings hidden)' -ForegroundColor Cyan
    & $obfuscar.Source $config | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { Write-Error 'obfuscation failed'; exit 1 }
    Remove-Item $config -Force

    $maps = Join-Path $repo 'dist\obfuscation-maps'
    New-Item -ItemType Directory -Force -Path $maps | Out-Null
    $rev = (& git -C $repo rev-parse --short HEAD).Trim()
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    Move-Item (Join-Path $obfDir 'Mapping.txt') (Join-Path $maps "customer-$rev-$stamp.map") -Force
    Get-ChildItem $obfDir -Filter *.dll | ForEach-Object { Copy-Item $_.FullName (Join-Path $OutDir $_.Name) -Force }
    Remove-Item -Recurse -Force $obfDir
    Write-Host "rename map: $maps\customer-$rev-$stamp.map (do not ship; needed to read customer stack traces)"
}

& (Join-Path $PSScriptRoot 'verify-customer-package.ps1') -PackageDir $OutDir
exit $LASTEXITCODE

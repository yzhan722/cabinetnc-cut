# Verifies a published customer package (Task 12 / design spec §14).
#
#   pwsh dotnet/scripts/verify-customer-package.ps1 -PackageDir dist/CabinetNC-Cut-Customer [-PoC]
#
# Exit 0 = PASS, 1 = FAIL. Checks (all must hold):
#   1. no deployable compute components: CabinetNC.ComputeWorker.*, CabinetNC.Compute.Core.dll, cloud server assemblies
#   2. no core manufacturing algorithm assembly  (CabinetNC.Domain.dll — see docs/security/CLIENT_CODE_PROTECTION.md;
#      with -PoC this is reported as a KNOWN GAP instead of a failure, because the PoC Desktop still needs Domain for its models)
#   3. no PDB / source / project files
#   4. no server secrets: .env*, connection strings, JWT signing keys, MinIO credentials (text and binary scan)
#   5. the Desktop executable itself is present
param(
    [Parameter(Mandatory)] [string] $PackageDir,
    [switch] $PoC
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PackageDir)) { Write-Error "package dir not found: $PackageDir"; exit 1 }
$PackageDir = (Resolve-Path $PackageDir).Path
$files = Get-ChildItem -Path $PackageDir -Recurse -File
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

function Fail([string] $m) { $script:failures.Add($m); Write-Host "FAIL  $m" -ForegroundColor Red }
function Warn([string] $m) { $script:warnings.Add($m); Write-Host "GAP   $m" -ForegroundColor Yellow }
function Ok([string] $m)   { Write-Host "ok    $m" -ForegroundColor Green }

# 1. deployable compute components / server-side assemblies
$forbidden = @(
    'CabinetNC.ComputeWorker.exe', 'CabinetNC.ComputeWorker.dll', 'CabinetNC.ComputeWorker.runtimeconfig.json',
    'CabinetNC.Compute.Core.dll',
    'CabinetNC.Cloud.Api.dll', 'CabinetNC.Cloud.Worker.dll', 'CabinetNC.Cloud.Infrastructure.dll',
    'Microsoft.EntityFrameworkCore.dll', 'Npgsql.dll', 'Minio.dll'
)
foreach ($name in $forbidden) {
    $hit = $files | Where-Object { $_.Name -ieq $name }
    if ($hit) { Fail "forbidden compute/server component present: $($hit.FullName.Substring($PackageDir.Length + 1) -join ', ')" }
}
if (-not ($files | Where-Object { $forbidden -icontains $_.Name })) { Ok 'no ComputeWorker / Compute.Core / cloud server assemblies' }

# 2. core manufacturing algorithm assembly
$domain = $files | Where-Object { $_.Name -ieq 'CabinetNC.Domain.dll' }
if ($domain) {
    $msg = 'CabinetNC.Domain.dll is shipped: it still contains the nesting (BLF/NFP), CAM and post-processor algorithms'
    if ($PoC) { Warn "$msg (accepted as a known PoC gap; see CLIENT_CODE_PROTECTION.md)" } else { Fail $msg }
} else { Ok 'no core algorithm assembly' }

# 3. PDB / source / project files
$leaks = $files | Where-Object { $_.Extension -in '.pdb', '.cs', '.xaml', '.csproj', '.slnx', '.sln', '.proto' }
if ($leaks) { Fail "debug symbols or sources present: $(($leaks | Select-Object -First 8 | ForEach-Object { $_.Name }) -join ', ')$(if ($leaks.Count -gt 8) { ' …' })" }
else { Ok 'no PDB / source / project files' }

# 4. server secrets
$envFiles = $files | Where-Object { $_.Name -like '.env*' -or $_.Name -like '*.env' }
if ($envFiles) { Fail "environment files present: $(($envFiles | ForEach-Object { $_.Name }) -join ', ')" }
$secretPatterns = @(
    'CABINETNC_JWT_SIGNING_KEY\s*=\s*\S{8,}',
    'CABINETNC_BOOTSTRAP_ADMIN_PASSWORD\s*=\s*\S+',
    'CABINETNC_OBJECTSTORE_SECRET_KEY\s*=\s*\S+',
    'MINIO_ROOT_PASSWORD\s*=\s*\S+',
    'POSTGRES_PASSWORD\s*=\s*\S+',
    'Host=[^;]+;.*Password=[^;]+',
    'AccessKey\s*[:=]\s*"?\S{8,}'
)
$textExt = '.json', '.xml', '.config', '.txt', '.md', '.yml', '.yaml', '.ini', '.ps1', '.cmd', '.bat'
$secretHits = @()
foreach ($f in $files) {
    $isText = $textExt -contains $f.Extension
    $isBinary = $f.Extension -in '.dll', '.exe'
    if (-not ($isText -or $isBinary)) { continue }
    $content = if ($isText) { Get-Content -Raw -Path $f.FullName -ErrorAction SilentlyContinue }
               else { [System.Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($f.FullName)) }
    if (-not $content) { continue }
    foreach ($p in $secretPatterns) {
        if ($content -match $p) { $secretHits += "$($f.Name): /$p/"; break }
    }
}
if ($secretHits) { Fail "possible embedded secrets: $($secretHits -join '; ')" } else { Ok 'no server secrets (env files, connection strings, signing keys, object-store credentials)' }

# 5. the product itself
if ($files | Where-Object { $_.Name -ieq 'CabinetNC.Desktop.exe' }) { Ok 'CabinetNC.Desktop.exe present' } else { Fail 'CabinetNC.Desktop.exe missing' }
if ($files | Where-Object { $_.Name -ieq 'CabinetNC.Desktop.Core.dll' }) { Ok 'CabinetNC.Desktop.Core.dll (intranet client) present' } else { Fail 'CabinetNC.Desktop.Core.dll missing' }

Write-Host ''
Write-Host ("package: {0}  files: {1}  size: {2:0.0} MiB" -f $PackageDir, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))
Write-Host ("assemblies: {0}" -f (($files | Where-Object { $_.Extension -eq '.dll' -and $_.Name -like 'CabinetNC.*' } | ForEach-Object { $_.Name }) -join ', '))
if ($failures.Count -eq 0) {
    Write-Host ("RESULT: PASS{0}" -f $(if ($warnings.Count -gt 0) { " (with $($warnings.Count) known gap(s))" } else { '' })) -ForegroundColor Green
    exit 0
}
Write-Host "RESULT: FAIL ($($failures.Count) failure(s))" -ForegroundColor Red
exit 1

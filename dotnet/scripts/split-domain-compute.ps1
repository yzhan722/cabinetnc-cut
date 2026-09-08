# One-off refactoring helper for P2-5: moves algorithm types from CabinetNC.Domain into
# CabinetNC.Domain.Compute, splitting files that mix models and algorithms by top-level type.
# Kept in the repo as documentation of exactly what moved; not part of any build.
param([string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)
$ErrorActionPreference = 'Stop'
$domain = Join-Path $Repo 'dotnet\src\CabinetNC.Domain'
$compute = Join-Path $Repo 'dotnet\src\CabinetNC.Domain.Compute'
New-Item -ItemType Directory -Force -Path (Join-Path $compute 'Nesting'), (Join-Path $compute 'Manufacturing') | Out-Null

function Split-TopLevel([string]$relative, [string[]]$move) {
    $src = Join-Path $domain $relative
    $lines = [IO.File]::ReadAllLines($src)
    # Find top-level type declarations (column 0) and the start of their leading comment/attribute block.
    $decls = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^(public|internal)\s+(static\s+|sealed\s+|abstract\s+|readonly\s+|partial\s+)*(class|interface|record struct|record|struct|enum)\s+(\w+)') {
            $name = $Matches[4]   # captured before the comment scan below overwrites $Matches
            $start = $i
            while ($start -gt 0 -and ($lines[$start - 1] -match '^\s*///' -or $lines[$start - 1] -match '^\s*\[')) { $start-- }
            $decls += [pscustomobject]@{ Name = $name; Start = $start; Decl = $i }
        }
    }
    if ($decls.Count -eq 0) { throw "no top-level types in $relative" }
    $header = $lines[0..($decls[0].Start - 1)]
    $keepOut = New-Object System.Collections.Generic.List[string]
    $moveOut = New-Object System.Collections.Generic.List[string]
    $keepOut.AddRange([string[]]$header); $moveOut.AddRange([string[]]$header)
    for ($d = 0; $d -lt $decls.Count; $d++) {
        $end = if ($d + 1 -lt $decls.Count) { $decls[$d + 1].Start - 1 } else { $lines.Count - 1 }
        $block = $lines[$decls[$d].Start..$end]
        if ($move -contains $decls[$d].Name) { $moveOut.AddRange([string[]]$block) } else { $keepOut.AddRange([string[]]$block) }
    }
    $moved = $decls | Where-Object { $move -contains $_.Name } | ForEach-Object { $_.Name }
    $kept = $decls | Where-Object { $move -notcontains $_.Name } | ForEach-Object { $_.Name }
    if (-not $moved) { throw "nothing matched to move in $relative" }
    $dst = Join-Path $compute $relative
    [IO.File]::WriteAllLines($dst, $moveOut, (New-Object System.Text.UTF8Encoding($false)))
    if ($kept) {
        [IO.File]::WriteAllLines($src, $keepOut, (New-Object System.Text.UTF8Encoding($false)))
    } else {
        git -C $Repo rm -q --cached -- "dotnet/src/CabinetNC.Domain/$($relative -replace '\\','/')" | Out-Null
        Remove-Item $src
    }
    Write-Host ("split {0,-36} moved: {1}  kept: {2}" -f $relative, ($moved -join ','), ($kept -join ','))
}

function Move-Whole([string]$relative) {
    $from = "dotnet/src/CabinetNC.Domain/$($relative -replace '\\','/')"
    $to = "dotnet/src/CabinetNC.Domain.Compute/$($relative -replace '\\','/')"
    git -C $Repo mv -f $from $to
    Write-Host "moved $relative"
}

Split-TopLevel 'Nesting\BlfNester.cs' @('BlfNester')
Split-TopLevel 'Nesting\INestingEngine.cs' @('BlfNestingEngine', 'AdvancedNestingEngineStub', 'NestEngineRouter')
Split-TopLevel 'Nesting\GroupedBlfNester.cs' @('GroupedBlfNester')
Split-TopLevel 'Manufacturing\OpsPlanner.cs' @('OpsPlanner')
Split-TopLevel 'Manufacturing\SheetBundleBuilder.cs' @('GenericMmPostProcessor', 'FanucLikePostProcessor')

foreach ($f in 'Nesting\ClipperNfpNestingEngine.cs', 'Nesting\DeepnestPreviewNestingEngine.cs', 'Nesting\PartsInPartPacker.cs',
                'Nesting\SheetStabilityOptimizer.cs', 'Manufacturing\PocketClearer.cs', 'Manufacturing\PocketClearIslands.cs',
                'Manufacturing\CamPipeline.cs', 'Manufacturing\NcEmitter.cs', 'Manufacturing\NcEmitter.Troy.cs') {
    Move-Whole $f
}
Write-Host 'done'

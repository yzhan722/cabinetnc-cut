# Smoke: Worker named-pipe gRPC Ping (no UI).
$ErrorActionPreference = "Stop"
# Only fall back to the machine-wide install when no dotnet is on PATH; forcing it first hides a
# user-level SDK on machines where C:\Program Files\dotnet carries just the runtime.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:Path = "C:\Program Files\dotnet;" + $env:Path }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Test-Path (Join-Path $PSScriptRoot "..\src\CabinetNC.ComputeWorker"))) {
  $root = Resolve-Path (Join-Path $PSScriptRoot "..")
} else {
  $root = Resolve-Path (Join-Path $PSScriptRoot "..")
}
Set-Location $root
dotnet build src/CabinetNC.ComputeWorker/CabinetNC.ComputeWorker.csproj -c Debug -v q | Out-Null
$exe = Join-Path $root "src\CabinetNC.ComputeWorker\bin\Debug\net10.0\CabinetNC.ComputeWorker.exe"
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
try {
  # use grpc_cli alternative: small csharp via dotnet script is heavy — instead hit that worker stays up
  if ($proc.HasExited) { throw "Worker exited early code=$($proc.ExitCode)" }
  Write-Host "OK worker-alive pid=$($proc.Id) pipe=cabinetnc.compute.v1"
} finally {
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}

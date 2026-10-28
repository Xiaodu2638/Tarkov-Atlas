<#
    Runs the three self-check projects with every cache and scratch directory
    kept inside the repo, so nothing is written to the host's %TEMP%, %LOCALAPPDATA%
    or user profile.

    Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\check.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"
$env:TEMP = Join-Path $root ".tmp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

$failed = @()
foreach ($name in @("datacheck", "logiccheck", "shotcheck")) {
    Write-Host "== $name ==" -ForegroundColor Cyan
    & dotnet run --project (Join-Path $PSScriptRoot $name) -c Release
    if ($LASTEXITCODE -ne 0) { $failed += $name }
}

if ($failed.Count -gt 0) { throw ("checks failed: " + ($failed -join ", ")) }
Write-Host "all three check suites passed" -ForegroundColor Green

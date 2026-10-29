<#
    Tarkov Atlas build script.

    Publishes a self-contained win-x64 build into dist\TarkovAtlas-win-x64 and
    prints the EXE path. NativeAOT is attempted first; if the ILCompiler step
    fails it retries without AOT so the output folder is always runnable.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$NoAot
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root "dist" }

$project = Join-Path $root "TarkovAtlas\TarkovAtlas.csproj"
$target  = Join-Path $OutputRoot "TarkovAtlas-$Runtime"

$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"
# keep dotnet/MSBuild scratch inside the repo instead of %TEMP%
$env:TEMP = Join-Path $root ".tmp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

function Invoke-Publish([bool]$aot) {
    $staging = Join-Path $root ("obj\_publish_" + $(if ($aot) { "aot" } else { "nonaot" }))
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    $publishArgs = @(
        "publish", $project,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "--nologo",
        "-p:PublishDir=$staging\"
    )
    if (-not $aot) { $publishArgs += @("-p:PublishAot=false", "-p:PublishTrimmed=false") }
    Write-Host "  publishing (AOT=$aot) ..." -ForegroundColor DarkGray
    & dotnet @publishArgs | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    # A successful AOT publish has no CoreCLR runtime beside the exe. If it is
    # there, MSBuild quietly produced a self-contained build; treat it as a
    # failed AOT attempt instead of shipping it as "native".
    if ($aot -and $ok -and (Test-Path (Join-Path $staging "coreclr.dll"))) {
        $ok = $false
    }
    return @{ Ok = $ok; Dir = $staging; Aot = $aot }
}

Write-Host "Tarkov Atlas publish" -ForegroundColor Cyan
Write-Host "  project: $project"

$result = $null
if (-not $NoAot) {
    $result = Invoke-Publish $true
    if (-not $result.Ok) {
        Write-Host "  NativeAOT failed, retrying as self-contained (no AOT)" -ForegroundColor Yellow
        $result = $null
    }
}
if ($null -eq $result) { $result = Invoke-Publish $false }
if (-not $result.Ok) { throw "dotnet publish failed" }

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem $result.Dir -File | Where-Object { $_.Extension -ne ".pdb" } |
    Copy-Item -Destination $target -Force
# Scripts/ is embedded in the assembly (OcrService), so it is not shipped.
foreach ($sub in @("Assets", "docs")) {
    $src = Join-Path $result.Dir $sub
    if (Test-Path $src) { Copy-Item $src $target -Recurse -Force }
}

$exe = Join-Path $target "TarkovAtlas.exe"
$required = @(
    "Assets\quests.json",
    "Assets\Maps\mapdata.json",
    "Assets\Maps\hazards.json",
    "Assets\Maps\layers\index.json"
)
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $target $_)) })
if (-not (Test-Path $exe)) { throw "publish produced no EXE in $target" }
if ($missing.Count -gt 0) { throw ("missing assets in output: " + ($missing -join ", ")) }

$sizeMb = [Math]::Round((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$zip = Join-Path $OutputRoot "TartovAtlas-$Runtime.zip".Replace("TartovAtlas","TarkovAtlas")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $target "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  folder: $target ($sizeMb MB)"
Write-Host "  mode:   $(if ($result.Aot) { 'NativeAOT (no coreclr.dll)' } else { 'self-contained (no AOT)' })"
Write-Host "  EXE:    $exe" -ForegroundColor Green

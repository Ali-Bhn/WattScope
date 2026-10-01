<#
.SYNOPSIS
  Publishes WattScope.Probe as a self-contained, single-file Windows x64
  binary and places it where Tauri's `externalBin` expects a sidecar:
  src-tauri/binaries/wattscope-probe-<target-triple>.exe

  Run this before `npm run tauri dev` / `npm run tauri build` -- Tauri does
  not build the .NET side itself.
#>
param(
    [string]$Configuration = "Release",
    [string]$TargetTriple = "x86_64-pc-windows-msvc"
)

$ErrorActionPreference = "Stop"

$desktopDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $desktopDir
$probeProject = Join-Path $repoRoot "src\WattScope.Probe\WattScope.Probe.csproj"
$binariesDir = Join-Path $desktopDir "src-tauri\binaries"
New-Item -ItemType Directory -Force -Path $binariesDir | Out-Null

$publishDir = Join-Path $env:TEMP "wattscope-probe-publish"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Write-Host "Publishing WattScope.Probe ($Configuration, win-x64, self-contained, single file)..."
dotnet publish $probeProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$dest = Join-Path $binariesDir "wattscope-probe-$TargetTriple.exe"
Copy-Item (Join-Path $publishDir "WattScope.Probe.exe") $dest -Force
Write-Host "Sidecar binary ready: $dest"

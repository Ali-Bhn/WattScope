<#
.SYNOPSIS
  Measures the incremental background cost of Milestone 005 game detection
  with the packaged app, as a same-binary A/B (so only detection differs):
    1. detection OFF   (WATTSCOPE_GAME_DETECTION=off)  -- monitoring only
    2. detection ON, no game running
    3. detection ON, a (mapped, harmless) test game running -> an active
       gaming session is being attributed during the whole measurement
  Each runs desktop/scripts/measure-background.ps1 (visible + hidden phases)
  against a scratch database (WATTSCOPE_DB), so real history is untouched.
  CPU is % of ONE core; single runs on a busy desktop are indicative only.
  It does NOT measure gaming frame rate.
#>
param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope.exe"),
    [string]$ProbeExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope-probe.exe"),
    [string]$WorkDir = (Join-Path $env:TEMP "wattscope-overhead"),
    [int]$Seconds = 60
)
$ErrorActionPreference = "Stop"
$measure = Join-Path $PSScriptRoot "measure-background.ps1"
if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$testGame = Join-Path $WorkDir "wattscope-testgame.exe"
Copy-Item (Join-Path $env:WINDIR "System32\PING.EXE") $testGame

function Run($label, $detection, [bool]$withGame) {
    Write-Host "`n##### $label #####"
    $db = Join-Path $WorkDir ("db-" + ($label -replace '\W', '') + ".db")
    $env:WATTSCOPE_DB = $db
    $env:WATTSCOPE_GAME_DETECTION = $detection
    $game = $null
    try {
        if ($withGame) {
            & $ProbeExe --game-mark "wattscope-testgame.exe" --name "Overhead Test Game" --db $db | Out-Null
            $game = Start-Process -FilePath $testGame -ArgumentList "-n", "400", "127.0.0.1" -WindowStyle Hidden -PassThru
        }
        & $measure -AppExe $AppExe -Seconds $Seconds
        if ($withGame) { & $ProbeExe --gaming --db $db | Select-Object -First 3 | Write-Host }
    }
    finally {
        $env:WATTSCOPE_DB = $null
        $env:WATTSCOPE_GAME_DETECTION = $null
        if ($game -and -not $game.HasExited) { $game.Kill() }
    }
}

Run "1 detection OFF" "off" $false
Run "2 detection ON idle" "on" $false
Run "3 detection ON with an active gaming session" "on" $true

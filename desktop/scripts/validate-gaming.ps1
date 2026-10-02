<#
.SYNOPSIS
  Real, non-invasive validation of Milestone 005 (gaming session detection)
  against the packaged WattScope app on Windows. It launches the actual
  release exe -- but with WATTSCOPE_DB pointed at a scratch database that is
  a COPY of yours, so your real history is never written to. (A LOCALAPPDATA
  override would NOT isolate it: the database path comes from the Windows
  known-folder API.)

  It uses harmless stand-in processes (copies of ping.exe / powershell.exe
  renamed for each scenario). It never launches a game, never touches
  Battle.net or any launcher, and never injects into or inspects anything.

  Scenarios: (A) ordinary non-game programs, (B) a launcher-named process
  alone, (C) a launcher-named process with a helper in its own folder,
  (D) a user-mapped test executable -> one gaming session, ended after the
  grace period, (E) the same while the window is hidden in the tray,
  (F) the host dying mid-game -> the session is closed cleanly, then history
  persists across a restart. Optionally (-BaselineProbe) the migration of
  your real M004 database copy (schema 3 -> 4) is checked.

  What it CANNOT check: a real game, the native tray menu, real sleep.
  See docs/MANUAL_VALIDATION.md section 7 and docs/PROJECT_STATE.md.
#>
param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope.exe"),
    [string]$ProbeExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope-probe.exe"),
    [string]$BaselineProbe = "",
    [string]$WorkDir = (Join-Path $env:TEMP "wattscope-gaming-validation"),
    [string]$SeedDb = (Join-Path $env:LOCALAPPDATA "WattScope\wattscope.db")
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_scratch-db.ps1")   # Get-WattScopeSidecar, Assert-NoWattScopeRunning
Assert-NoWattScopeRunning
$AppExe = (Resolve-Path $AppExe).Path
$ProbeExe = (Resolve-Path $ProbeExe).Path
$results = New-Object System.Collections.Generic.List[object]

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GWin {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    static bool IsMain(IntPtr h) { var sb = new System.Text.StringBuilder(64); GetWindowText(h, sb, 64); return sb.ToString() == "WattScope"; }
    public static int Visible(uint pid) { int n = 0; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && IsMain(h)) n++; return true; }, IntPtr.Zero); return n; }
    public static void Close(uint pid) { EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && IsMain(h)) PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero); }
}
"@

function Check($name, [bool]$ok, $detail = "") {
    $results.Add([pscustomobject]@{ Check = $name; Result = $(if ($ok) { "PASS" } else { "FAIL" }); Detail = $detail })
    Write-Host ("[{0}] {1}  {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail)
}
function WaitFor([scriptblock]$cond, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 300 }
    return $false
}
function App { ,@(Get-Process -Name "wattscope" -ErrorAction SilentlyContinue) }
function Sidecar { ,@(Get-WattScopeSidecar) }   # by command line, never by name: the Probe CLI below is the same exe

# ---- isolated environment -------------------------------------------------
if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
$iso = Join-Path $WorkDir "localappdata"
$dbDir = Join-Path $iso "WattScope"
New-Item -ItemType Directory -Force -Path $dbDir | Out-Null
$Db = Join-Path $dbDir "wattscope.db"
$seeded = $false
if (Test-Path $SeedDb) {
    foreach ($ext in "", "-wal", "-shm") { if (Test-Path ($SeedDb + $ext)) { Copy-Item ($SeedDb + $ext) ($Db + $ext) } }
    $seeded = $true
}

function Probe { $ErrorActionPreference = "SilentlyContinue"; & $ProbeExe @args --db $Db 2>$null | Out-String }
function GamingSessions {
    $rows = @()
    $lines = (Probe --gaming) -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s+#(\d+) (.+?)  (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) -> (.+?)  duration (\d+:\d\d:\d\d)  monitored (\d+:\d\d:\d\d)  \[(\w+)(?:: (\w+))?\]  (.+)$') {
            $next = if ($i + 1 -lt $lines.Count) { $lines[$i + 1] } else { "" }
            $rows += [pscustomobject]@{
                Id = [int]$Matches[1]; Name = $Matches[2]; Start = [datetime]$Matches[3]; End = $Matches[4]
                Duration = [timespan]$Matches[5]; Monitored = [timespan]$Matches[6]; Status = $Matches[7]; Reason = $Matches[8]; Source = $Matches[9]
                Completeness = $(if ($next -match 'completeness (\w+);') { $Matches[1] } else { "" }); Detail = $next.Trim()
            }
        }
    }
    $rows
}
function Spawn($exe, $argList, $hidden = $true) {
    $a = @{ FilePath = $exe; PassThru = $true }
    if ($argList -and @($argList).Count -gt 0) { $a.ArgumentList = @($argList) }
    if ($hidden) { $a.WindowStyle = "Hidden" }
    Start-Process @a
}

$games = Join-Path $WorkDir "stand-ins"
$launcherDir = Join-Path $games "launcher"
New-Item -ItemType Directory -Force -Path $games, $launcherDir | Out-Null
$ping = Join-Path $env:WINDIR "System32\PING.EXE"
$psExe = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
$testGame = Join-Path $games "wattscope-testgame.exe";     Copy-Item $ping $testGame
$unmapped = Join-Path $games "unmapped-render-tool.exe";   Copy-Item $ping $unmapped
$launcher = Join-Path $launcherDir "Battle.net.exe";       Copy-Item $psExe $launcher          # stand-in NAME only; never the real launcher
$helper = Join-Path $launcherDir "launcher-updater-tool.exe"; Copy-Item $ping $helper

Write-Host "== Preconditions =="
Check "no WattScope process is running (this script never stops yours)" ((App).Count -eq 0 -and (Sidecar).Count -eq 0)
if ((App).Count -ne 0 -or (Sidecar).Count -ne 0) { Write-Host "Close WattScope (tray -> Exit) and run again."; exit 2 }
Check "the real Battle.net / game launchers are not touched (only observed): real Battle.net running now?" $true ("running=" + [bool](Get-Process -Name "Battle.net" -ErrorAction SilentlyContinue))
Check "the packaged sidecar has the Milestone 005 CLI" ((Probe --help) -match '--gaming')

# ---- real migration check on the copy of the user's M004 database ----------
if ($seeded -and $BaselineProbe -ne "" -and (Test-Path $BaselineProbe)) {
    Write-Host "`n== Migration of a real M004 database copy =="
    $before = (& (Resolve-Path $BaselineProbe).Path --db-info --db $Db 2>$null | Out-String)
    $beforeSchema = if ($before -match 'chema(?: version)?:?\s+(\d+)') { [int]$Matches[1] } else { -1 }
    $beforeSessions = if ($before -match 'Sessions:?\s+(\d+)') { [int]$Matches[1] } else { -1 }
    $after = Probe --db-info
    $afterSchema = if ($after -match 'chema(?: version)?:?\s+(\d+)') { [int]$Matches[1] } else { -1 }
    $afterSessions = if ($after -match 'Sessions:?\s+(\d+)') { [int]$Matches[1] } else { -1 }
    Check "M004 build reported schema 3 on the database copy" ($beforeSchema -eq 3) "schema=$beforeSchema"
    Check "M005 build migrated it to schema 4 and kept every monitoring session" ($afterSchema -eq 4 -and $afterSessions -eq $beforeSessions) "schema=$afterSchema sessions $beforeSessions -> $afterSessions"
    Check "integrity check ok after migration" ($after -match 'ok')
} else {
    Write-Host "`n(migration check skipped: needs an existing database and -BaselineProbe)"
}

Write-Host "`n== Mapping the test executable (CLI) =="
Probe --game-mark "wattscope-testgame.exe" --name "WattScope Test Game" | Out-Null
Check "mapping stored" ((Probe --gaming) -match 'wattscope-testgame.exe: game as "WattScope Test Game"')
$initialSessions = @(GamingSessions).Count

# ---- launch the packaged app on the scratch database ------------------------
Write-Host "`n== Launch (packaged app, scratch database via WATTSCOPE_DB) =="
$env:WATTSCOPE_DB = $Db
try {
    $app = Start-Process -FilePath $AppExe -PassThru
    Check "one host + one sidecar after launch" (WaitFor { (App).Count -eq 1 -and (Sidecar).Count -eq 1 } 30)
    Check "monitoring is running (an Active session)" (WaitFor { (Probe --sessions) -match '\bActive\b' } 30)
    Start-Sleep -Seconds 8   # let the first polls run

    # ---- A: ordinary non-game programs
    Write-Host "`n== A. Ordinary non-game programs for 40 s =="
    $np = Spawn "notepad.exe" $null $false
    $um = Spawn $unmapped @("-n", "60", "127.0.0.1")
    Start-Sleep -Seconds 40
    Check "A: notepad + an unmapped tool created no gaming session" (@(GamingSessions).Count -eq $initialSessions) ("sessions=" + @(GamingSessions).Count)
    foreach ($p in @($np, $um)) { try { if (-not $p.HasExited) { $p.Kill() } } catch {} }

    # ---- B: a launcher-named process alone
    Write-Host "`n== B. A launcher-named process alone for 50 s =="
    $lp = Spawn $launcher @("-NoProfile", "-Command", "Start-Sleep 55")
    Start-Sleep -Seconds 50
    Check "B: a launcher (Battle.net-named stand-in) running alone created no gaming session" (@(GamingSessions).Count -eq $initialSessions)

    # ---- C: launcher + helper inside the launcher's own folder
    Write-Host "`n== C. Launcher with a helper in its own folder for 45 s =="
    $hp = Spawn $helper @("-n", "50", "127.0.0.1")
    Start-Sleep -Seconds 45
    Check "C: the launcher's own-folder helper created no gaming session" (@(GamingSessions).Count -eq $initialSessions)
    foreach ($p in @($hp, $lp)) { try { if (-not $p.HasExited) { $p.Kill() } } catch {} }
    Start-Sleep -Seconds 3

    # ---- D: the mapped test executable
    Write-Host "`n== D. Mapped test executable (the 'game') =="
    $t0 = Get-Date
    $g = Spawn $testGame @("-n", "75", "127.0.0.1")
    Check "D: a gaming session appears after the confirm delay" (WaitFor { @(GamingSessions | Where-Object { $_.Status -eq "Active" }).Count -eq 1 } 60)
    $live = @(GamingSessions | Where-Object { $_.Status -eq "Active" })
    if ($live.Count -eq 1) {
        $latency = ((Get-Date) - $t0).TotalSeconds
        Check "D: exactly one active gaming session, correct name, detected via the user mapping" ($live[0].Name -eq "WattScope Test Game" -and $live[0].Source -match 'UserMapping') ($live[0].Name + " / " + $live[0].Source)
        Check "D: it is dated from the process start (not from confirmation)" ([math]::Abs(($live[0].Start - $t0).TotalSeconds) -lt 12) ("start offset " + [math]::Round(($live[0].Start - $t0).TotalSeconds, 1) + " s, detected after ~" + [math]::Round($latency, 0) + " s")
    }
    $g.WaitForExit()
    $tExit = Get-Date
    Check "D: still one Active session right after the process exits (grace period)" (@(GamingSessions | Where-Object { $_.Status -eq "Active" }).Count -eq 1)
    Check "D: the session ends after the grace period (ProcessExited)" (WaitFor { @(GamingSessions | Where-Object { $_.Status -eq "Ended" -and $_.Reason -eq "ProcessExited" }).Count -ge 1 } 60)
    $done = @(GamingSessions | Where-Object { $_.Status -eq "Ended" -and $_.Name -eq "WattScope Test Game" } | Select-Object -First 1)
    if ($done.Count -eq 1) {
        $endLocal = [datetime]($done[0].End)
        Check "D: ended at the time the process was found missing, not 30 s later" ([math]::Abs(($endLocal - $tExit).TotalSeconds) -lt 12) ("end offset " + [math]::Round(($endLocal - $tExit).TotalSeconds, 1) + " s")
        Check "D: monitored time is (almost) the whole duration -- no gaps" (($done[0].Duration - $done[0].Monitored).TotalSeconds -lt 8) ("duration " + $done[0].Duration + " monitored " + $done[0].Monitored)
        Check "D: completeness is Partial or Complete -- never an invented value" ($done[0].Completeness -in @("Partial", "Complete", "None")) ($done[0].Completeness + " | " + $done[0].Detail)
        Write-Host ("     " + $done[0].Detail)
    }
    $afterD = @(GamingSessions).Count
    Check "D: exactly one new gaming session in total" ($afterD -eq $initialSessions + 1) ("sessions=" + $afterD)

    # ---- E: hidden in the tray
    Write-Host "`n== E. Same, with the window hidden in the tray =="
    [GWin]::Close([uint32]$app.Id)
    Check "E: closing the window hides it (window gone, host + sidecar alive)" (WaitFor { [GWin]::Visible([uint32]$app.Id) -eq 0 } 10 -and (App).Count -eq 1 -and (Sidecar).Count -eq 1)
    $g2 = Spawn $testGame @("-n", "60", "127.0.0.1")
    Check "E: a session is detected while the window is hidden" (WaitFor { @(GamingSessions | Where-Object { $_.Status -eq "Active" }).Count -eq 1 } 60)
    $g2.WaitForExit()
    Check "E: and it ends" (WaitFor { @(GamingSessions | Where-Object { $_.Status -eq "Active" }).Count -eq 0 } 60)
    Check "E: two gaming sessions recorded in total, one active monitoring session still" (@(GamingSessions).Count -eq $initialSessions + 2 -and ((Probe --sessions) -match '\bActive\b'))

    # ---- F: the host dies mid-game; history persists
    Write-Host "`n== F. Host killed mid-game, then restart =="
    $g3 = Spawn $testGame @("-n", "120", "127.0.0.1")
    Check "F: a session is active" (WaitFor { @(GamingSessions | Where-Object { $_.Status -eq "Active" }).Count -eq 1 } 60)
    Stop-Process -Id $app.Id -Force
    Check "F: the sidecar exits by itself (no orphan)" (WaitFor { (Sidecar).Count -eq 0 } 20)
    Start-Sleep -Seconds 2
    $after = @(GamingSessions)
    Check "F: the in-flight gaming session was closed, not left Active" (@($after | Where-Object { $_.Status -eq "Active" }).Count -eq 0) (($after | Select-Object -Last 1).Status + ": " + ($after | Select-Object -Last 1).Reason)
    try { if (-not $g3.HasExited) { $g3.Kill() } } catch {}

    $app2 = Start-Process -FilePath $AppExe -PassThru
    Check "F: relaunch works (one host + one sidecar)" (WaitFor { (App).Count -eq 1 -and (Sidecar).Count -eq 1 } 30)
    Start-Sleep -Seconds 6
    Check "F: gaming history persisted across the restart" (@(GamingSessions).Count -eq $initialSessions + 3) ("sessions=" + @(GamingSessions).Count)
    Check "F: only one Active monitoring session" (@([regex]::Matches((Probe --sessions), '\bActive\b')).Count -eq 1)
    Check "F: database integrity ok" ((Probe --db-info) -match '\bok\b')
}
finally {
    $env:WATTSCOPE_DB = $null
    foreach ($p in (App)) { try { Stop-Process -Id $p.Id -Force } catch {} }
    Start-Sleep -Seconds 2
    foreach ($p in (Sidecar)) { try { Stop-Process -Id $p.Id -Force } catch {} }
    foreach ($n in "wattscope-testgame", "unmapped-render-tool", "launcher-updater-tool", "Battle.net") {
        # only the stand-ins this script copied (by exact path), never a real process of the same name
        foreach ($p in (Get-Process -Name $n -ErrorAction SilentlyContinue)) { if ($p.Path -like "$games*") { try { $p.Kill() } catch {} } }
    }
}

Write-Host "`n== Final gaming history (scratch database) =="
Probe --gaming | Write-Host

Write-Host "`n== Summary =="
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Result -eq "FAIL" }).Count
Write-Host ("{0} checks, {1} failed. Scratch data: {2}" -f $results.Count, $failed, $WorkDir)
exit $(if ($failed -eq 0) { 0 } else { 1 })

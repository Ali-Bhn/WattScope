<#
.SYNOPSIS
  Real, process-level validation of the packaged WattScope app's lifecycle
  (Milestone 004). Launches the actual release exe, so it records sessions in a SCRATCH database (WATTSCOPE_DB, see _scratch-db.ps1), never your real history
  -- run it deliberately, not in CI.

  Checks: single instance (sequential and racing launches), close-to-tray
  keeps monitoring running, reopen attaches to the same session, bounded
  sidecar crash/restart, host crash leaves no orphan sidecar and a cleanly
  stopped session, and (with -Exit) the in-window Exit path.

  Things it CANNOT check and leaves to a human: the tray icon/menu itself,
  real sleep/resume, logoff/shutdown. See docs/PROJECT_STATE.md.
#>
param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope.exe"),
    [string]$ProbeExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope-probe.exe")
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_scratch-db.ps1")
Assert-NoWattScopeRunning
$ScratchDb = Use-ScratchDatabase "lifecycle"   # every launched process (and the --sessions CLI below) inherits WATTSCOPE_DB
$AppExe = (Resolve-Path $AppExe).Path
$ProbeExe = (Resolve-Path $ProbeExe).Path
$results = New-Object System.Collections.Generic.List[object]

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    static bool IsMain(IntPtr h) { var sb = new System.Text.StringBuilder(64); GetWindowText(h, sb, 64); return sb.ToString() == "WattScope"; }
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static int CloseVisible(uint pid) {
        int n = 0;
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && IsMain(h)) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); n++; } return true; }, IntPtr.Zero);
        return n;
    }
    public static int VisibleTopLevelWindows(uint pid) {
        int n = 0;
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && IsMain(h)) n++; return true; }, IntPtr.Zero);
        return n;
    }
}
"@

function Check($name, [bool]$ok, $detail = "") {
    $results.Add([pscustomobject]@{ Check = $name; Result = $(if ($ok) { "PASS" } else { "FAIL" }); Detail = $detail })
    Write-Host ("[{0}] {1}  {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail)
}

function App { ,@(Get-Process -Name "wattscope" -ErrorAction SilentlyContinue) }
function Sidecar { ,@(Get-Process -Name "wattscope-probe" -ErrorAction SilentlyContinue) }
function Sessions {
    $ErrorActionPreference = "SilentlyContinue"   # the CLI logs recovery warnings to stderr; that is not a failure here
    $lines = & $ProbeExe --sessions 2>$null | Out-String -Stream
    $lines | ForEach-Object { if ($_ -match '^\s+#(\d+)\s+(\w+)\s+') { [pscustomobject]@{ Id = [int]$Matches[1]; Status = $Matches[2]; Line = $_ } } }
}
function ActiveSessions { ,@(Sessions | Where-Object { $_.Status -eq "Active" }) }
function StopAll {
    foreach ($p in App) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {} }
    Start-Sleep -Seconds 2
    foreach ($p in Sidecar) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {} }
    Start-Sleep -Seconds 1
}
function WaitFor([scriptblock]$cond, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 250 }
    return $false
}

Write-Host "== Preconditions =="
StopAll
Check "no WattScope processes running before the test" ((App).Count -eq 0 -and (Sidecar).Count -eq 0)

Write-Host "`n== 1. Launch =="
$app = Start-Process -FilePath $AppExe -PassThru
Check "one host + one sidecar after launch" (WaitFor { (App).Count -eq 1 -and (Sidecar).Count -eq 1 } 20) ("host={0} sidecar={1}" -f (App).Count, (Sidecar).Count)
Check "exactly one Active session" (WaitFor { (ActiveSessions).Count -eq 1 } 20)
$sessionId = (ActiveSessions)[0].Id
$sidecarPid = (Sidecar)[0].Id
Check "main window is visible" (WaitFor { [Win]::VisibleTopLevelWindows($app.Id) -ge 1 } 10)

Write-Host "`n== 2. Second launch (single instance) =="
$second = Start-Process -FilePath $AppExe -PassThru
Check "second launch exits by itself" ($second.WaitForExit(15000))
Start-Sleep -Seconds 2
Check "still exactly one host and one sidecar" ((App).Count -eq 1 -and (Sidecar).Count -eq 1) ("host={0} sidecar={1}" -f (App).Count, (Sidecar).Count)
Check "sidecar is the same process" ((Sidecar)[0].Id -eq $sidecarPid)
Check "still exactly one Active session, same id" ((ActiveSessions).Count -eq 1 -and (ActiveSessions)[0].Id -eq $sessionId)

Write-Host "`n== 3. Racing launches =="
$racers = 1..8 | ForEach-Object { Start-Process -FilePath $AppExe -PassThru }
$racers | ForEach-Object { [void]$_.WaitForExit(20000) }
Start-Sleep -Seconds 2
Check "8 simultaneous launches create no extra host/sidecar" ((App).Count -eq 1 -and (Sidecar).Count -eq 1) ("host={0} sidecar={1}" -f (App).Count, (Sidecar).Count)
Check "...and no extra session" ((ActiveSessions).Count -eq 1 -and (ActiveSessions)[0].Id -eq $sessionId)

Write-Host "`n== 4. Close window to tray =="
[void][Win]::CloseVisible($app.Id)   # WM_CLOSE to the visible window = the user clicking X
Check "window is hidden after close" (WaitFor { [Win]::VisibleTopLevelWindows($app.Id) -eq 0 } 10)
$app.Refresh()
Check "host is still running" (-not $app.HasExited)
Check "sidecar is still running (same process)" ((Sidecar).Count -eq 1 -and (Sidecar)[0].Id -eq $sidecarPid)
$before = (Sessions | Where-Object { $_.Id -eq $sessionId }).Line
Start-Sleep -Seconds 25
$after = (Sessions | Where-Object { $_.Id -eq $sessionId }).Line
Check "monitoring kept recording while hidden (session record advanced)" ($before -ne $after) ("before: $($before.Trim())`n        after:  $($after.Trim())")

Write-Host "`n== 5. Launch again while hidden -> shows the existing window =="
$third = Start-Process -FilePath $AppExe -PassThru
[void]$third.WaitForExit(15000)
Check "existing window is shown again" (WaitFor { [Win]::VisibleTopLevelWindows($app.Id) -ge 1 } 10)
Check "same single session continues" ((ActiveSessions).Count -eq 1 -and (ActiveSessions)[0].Id -eq $sessionId)

Write-Host "`n== 6. Sidecar crash -> bounded restart =="
$victim = (Sidecar)[0]
Stop-Process -Id $victim.Id -Force
Check "a new sidecar appears after a crash" (WaitFor { (Sidecar).Count -eq 1 -and (Sidecar)[0].Id -ne $victim.Id } 20)
Check "no duplicate sidecars" ((Sidecar).Count -eq 1)
Check "the crashed session was recovered and one session is Active" (WaitFor { (ActiveSessions).Count -eq 1 } 15) ("sessions: " + ((Sessions | ForEach-Object { "#$($_.Id)=$($_.Status)" }) -join " "))
$killed = 1
for ($i = 0; $i -lt 6; $i++) {
    $s = Sidecar
    if ($s.Count -eq 0) { break }
    Stop-Process -Id $s[0].Id -Force
    $killed++
    Start-Sleep -Seconds 6
}
Start-Sleep -Seconds 10
Check "restarts are bounded (the host stops restarting a crash loop)" ((Sidecar).Count -eq 0) ("crashes injected: $killed, sidecars now: $((Sidecar).Count)")
Check "host itself is still alive and responsive" (-not $app.HasExited)

Write-Host "`n== 7. Host dies abnormally -> no orphan sidecar =="
StopAll
$app = Start-Process -FilePath $AppExe -PassThru
Check "relaunch works after the give-up" (WaitFor { (App).Count -eq 1 -and (Sidecar).Count -eq 1 } 20)
[void](WaitFor { (ActiveSessions).Count -eq 1 } 20)
$sid = (ActiveSessions)[0].Id
Start-Sleep -Seconds 12
Stop-Process -Id $app.Id -Force       # simulates a host crash / forced termination
Check "the sidecar notices the host is gone and exits by itself" (WaitFor { (Sidecar).Count -eq 0 } 20)
$final = (Sessions | Where-Object { $_.Id -eq $sid })
Check "the session was closed cleanly (Stopped, not left Active/Interrupted)" ($final.Status -eq "Stopped") ("session #$sid = $($final.Status)")

Write-Host "`n== Summary =="
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Result -eq "FAIL" })
Write-Host ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
StopAll
if ($failed.Count -gt 0) { exit 1 }


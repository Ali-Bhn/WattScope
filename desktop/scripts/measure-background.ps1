<#
.SYNOPSIS
  Measures the packaged app's resource use with the dashboard visible and
  with it hidden in the tray: CPU, memory and process count per process
  group (host, WebView2 tree, sidecar). Launches the real release exe, so it
  records a real session in the default database.

  CPU is the delta of each process's TotalProcessorTime over wall-clock time,
  reported as % of ONE logical core (and of the whole machine). Short runs on
  a busy desktop are noisy: treat results as indicative, and run it more than
  once. It does NOT measure gaming FPS impact -- that needs a controlled
  benchmark with a game running.
#>
param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope.exe"),
    [int]$Seconds = 60,
    [int]$SettleSeconds = 15
)

$ErrorActionPreference = "Stop"
$AppExe = (Resolve-Path $AppExe).Path
$cores = [Environment]::ProcessorCount

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W2 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    static bool IsMain(IntPtr h) { var sb = new System.Text.StringBuilder(64); GetWindowText(h, sb, 64); return sb.ToString() == "WattScope"; }
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static int Visible(uint pid) { int n = 0; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && IsMain(h)) n++; return true; }, IntPtr.Zero); return n; }
    public static void Close(uint pid) { EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && IsMain(h)) PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero); }
}
"@

function Descendants($rootPid) {
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
    $found = New-Object System.Collections.Generic.HashSet[int]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($rootPid)
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        foreach ($p in $all | Where-Object { $_.ParentProcessId -eq $cur }) { if ($found.Add([int]$p.ProcessId)) { $queue.Enqueue([int]$p.ProcessId) } }
    }
    $found
}

function GroupStats($procs) {
    # Read everything now: Process objects cache lazily, so values must be captured at snapshot time.
    $cpu = 0.0; $ws = 0.0; $priv = 0.0; $n = 0
    foreach ($p in $procs) {
        try { $cpu += $p.TotalProcessorTime.TotalSeconds; $ws += $p.WorkingSet64; $priv += $p.PrivateMemorySize64; $n++ } catch { }
    }
    [pscustomobject]@{ Cpu = $cpu; Ws = [math]::Round($ws / 1MB, 1); Priv = [math]::Round($priv / 1MB, 1); N = $n }
}

function Snapshot($hostPid) {
    $ids = Descendants $hostPid
    $web = @(); $side = @()
    foreach ($id in $ids) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        if ($p.ProcessName -eq "wattscope-probe") { $side += $p } else { $web += $p }
    }
    $h = @(Get-Process -Id $hostPid -ErrorAction SilentlyContinue)
    [pscustomobject]@{ Time = Get-Date; Host = (GroupStats $h); Web = (GroupStats $web); Sidecar = (GroupStats $side) }
}

function Measure-Phase($name, $hostPid) {
    $a = Snapshot $hostPid
    Start-Sleep -Seconds $Seconds
    $b = Snapshot $hostPid
    $wall = ($b.Time - $a.Time).TotalSeconds
    $rows = foreach ($group in "Host", "Web", "Sidecar") {
        $pct = [math]::Round((($b.$group.Cpu - $a.$group.Cpu) / $wall) * 100, 2)
        [pscustomobject]@{
            Phase = $name; Group = $group; Procs = $b.$group.N
            "CPU % of one core" = $pct; "CPU % of machine" = [math]::Round($pct / $cores, 3)
            "WorkingSet MiB" = $b.$group.Ws; "Private MiB" = $b.$group.Priv
        }
    }
    $total = [pscustomobject]@{
        Phase = $name; Group = "TOTAL"; Procs = ($rows | Measure-Object -Property Procs -Sum).Sum
        "CPU % of one core" = [math]::Round(($rows | Measure-Object -Property "CPU % of one core" -Sum).Sum, 2)
        "CPU % of machine" = [math]::Round(($rows | Measure-Object -Property "CPU % of machine" -Sum).Sum, 3)
        "WorkingSet MiB" = [math]::Round(($rows | Measure-Object -Property "WorkingSet MiB" -Sum).Sum, 1)
        "Private MiB" = [math]::Round(($rows | Measure-Object -Property "Private MiB" -Sum).Sum, 1)
    }
    @($rows) + $total
}
foreach ($p in Get-Process -Name "wattscope", "wattscope-probe" -ErrorAction SilentlyContinue) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Seconds 2

$app = Start-Process -FilePath $AppExe -PassThru
Start-Sleep -Seconds $SettleSeconds
"Logical CPUs: $cores. Window visible: $([W2]::Visible($app.Id) -ge 1). Measuring $Seconds s per phase after a ${SettleSeconds}s settle."

$visible = Measure-Phase "dashboard visible" $app.Id
[W2]::Close($app.Id)
Start-Sleep -Seconds 5
"Window visible after close: $([W2]::Visible($app.Id) -ge 1) (expected False); host alive: $(-not $app.HasExited)"
$hidden = Measure-Phase "hidden in tray" $app.Id

($visible + $hidden) | Format-Table -AutoSize

Stop-Process -Id $app.Id -Force
Start-Sleep -Seconds 4
foreach ($p in Get-Process -Name "wattscope-probe" -ErrorAction SilentlyContinue) { Stop-Process -Id $p.Id -Force }

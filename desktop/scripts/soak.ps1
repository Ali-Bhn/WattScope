<#
.SYNOPSIS
  Long-session stability (soak) test of the packaged app: launches the real release exe against a SCRATCH
  database (WATTSCOPE_DB, never your real history), leaves it monitoring with the dashboard visible, and
  samples every process group at a fixed interval so that TRENDS can be read, not just a start and an end.

  Per sample (CSV): host / WebView2 tree / sidecar working set, private bytes, handles, threads and CPU time;
  process count; database and WAL size; and every -DbInfoEvery samples the raw-sample and session counts
  (through the read-only `--db-info` CLI). At the end it prints first/last/min/max and a least-squares slope
  per hour for each metric, computed after a warm-up period, and the result of an integrity check.

  It needs no game and does nothing disruptive. It refuses to start while a WattScope is running, and it ends
  early (reporting the real duration) if the app is exited by hand. A stable WebView2 baseline of a few hundred
  MiB is expected and is not a leak; look at the slopes.

.EXAMPLE
  .\soak.ps1 -Minutes 240
#>
param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope.exe"),
    [string]$ProbeExe = (Join-Path $PSScriptRoot "..\src-tauri\target\release\wattscope-probe.exe"),
    [int]$Minutes = 240,
    [int]$IntervalSeconds = 60,
    [int]$WarmupMinutes = 10,
    [int]$DbInfoEvery = 5,
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) ("wattscope-soak-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
    # Optional: expose the WebView2 DevTools port (and use a scratch WebView2 profile) so an external tool can read
    # JS-heap / DOM-node counts and the sidecar's own latency statistics during the run. 0 = a plain launch.
    [int]$WebViewDebugPort = 0
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_scratch-db.ps1")
Assert-NoWattScopeRunning
$ScratchDb = Use-ScratchDatabase "soak"
$AppExe = (Resolve-Path $AppExe).Path
$ProbeExe = (Resolve-Path $ProbeExe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$csv = Join-Path $OutDir "soak.csv"
if ($WebViewDebugPort -gt 0) {
    $env:WEBVIEW2_USER_DATA_FOLDER = Join-Path (Split-Path $ScratchDb) "webview-profile"
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$WebViewDebugPort"
}

# (Not "Group": that is the built-in alias of Group-Object, which would win over a function.)
function Measure-ProcessGroup($procs) {
    $ws = 0.0; $priv = 0.0; $handles = 0; $threads = 0; $cpu = 0.0; $n = 0
    foreach ($p in $procs) {
        try { $p.Refresh(); $ws += $p.WorkingSet64; $priv += $p.PrivateMemorySize64; $handles += $p.HandleCount; $threads += $p.Threads.Count; $cpu += $p.TotalProcessorTime.TotalSeconds; $n++ } catch { }
    }
    [pscustomobject]@{ N = $n; WsMiB = [math]::Round($ws / 1MB, 2); PrivMiB = [math]::Round($priv / 1MB, 2); Handles = $handles; Threads = $threads; CpuS = [math]::Round($cpu, 3) }
}

function Get-ProcessTree($rootPid) {
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name
    $found = New-Object System.Collections.Generic.List[object]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($rootPid)
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        foreach ($p in $all | Where-Object { $_.ParentProcessId -eq $cur }) { $found.Add($p); $queue.Enqueue([int]$p.ProcessId) }
    }
    $found
}

function FileBytes($path) { if (Test-Path $path) { (Get-Item $path).Length } else { 0 } }

function Sample($hostProc, $t0) {
    $tree = Get-ProcessTree $hostProc.Id
    $side = @(); $web = @()
    foreach ($t in $tree) {
        $p = Get-Process -Id $t.ProcessId -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        if ($t.Name -eq "wattscope-probe.exe") { $side += $p } else { $web += $p }
    }
    $h = Measure-ProcessGroup @($hostProc); $w = Measure-ProcessGroup $web; $s = Measure-ProcessGroup $side
    [pscustomobject]@{
        Time = (Get-Date).ToString("o"); ElapsedMin = [math]::Round(((Get-Date) - $t0).TotalMinutes, 2)
        HostWsMiB = $h.WsMiB; HostPrivMiB = $h.PrivMiB; HostHandles = $h.Handles; HostThreads = $h.Threads; HostCpuS = $h.CpuS
        WebProcs = $w.N; WebWsMiB = $w.WsMiB; WebPrivMiB = $w.PrivMiB; WebHandles = $w.Handles; WebThreads = $w.Threads; WebCpuS = $w.CpuS
        SidecarProcs = $s.N; SidecarWsMiB = $s.WsMiB; SidecarPrivMiB = $s.PrivMiB; SidecarHandles = $s.Handles; SidecarThreads = $s.Threads; SidecarCpuS = $s.CpuS
        TotalProcs = 1 + $w.N + $s.N; TotalPrivMiB = [math]::Round($h.PrivMiB + $w.PrivMiB + $s.PrivMiB, 2)
        DbKiB = [math]::Round((FileBytes $ScratchDb) / 1KB, 1); WalKiB = [math]::Round((FileBytes ($ScratchDb + "-wal")) / 1KB, 1)
        RawSamples = $null; Sessions = $null
    }
}

function DbInfo {
    $ErrorActionPreference = "SilentlyContinue"
    $text = (& $ProbeExe --db-info 2>$null | Out-String)
    $samples = if ($text -match 'Raw samples:\s+([\d.,]+)') { [int64]($Matches[1] -replace '[.,]', '') } else { $null }
    $sessions = if ($text -match 'Sessions:\s+(\d+)') { [int]$Matches[1] } else { $null }
    $integrity = if ($text -match 'Integrity check:\s+(\S+)') { $Matches[1] } else { "?" }
    [pscustomobject]@{ Samples = $samples; Sessions = $sessions; Integrity = $integrity }
}

# Least-squares slope of y against x.
function Slope($xs, $ys) {
    $n = $xs.Count; if ($n -lt 3) { return $null }
    $mx = ($xs | Measure-Object -Average).Average; $my = ($ys | Measure-Object -Average).Average
    $sxy = 0.0; $sxx = 0.0
    for ($i = 0; $i -lt $n; $i++) { $sxy += ($xs[$i] - $mx) * ($ys[$i] - $my); $sxx += ($xs[$i] - $mx) * ($xs[$i] - $mx) }
    if ($sxx -eq 0) { return $null }
    $sxy / $sxx
}

Write-Host "Soak: $Minutes min, one sample every $IntervalSeconds s, warm-up $WarmupMinutes min. Output: $OutDir"
$app = Start-Process -FilePath $AppExe -PassThru
$t0 = Get-Date
Start-Sleep -Seconds 10
$rows = New-Object System.Collections.Generic.List[object]
$deadline = $t0.AddMinutes($Minutes)
$i = 0
$endedEarly = $false
while ((Get-Date) -lt $deadline) {
    $app.Refresh()
    if ($app.HasExited) { $endedEarly = $true; Write-Host "The app exited after $([math]::Round(((Get-Date) - $t0).TotalMinutes, 1)) min; stopping the soak." -ForegroundColor Yellow; break }
    $row = Sample $app $t0
    if ($i % $DbInfoEvery -eq 0) { $d = DbInfo; $row.RawSamples = $d.Samples; $row.Sessions = $d.Sessions }
    $rows.Add($row)
    $row | Export-Csv -Path $csv -Append -NoTypeInformation -Encoding UTF8
    if ($i % 10 -eq 0) { Write-Host ("{0,6:N1} min  procs {1}  private: host {2} web {3} sidecar {4} MiB  handles: {5}/{6}/{7}  db {8}+{9} KiB  samples {10}" -f $row.ElapsedMin, $row.TotalProcs, $row.HostPrivMiB, $row.WebPrivMiB, $row.SidecarPrivMiB, $row.HostHandles, $row.WebHandles, $row.SidecarHandles, $row.DbKiB, $row.WalKiB, $row.RawSamples) }
    $i++
    $next = $t0.AddSeconds(10 + $i * $IntervalSeconds)
    $wait = ($next - (Get-Date)).TotalSeconds
    if ($wait -gt 0) { Start-Sleep -Seconds ([int][math]::Ceiling($wait)) }
}

$actualMinutes = [math]::Round(((Get-Date) - $t0).TotalMinutes, 1)
$steady = @($rows | Where-Object { $_.ElapsedMin -ge $WarmupMinutes })
Write-Host "`n== Result: $actualMinutes min, $($rows.Count) samples ($($steady.Count) after the $WarmupMinutes min warm-up)$(if ($endedEarly) { ' -- ENDED EARLY' }) =="
$metrics = "HostPrivMiB", "HostWsMiB", "HostHandles", "HostThreads", "WebProcs", "WebPrivMiB", "WebWsMiB", "WebHandles", "WebThreads", "SidecarPrivMiB", "SidecarWsMiB", "SidecarHandles", "SidecarThreads", "TotalProcs", "TotalPrivMiB", "DbKiB", "WalKiB"
$summary = foreach ($m in $metrics) {
    $ys = @($steady | ForEach-Object { [double]$_.$m }); if ($ys.Count -eq 0) { continue }
    $xs = @($steady | ForEach-Object { [double]$_.ElapsedMin / 60.0 })
    $slope = Slope $xs $ys
    [pscustomobject]@{ Metric = $m; First = $ys[0]; Last = $ys[-1]; Min = ($ys | Measure-Object -Minimum).Minimum; Max = ($ys | Measure-Object -Maximum).Maximum; "Slope per hour" = $(if ($null -ne $slope) { [math]::Round($slope, 3) } else { "n/a" }) }
}
$summary | Format-Table -AutoSize | Out-String | Write-Host
if ($steady.Count -ge 2) {
    $wall = ([datetime]$steady[-1].Time - [datetime]$steady[0].Time).TotalSeconds
    foreach ($g in "Host", "Web", "Sidecar") { Write-Host ("CPU {0,-8} {1,6:N2} % of one core (average after warm-up)" -f $g, ((([double]$steady[-1]."${g}CpuS") - ([double]$steady[0]."${g}CpuS")) / $wall * 100)) }
}
$withSamples = @($rows | Where-Object { $null -ne $_.RawSamples })
if ($withSamples.Count -ge 2) {
    $a = $withSamples[0]; $b = $withSamples[-1]
    $secs = ([datetime]$b.Time - [datetime]$a.Time).TotalSeconds
    Write-Host ("Raw samples {0} -> {1}: {2:N3} per second over {3:N0} s; sessions {4} -> {5}" -f $a.RawSamples, $b.RawSamples, (($b.RawSamples - $a.RawSamples) / $secs), $secs, $a.Sessions, $b.Sessions)
}
$summary | Export-Csv -Path (Join-Path $OutDir "soak-summary.csv") -NoTypeInformation -Encoding UTF8

# Stop the app the way a host crash would (the sidecar then closes its session by itself), and check the database.
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
$gone = $false
for ($k = 0; $k -lt 40; $k++) { if (@(Get-WattScopeSidecar).Count -eq 0) { $gone = $true; break }; Start-Sleep -Milliseconds 500 }
Write-Host "Sidecar exited by itself after the host ended: $gone"
$final = DbInfo
Write-Host "Final database: $($final.Samples) raw samples, $($final.Sessions) session(s), integrity $($final.Integrity)"
Write-Host "CSV: $csv"
if (-not $gone -or $final.Integrity -ne "ok") { exit 1 }

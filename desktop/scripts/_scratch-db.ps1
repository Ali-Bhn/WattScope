<#
.SYNOPSIS
  Shared safety guard for every validation script that launches the packaged app (dot-source it).

  Hard rule (Milestone 006): validation never touches the real history. Each script must point WATTSCOPE_DB at a
  scratch database before launching anything, confirm that path out loud, and refuse to run if a WattScope is
  already running (a second launch would just forward to that instance -- the user's real one -- and the scripts
  stop processes by name).
#>

function Use-ScratchDatabase([string]$Name) {
    $real = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "WattScope\wattscope.db"))
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("wattscope-validation-" + $Name + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force $dir | Out-Null
    $db = [IO.Path]::GetFullPath((Join-Path $dir "scratch.db"))
    if ($db -ieq $real -or $db.StartsWith((Split-Path $real), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to run: the scratch database resolves inside the real WattScope data folder ($db)."
    }
    $env:WATTSCOPE_DB = $db
    Write-Host "Validation database (scratch, NOT your real history): $db" -ForegroundColor Cyan
    Write-Host "Real database that will NOT be touched:               $real" -ForegroundColor Cyan
    return $db
}

# The sidecar is `wattscope-probe.exe --serve`. The validation scripts also run the same executable as a
# short-lived CLI (`--sessions`, `--gaming`), and such a process can still be visible for a moment after its
# output was read. Counting processes by name therefore mistakes a CLI call for a (second, or a restarted)
# sidecar; the sidecar is identified by its command line instead.
function Get-WattScopeSidecar {
    @(Get-CimInstance Win32_Process -Filter "Name='wattscope-probe.exe'" |
        Where-Object { $_.CommandLine -match '(^|\s)--serve(\s|$)' } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
}

function Assert-NoWattScopeRunning {
    $running = @(Get-Process -Name "wattscope", "wattscope-probe" -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw "Refusing to run: WattScope is already running ($($running.Name -join ', ')). Exit it first -- this script stops WattScope processes by name and must not touch a real, running instance."
    }
}

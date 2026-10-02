# Architecture

## Scope

- **Milestone 001:** hardware discovery, sensor classification, sampling,
  JSON export.
- **Milestone 002:** energy integration, wall-power estimation, electricity
  cost, SQLite persistence, session lifecycle. Calculation rules:
  `docs/ENERGY_MODEL.md`; storage: `docs/STORAGE.md`.
- **Milestone 003:** sub-bucket electricity price allocation (Part A --
  `docs/ENERGY_MODEL.md` section 4) and the first desktop application
  (`desktop/`, Tauri 2 + React + TypeScript + Tailwind) talking to the
  unmodified .NET engine through a new `--serve` JSON sidecar protocol. See
  "Data flow (--serve)" and "Desktop application" below.

- **Milestone 004:** runtime lifecycle of the desktop app -- system tray,
  close-to-tray, single instance, an explicit start/stop of monitoring that is
  independent of the window, a hardened and bounded sidecar lifecycle,
  database-enforced single active session, suspend/resume handling, honest
  partial-monitoring UX. See "Process model (Milestone 004)" below.
  **Automatic Windows startup and a least-privilege privileged sensor
  helper/service are intentionally deferred** (see "Privilege model").

- **Milestone 005:** gaming session detection and energy analytics, built
  on top of the existing monitoring pipeline: passive process-based game
  detection with an explicit state machine, `GamingSession` (distinct from the
  monitoring session) with energy derived from the monitor's own verified
  intervals, per-game aggregates, user mappings, manual sessions, a Gaming
  page. Entirely external and passive -- no injection, hooks or memory access.
  See `docs/GAMING.md` and "Gaming (Milestone 005)" below.

- **Milestone 006:** application completion -- no new engine capability and no
  schema change; the desktop app became coherent and honest. See "Application
  layer (Milestone 006)" below.

Nothing here knows about FPS, frame times or anti-cheat integration; those are
not part of this project.

## Projects

```
WattScope.Core        Provider-agnostic domain model + orchestration logic:
                       classification, sampling, energy integration,
                       estimation, pricing, reporting, session lifecycle
                       (EnergyMonitor behind IEnergyStore). No dependency on
                       any monitoring backend, database, or UI.
                       Depends only on Microsoft.Extensions.Logging.Abstractions.

WattScope.Hardware     Concrete ISensorProvider implementations
                       (LibreHardwareMonitor, NVML) plus WMI-based hardware
                       inventory and the DiscoveryService that ties them
                       together. Windows-only (net10.0-windows).

WattScope.Storage      SQLite implementation of IEnergyStore
                       (Microsoft.Data.Sqlite): schema migrations, sessions,
                       compact samples, energy buckets, gaps, availability,
                       prices, settings, retention, crash recovery, monitor
                       lock. Cross-platform (net10.0).

WattScope.Probe        Standalone CLI. References Core + Hardware + Storage.
                       The executable the desktop app's sidecar runs, via
                       its --serve command (below) -- not a separate service.

WattScope.Core.Tests       xUnit, fully mocked / synthetic data, no hardware
                            or database -- runs anywhere.

WattScope.Storage.Tests    xUnit against real SQLite files in a temp
                            directory, synthetic data -- no hardware.

WattScope.Hardware.Tests   xUnit, integration-style against the real WMI /
                            LibreHardwareMonitor / NVML backends on the host
                            machine. Requires Windows.
```

Dependency direction is strictly one-way: `Probe -> {Hardware, Storage} ->
Core`. `Hardware` and `Storage` do not reference each other. Nothing in
`Core` references `Hardware` or `Storage`.

## Why the split

Future milestones (Tauri/React UI, C#/.NET monitoring engine as a long-lived
service, SQLite persistence) need to consume sensor data without depending on
LibreHardwareMonitorLib or NVML's P/Invoke surface directly. `WattScope.Core`
defines that boundary now:

- `ISensorProvider` is the only contract a future host needs to depend on to
  get sensor data. `LibreHardwareMonitorProvider` and `NvmlSensorProvider`
  are interchangeable implementations of it.
- `SamplingEngine` drives periodic sampling against `ISensorProvider` alone,
  so it is testable with fakes and reusable by whatever future process hosts
  continuous monitoring (this milestone's CLI, or a later background
  service).
- `PowerSensorClassifier` encodes the "don't double-count" policy once, in
  one place, independent of which backend produced the readings.

## Data flow (`--discover`)

```
WmiHardwareInventoryProvider.Discover()  ---> HardwareDevice[] (sensor-independent)
                                                        |
ISensorProvider.Initialize() (per provider)             |
        |                                                |
        v                                                v
ISensorProvider.GetHardware() + GetSensorReadings()  --> combined into
                                                          DiscoveryService.Discover()
                                                                |
                                                                v
                                                   PowerSensorClassifier.Classify()
                                                                |
                                                                v
                                                       DiscoverySnapshot
                                                       (Core.Discovery)
                                                          /         \
                                                         v           v
                                              ConsoleReporter   ExportReportBuilder
                                              (human-readable)  -> JsonExportService
```

## Data flow (`--monitor`)

`DiscoveryService.Discover()` runs once up front (to build the classification
and know which providers are actually available). The **same** already-
initialized `ISensorProvider` instances are then handed to `SamplingEngine`,
which calls only `GetSensorReadings()` on each tick -- hardware is never
re-enumerated mid-session. Each tick is printed live; if `--export` was
given, ticks are buffered in memory and written as one `ExportReport` at the
end of the run (or on Ctrl+C).

`--monitor` is unchanged by Milestone 002 and remains the diagnostic path:
it never touches the database.

## Data flow (`--record`, Milestone 002)

```
DiscoveryService.Discover()  (once)
        |
        v
PowerSampleBuilder  <-- binds CpuPackage / GpuBoard channels to the
        |               classifier's *selected* sensors, once per session
        |
SamplingEngine.RunAsync()  -- per tick: readings + UTC + monotonic time
        |
        v
PowerSampleBuilder.Build(tick) -> PowerSample (2 validated channels only)
        |
        v
EnergyMonitor.OnSample()                         OS suspend/resume
  EnergyIntegrator  -> verified PowerInterval | MonitoringGap   <-- SystemEvents
  EnergyAccumulator -> 15-min bucket deltas (measured + estimated)
  batch: samples, bucket deltas, gaps, availability changes
        |  every 10 s, one transaction
        v
IEnergyStore (SqliteEnergyStore)
        |
        v
EnergyReportBuilder + CostCalculator (read side: --summary, --sessions)
```

Hardware is still enumerated exactly once; the full sensor list is read
each tick (that is how the providers refresh) but only the bound sensors'
values leave `PowerSampleBuilder`.

## Data flow (`--serve`, Milestone 003)

The desktop app's native sidecar: the *same* pipeline as `--record` above
(reused, not duplicated -- `ServeCommand` and `RecordCommand` both drive one
`EnergyMonitor`/`SamplingEngine`), but speaking newline-delimited JSON on
stdin/stdout instead of printing text, so a non-.NET host (Tauri's Rust
process) can consume it without re-implementing any of the energy/cost
logic:

```
(same pipeline as --record, down to EnergyMonitor.OnSample)
        |
        v
stdout: one JSON "tick" line per sample (only the fields a dashboard
        needs -- never the full sensor inventory; see Part E)
stdin:  one JSON request per line (get_sessions, get_summary, set_price,
        get/set_estimation, get/set_retention, get_db_info, shutdown)
        -> dispatched to the *same* DatabaseCommands.Build*Report /
           SqliteEnergyStore methods --summary/--sessions/--set-price use,
           serialized directly (no parallel DTO layer to drift out of sync)
stdout: one JSON "response" line per request, correlated by id
```

Two `SqliteEnergyStore` connections are opened: one owned exclusively by the
recording `EnergyMonitor` (writes), one for the request/response loop's
reads and settings writes -- the same already-supported pattern of a
separate `--summary`/`--set-price` process reading/writing while `--record`
holds the `MonitorLock` (`docs/STORAGE.md`), just both ends alive in one
process. All logging goes to stderr, never stdout, so a log line can never
corrupt the JSON stream (`Program.cs`, `ConsoleLoggerOptions.LogToStandardErrorThreshold`).

**A real concurrency bug found and fixed while building this**: the
request-reading loop must run via `Task.Run`, not as a plain un-awaited
async call -- `Console.In.ReadLineAsync` was observed to not reliably yield
control back before it has a line to return, which silently starved the
sampling loop of any chance to run at all (zero ticks were ever emitted
until the first stdin write arrived). Caught by actually spawning the
process and watching its output, not by code review; see `ServeCommand.cs`.

## Process model (Milestone 004)

WattScope runs only when the user launches it. While it runs, six lifecycles
are kept deliberately separate, because conflating them is what makes a
tray app stop monitoring when a window closes:

| Lifecycle | Owner | Ends when |
|---|---|---|
| Window (React UI) | the webview | the user closes it -- which only **hides** it; it can be shown again at any time |
| Host (Tauri process) | the OS process the user launched | the user chooses **Exit WattScope** (tray or Settings), or the OS tears it down |
| Sidecar process (`wattscope-probe --serve`) | the host (by process *handle*) | host Exit, host death (stdin closes), or a crash (bounded restart) |
| Monitoring (sampling + integration + persistence) | `MonitoringController` inside the sidecar | Stop monitoring, Exit, an interval change (restarts in the same session), or a sampling failure |
| Sensor providers (LibreHardwareMonitor, NVML) | the sidecar process | the sidecar process ends; initialised once, reused across start/stop |
| Session (database row) | the database | Stop/Exit marks it `Stopped`; a crash leaves it `Active` until recovered as `Interrupted` |

```
user launches WattScope
        |
  [single-instance guard] -- already running? --> show that window, exit
        v
  host: tray + supervisor
        |  spawn exactly one sidecar  (--serve)
        v
  sidecar: recording lock, 2 DB connections, providers initialised
        |  start monitoring (new session)            <- tray Start / Stop,
        v                                               window button, or request
  MonitoringController: one run at a time, one Active session
        |
        +--> tick JSON ---> host: tray model (always) ---> webview (only while visible)
```

**One host, one sidecar, one session.** Three independent guards, so no
single layer has to be perfect:

1. *Host single instance* -- `tauri-plugin-single-instance` (a second launch
   forwards "show" to the first and exits; it never reaches sidecar code).
2. *One recorder per database* -- the existing `MonitorLock` file lock; a
   sidecar that cannot take it exits with code 3 and the host does **not**
   retry or kill the holder.
3. *One Active session per database* -- migration v3 adds the partial unique
   index `ux_session_single_active`. `CreateSession`/`ReopenSession` throw
   `ActiveSessionExistsException` for a second one, even from another process.

**Close is not Exit.** Window X hides the window (`prevent_close` + `hide`)
and keeps the host, sidecar and monitoring running. Only Exit stops
anything: it asks the sidecar to shut down (it flushes, closes the session
as `Stopped` and exits), waits up to 5 s, and only then kills the child the
host holds the handle of. The host never kills a process by executable name
or by a bare PID.

**Sidecar supervision.** The host owns the child by `CommandChild` handle
and a spawn generation number (a late exit event from an older spawn is
ignored). An exit the host did not ask for is a crash: `RestartPolicy`
restarts it with 1 s / 2 s / 4 s back-off, at most 3 times in 60 s, then
gives up and reports it (tray: *Monitor not running*; dashboard: an error
and *Start monitoring*). Exit code 3 (another recorder owns the database) is
never retried. A restart passes `--resume` (the crash becomes an honest
`Interrupted` gap in the same session) only if the user wanted monitoring,
and `--paused` if they had stopped it. If the host itself dies, the
sidecar's stdin closes; the sidecar treats that as shutdown, stops the
session cleanly and exits -- no orphan.

**The window is only a view.** The sidecar emits one JSON tick per sample
regardless. The host feeds the tray from every tick, but forwards ticks to
the webview only while the window is visible, so a hidden dashboard does no
JSON parsing, React rendering or canvas work. When the window is shown again
the UI asks `get_monitoring_state` (which includes the last tick) and
attaches to the same running session.

**Suspend/resume.** The sidecar subscribes to `SystemEvents.PowerModeChanged`
once, at process level, and forwards to the running monitor. Suspend breaks
the sample chain and flushes; samples that arrive while suspended are
ignored; resume cuts one `Suspend` gap from the last pre-sleep sample to the
first post-resume sample. Duplicate Suspend notifications and a Resume that
does not follow a Suspend are no-ops, so one sleep is one gap. If no
notification arrives at all, the integrator's own elapsed-time checks
(monotonic stall / wall-clock-vs-monotonic disagreement) still record the gap
and integrate nothing across it; the sampling loop re-anchors after a long
stall instead of firing catch-up ticks. No energy is ever integrated across a
gap.

**Shutdown.** Explicit Exit is the clean path described above. A last-resort
`RunEvent::Exit` handler stops the sidecar with a 2 s bound if the process is
torn down by something else. If even that does not complete (for example the
OS ends the session abruptly), the sidecar notices stdin closing, and
Milestone 002 crash recovery is the final safety net: an `Active` session
with no live recorder is closed as `Interrupted` at its last persisted
observation. Whether Windows logoff/shutdown/restart reliably reaches these
handlers was **not** verified (it needs a real logoff); see PROJECT_STATE.md.
## Gaming (Milestone 005)

Full design: `docs/GAMING.md`. In short:

```
WindowsProcessSource (toolhelp snapshot, cached; WattScope.Hardware)
        | every 5 s, only while monitoring  (GamingService poll loop, WattScope.Probe)
        v
GameDetector (state machine) <-- GameIdentifier (mappings, catalog, launcher/library heuristics)
        | events: candidate / confirmed / grace / ended
        v
GamingSessionTracker  <-- IMonitorObserver: every accepted sample + the integrator's
        |                  verified interval or gap (EnergyMonitor, unchanged otherwise)
        |  GamingLedger: per-price EnergyTotals + peaks (no raw samples)
        v
IGamingStore (SqliteEnergyStore, a third connection) -- gaming_session, gaming_session_energy, game_mapping
        |
        v
tick.gaming (small live summary) + get_gaming_* requests --> the Gaming page (display only)
```

Ownership rules: one Active monitoring session and one Active gaming session
per database (unique partial indexes); the tracker owns the single gaming
context; `EnergyMonitor` is unchanged apart from an optional observer hook
whose failures are logged and contained. Core stays UI-agnostic and
process-model-agnostic: `IProcessSource` and `IExecutableMetadata` are the only
seams to the OS, and tests drive the whole thing with synthetic processes and
samples. The Rust host did not change: it forwards ticks unchanged, so the
`gaming` field reaches the webview without host code.

Lifecycle: `MonitoringController` attaches the gaming service to each run
(`GamingService.Attach`) and detaches it before the monitoring session stops;
an interval change keeps an open gaming session (the same monitoring session
continues with a recorded `Paused` gap). After a sidecar crash the orphaned
gaming session is recovered as `Interrupted` together with its monitoring
session, and a game that is still running is detected afresh.

## Application layer (Milestone 006)

```
host (Rust)  --events-->  window (React)
  sidecar-tick / sidecar-status / backend-state / ui-visibility / navigate
  commands: sidecar_request, get_backend_state, retry_sidecar, get_host_info, ...
window: ONE store (lib/store.ts) = the only listener of those events
  state slices (view, status, snapshot, backend, uiVisible) via useSyncExternalStore
  bounded chart history (300 points), tick callbacks for the canvas (no React state)
  pages read slices: Dashboard (tick), Gaming live card (tick), Settings (snapshot only)
  usePolling / useQuery: requests with loading/ready/error, polling only while visible
```

- **Sidecar process state is separate from monitoring state.** The host
  tracks `starting -> running`, `restarting` (bounded automatic restart
  pending) and `failed` (gave up: lock held, database error, too many restarts,
  spawn failure) and emits `backend-state`; the UI localizes the *reason code*
  and offers **Retry** (`retry_sidecar`), which only acts in `failed` with no
  sidecar alive and goes through the same guarded spawn, so it cannot create a
  second sidecar or session. The pending automatic restart also re-checks
  `alive` before spawning.
- **Exit codes** of `--serve`: 3 = another recorder owns the database, 4 = the
  database cannot be opened/migrated; neither is retried automatically.
- **Money and estimates stay in the backend.** The UI sends prices as exact
  decimal text and only *formats* numbers it receives. The server enforces what
  the UI previews: a price that reaches into already-recorded energy is refused
  unless `confirmHistorical` is sent, and history deletion needs the exact
  session count (`confirmSessions`) the user was shown and an age >= 30 days.
- **Honesty rules live in pure, tested modules** (`lib/reports.ts`,
  `lib/monitoring.ts`, `lib/gaming.ts`): Complete and Partial are never merged;
  a missing value is `--`, not zero; cost needs Complete + priced.
- **Persistence of UI state**: first-run completion is in the database
  (`app_setting`), not browser storage; the language is the only value kept in
  `localStorage` (and mirrored to the host for the tray).
- **Dev-only mock** (`src/dev/mock.ts`, `--mode mock`) replaces the host and
  sidecar with synthetic data so the UI can be rendered/screenshotted in a
  browser; it is compiled out of normal builds.

## Provider notes

- **LibreHardwareMonitorProvider** wraps a single `LibreHardwareMonitor.Hardware.Computer`,
  created once in `Initialize()` and reused. CPU, GPU, Memory, and
  Motherboard hardware groups are enabled; Storage/Network/Controller/PSU/
  Battery are left disabled to keep the always-on sampling path lightweight.
  Discovery and `--monitor` refresh every node and return every sensor. For
  continuous monitoring (`--serve`, `--record`) the bound sensor IDs are handed
  over once (`PowerSampleBuilder.LimitSamplingToBindings` ->
  `IScopedSensorProvider.LimitSamplingTo`); a tick then refreshes only the
  nodes that own a bound sensor, plus their parents, and returns only those
  sensors. A bound sensor that is not there is absent (the channel reads
  Missing), never substituted.
- **NvmlSensorProvider** P/Invokes `nvml.dll` directly (found under
  `C:\Windows\System32` when an NVIDIA driver is installed) via
  `DllImport("nvml.dll")`. It is never bundled by this application. If the
  DLL is missing, `Initialize()` catches `DllNotFoundException` and reports
  `IsAvailable = false` -- the rest of the application continues normally.
- **WmiHardwareInventoryProvider** uses `System.Management` and is entirely
  independent of both providers above -- it exists so hardware can be listed
  even when zero sensor providers are available.

See `docs/SENSOR_CONTRACT.md` for the full field-level contract and the
power-sensor classification/precedence policy.

## Privilege model

WattScope never requires administrator rights; every component runs and
degrades gracefully unelevated. Elevation only changes *which sensors are
readable*. On the development machine (i9-14900KF, MSI PRO Z790-P WIFI) an
elevated `--discover` produced real CPU package/core power (45.40 W /
44.14 W) where the unelevated process read a flat 0.0 W, and discovered 264
sensors instead of 159. GPU board power (NVML) is readable either way. This
is recorded as a per-machine observation, not a universal rule -- see
`docs/SENSOR_CONTRACT.md` section 4.

Current state: the Probe CLI runs at whatever privilege level it was
launched with. Since Milestone 002, a 0 W CPU package reading (the
non-elevated symptom on this machine) is rejected as `ImplausibleZero`
rather than integrated, so an unelevated run yields a *partial* wall
estimate and no cost -- never a silently low one.

**Product decision (Milestone 004, intentional -- not an implementation
failure):** automatic Windows startup and a least-privilege privileged
sensor helper/service are deferred. WattScope currently runs only when the
user explicitly launches it. Complete CPU monitoring on hardware requiring
privileged sensor access may require the user to explicitly launch WattScope
with Administrator privileges.

What that means in practice, as actually implemented:

- The whole desktop application (host, window and sidecar) runs at whatever
  privilege level the user launched it with. Nothing in WattScope elevates
  itself, ever. There is no Windows Service, no privileged helper, no
  privileged IPC, no UAC-bypass technique and no startup registration.
- Launched normally (unelevated): GPU board power works; CPU package reads
  `ImplausibleZero` (rejected, never integrated as 0 W). The dashboard and
  tray show **Partial monitoring**, say why, show no cost for a partial
  estimate, and keep monitoring the GPU.
- Launched with "Run as administrator" by the user: CPU package power is
  readable on the development machine (docs/SENSOR_CONTRACT.md section 4)
  and the estimate is Complete. Running the entire app elevated is an
  accepted **interim** limitation of this development-stage version, only
  when the user chooses it -- it is not the long-term production privilege
  architecture.
- A "Restart as Administrator" button was **not** implemented: doing it
  safely (hand ownership of the single-instance lock, the recording lock and
  the active session from the old process to an elevated new one across a UAC
  prompt) needs more architecture than this milestone warrants. The
  Milestone 003 "Restart monitor as administrator" action, which elevated
  only the sidecar and left an untracked second recorder outside the host's
  control, conflicted with the one-sidecar/one-session rules and was
  **removed**.

`ISensorProvider` remains the seam for a future privileged broker -- an
IPC-backed provider can replace the in-process LibreHardwareMonitor provider
without changes to `WattScope.Core`.
## Desktop application (`desktop/`, Milestone 003)

```
desktop/src/            React 18 + TypeScript + Tailwind. No charting
                        library for the live chart (Part E) -- a small
                        canvas component subscribes to tick events itself
                        and draws imperatively on a throttled
                        requestAnimationFrame loop with a bounded ring
                        buffer, so a new sample never triggers a React
                        re-render of anything else.
desktop/src-tauri/      Tauri 2 (Rust). src/lib.rs: single-instance guard,
                        tray, close-to-tray, and the supervisor that owns
                        the one --serve sidecar by handle (spawn, crash
                        detection, bounded restart, stop on Exit);
                        src/lifecycle.rs: the pure, unit-tested restart
                        policy and spawn arguments; src/labels.rs: tray
                        strings in EN/DE/FA.
desktop/scripts/        publish-probe.ps1: dotnet publish of
                        WattScope.Probe as a self-contained, single-file
                        win-x64 binary, placed where Tauri's externalBin
                        expects a sidecar. Must run before `tauri dev`/
                        `tauri build` -- Tauri does not build the .NET side.
```

Localization (English, German, Persian) uses `react-i18next`; Persian sets
`dir="rtl"` on `<html>` and the layout uses CSS logical properties
(`ps-`/`pe-`/`border-e`, etc.) throughout so the whole layout mirrors, not
just text -- numbers, units and timestamps are deliberately kept in a fixed
Latin-digit, Gregorian format regardless of UI language (a Jalali calendar
or Eastern Arabic digits on a log timestamp would be a confusing
"localization," not a faithful one), isolated with `dir="ltr"` spans so they
read correctly embedded in right-to-left text.

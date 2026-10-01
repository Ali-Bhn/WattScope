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

There is still no gaming analytics -- that remains a future milestone, built
on top of `WattScope.Core`'s abstractions without needing to know
LibreHardwareMonitor, NVML, or SQLite exist.

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

## Provider notes

- **LibreHardwareMonitorProvider** wraps a single `LibreHardwareMonitor.Hardware.Computer`,
  created once in `Initialize()` and reused. CPU, GPU, Memory, and
  Motherboard hardware groups are enabled; Storage/Network/Controller/PSU/
  Battery are left disabled to keep the always-on sampling path lightweight.
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

**Milestone 003 implements the unelevated-by-default half of this, not the
full privileged-broker service.** A minimal, separately-installed elevated
Windows service exposing sensor reads over an authenticated local IPC
channel (the design `docs/SENSOR_CONTRACT.md` section "Future requirement"
describes) was **not** built in this milestone -- it is real, scoped work
(packaging, service install/uninstall, an ACL'd named pipe, a client
protocol) that does not fit here. Instead, per the brief's explicit
allowance for "an honest partial-monitoring state" in lieu of that:

- The desktop app's sidecar (`--serve`) runs **unelevated by default**. GPU
  board power works; CPU package reads `ImplausibleZero` and the dashboard
  shows it as an explicit unavailable state (`dashboard.notElevated`),
  never a substituted zero.
- Settings has one explicit, user-initiated action ("Restart monitor as
  administrator") that relaunches *only* the small Probe sidecar elevated,
  via a real Windows UAC prompt (`Start-Process -Verb RunAs`) -- never the
  whole UI window. This is a real elevation request a user must approve,
  not a bypass.
- **Known limitation of this approach** (documented, not hidden): Windows
  gives no process handle back across a UAC elevation boundary, so the
  running desktop window cannot keep talking to the elevated process it
  just spawned -- live monitoring in that window pauses until the app is
  restarted, at which point it reconnects to the (now complete, costed)
  session the elevated process recorded into the same database. See
  `docs/PROJECT_STATE.md` for the full statement of this gap and what a
  real privileged-broker service would need to replace it.

`ISensorProvider` remains the seam for that future broker -- an IPC-backed
provider can still replace the in-process LibreHardwareMonitor provider
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
desktop/src-tauri/      Tauri 2 (Rust). Owns the one --serve sidecar
                        process for the app's lifetime (src/lib.rs):
                        spawns it, forwards "tick"/"status" lines as Tauri
                        events, matches "response" lines back to whichever
                        frontend request is awaiting that id, and asks it
                        to stop cleanly on window close.
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

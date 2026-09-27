# Architecture

## Scope

- **Milestone 001:** hardware discovery, sensor classification, sampling,
  JSON export.
- **Milestone 002:** energy integration, wall-power estimation, electricity
  cost, SQLite persistence, session lifecycle. Calculation rules:
  `docs/ENERGY_MODEL.md`; storage: `docs/STORAGE.md`.

There is still no desktop UI and no gaming analytics -- those are future
milestones, built on top of `WattScope.Core`'s abstractions without needing
to know LibreHardwareMonitor, NVML, or SQLite exist.

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
                       Still the only executable -- no desktop UI yet.

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
estimate and no cost -- never a silently low one. Planned (future milestone, not implemented): the desktop UI
and the energy/cost engine stay unelevated; privileged sensor reads move
into a minimal, separately-installed elevated component (e.g. a Windows
service) that exposes only selected readings over local, ACL-restricted IPC.
`ISensorProvider` is the seam for this -- an IPC-backed provider can replace
the in-process LibreHardwareMonitor provider without changes to Core.

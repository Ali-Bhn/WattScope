# Architecture

## Milestone 001 scope

This milestone builds the hardware/sensor foundation only. There is no UI,
database, cost calculator, or gaming analytics yet -- those are future
milestones, built on top of `WattScope.Core`'s abstractions without needing
to know LibreHardwareMonitor or NVML exist.

## Projects

```
WattScope.Core        Provider-agnostic domain model + orchestration logic.
                       No dependency on any concrete monitoring backend.
                       Depends only on Microsoft.Extensions.Logging.Abstractions.

WattScope.Hardware     Concrete ISensorProvider implementations
                       (LibreHardwareMonitor, NVML) plus WMI-based hardware
                       inventory and the DiscoveryService that ties them
                       together. Windows-only (net10.0-windows).

WattScope.Probe        Standalone CLI. References Core + Hardware. This is
                       the only executable in Milestone 001 -- there is no
                       desktop UI yet.

WattScope.Core.Tests       xUnit, fully mocked (FakeSensorProvider), no
                            hardware dependency -- runs anywhere.

WattScope.Hardware.Tests   xUnit, integration-style against the real WMI /
                            LibreHardwareMonitor / NVML backends on the host
                            machine. Requires Windows.
```

Dependency direction is strictly one-way: `Probe -> Hardware -> Core`, and
`Hardware.Tests -> Hardware -> Core`, `Core.Tests -> Core`. Nothing in `Core`
references `Hardware`.

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

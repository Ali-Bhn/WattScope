# WattScope

A Windows desktop application for monitoring computer energy consumption,
estimating electricity costs, and analyzing gaming sessions.

**This repository currently implements Milestone 001 only:** hardware
discovery and power-sensor validation. There is no desktop UI, database,
cost calculator, or gaming analytics yet.

## Projects

- `src/WattScope.Core` -- provider-agnostic domain model, power-sensor
  classification, sampling engine, JSON export. No hardware dependency.
- `src/WattScope.Hardware` -- LibreHardwareMonitor and NVML sensor
  providers, WMI-based hardware inventory, discovery orchestration.
  Windows-only.
- `src/WattScope.Probe` -- standalone CLI for exercising discovery and live
  monitoring.
- `tests/WattScope.Core.Tests` -- fully mocked unit tests.
- `tests/WattScope.Hardware.Tests` -- integration tests against the real
  hardware/backends on the host machine.

See `docs/ARCHITECTURE.md` for how these fit together and
`docs/SENSOR_CONTRACT.md` for the full sensor data contract, classification
policy, and real findings from a development machine.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK

## Build & test

```
dotnet build WattScope.slnx
dotnet test WattScope.slnx
```

## Running the probe

```
WattScope.Probe.exe --discover
WattScope.Probe.exe --discover --export discovery.json
WattScope.Probe.exe --monitor --interval 1000
WattScope.Probe.exe --monitor --duration 60 --export report.json
```

Run `WattScope.Probe.exe --help` for full usage.

Some power sensors (notably Intel CPU package/core power via
LibreHardwareMonitor) require the process to run elevated (Administrator).
Without elevation, WattScope.Probe still runs correctly and reports which
sensors were unavailable or limited -- see `docs/SENSOR_CONTRACT.md`,
section "Real findings from this development machine".

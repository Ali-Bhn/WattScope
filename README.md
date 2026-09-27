# WattScope

A Windows desktop application for monitoring computer energy consumption,
estimating electricity costs, and analyzing gaming sessions.

**Implemented so far:** Milestone 001 (hardware discovery and power-sensor
validation) and Milestone 002 (energy calculation, electricity cost, SQLite
persistence) -- see `docs/PROJECT_STATE.md`. There is no desktop UI or
gaming analytics yet.

## Projects

- `src/WattScope.Core` -- provider-agnostic domain model, power-sensor
  classification, sampling engine, JSON export, energy integration,
  estimation, pricing, reporting, session lifecycle. No hardware or
  database dependency.
- `src/WattScope.Storage` -- SQLite persistence (sessions, compact power
  samples, energy aggregates, prices, settings, migrations, recovery).
- `src/WattScope.Hardware` -- LibreHardwareMonitor and NVML sensor
  providers, WMI-based hardware inventory, discovery orchestration.
  Windows-only.
- `src/WattScope.Probe` -- standalone CLI for exercising discovery and live
  monitoring.
- `tests/WattScope.Core.Tests` -- fully mocked / synthetic-data unit tests.
- `tests/WattScope.Storage.Tests` -- tests against real temporary SQLite
  databases (no hardware).
- `tests/WattScope.Hardware.Tests` -- integration tests against the real
  hardware/backends on the host machine.

See `docs/ARCHITECTURE.md` for how these fit together,
`docs/SENSOR_CONTRACT.md` for the sensor data contract, classification
policy, and real findings from a development machine,
`docs/ENERGY_MODEL.md` for the energy/estimation/cost rules, and
`docs/STORAGE.md` for the database.

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

WattScope.Probe.exe --set-price 0.3175          # EUR/kWh, '.' as decimal separator
WattScope.Probe.exe --record --duration 600     # persistent energy monitoring
WattScope.Probe.exe --summary                   # latest session: energy + cost
WattScope.Probe.exe --summary --month 2026-09   # monthly with daily breakdown
WattScope.Probe.exe --selftest                  # synthetic validation, no hardware
```

Run `WattScope.Probe.exe --help` for full usage.

On the development machine, Intel CPU package/core power via
LibreHardwareMonitor only read correctly when the process ran elevated
(Administrator); unelevated it reads 0 W. This is a per-machine observation,
not a universal rule. Without elevation, WattScope.Probe still runs
correctly: a 0 W CPU package reading is rejected, and the wall-power
estimate is reported as partial (and not costed) rather than silently low
-- see `docs/SENSOR_CONTRACT.md` section 4 and `docs/ENERGY_MODEL.md`.

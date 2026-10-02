# WattScope

A Windows desktop application for monitoring computer energy consumption,
estimating electricity costs, and analyzing gaming sessions.

**Implemented so far:** Milestone 001 (hardware discovery and power-sensor
validation), Milestone 002 (energy calculation, electricity cost, SQLite
persistence), and Milestone 003 (sub-bucket electricity pricing and the
first desktop application, `desktop/`), and Milestone 004 (tray, single
instance, monitoring/sidecar lifecycle), Milestone 005 (gaming session
detection and energy analytics) and Milestone 006 (application completion:
reports, settings, price history, first run, diagnostics, data management) --
see `docs/PROJECT_STATE.md`. The application is feature complete; real-world
validation (real game, installers, Administrator, shutdown/logoff) is
deliberately deferred and **unverified** -- `docs/PRE_RELEASE_CHECKLIST.md`.

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
  monitoring; also the executable the desktop app's sidecar runs (`--serve`).
- `desktop/` -- the Tauri 2 + React + TypeScript + Tailwind desktop
  application. See "Running the desktop app" below.
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
- For the desktop app (`desktop/`): Node.js 20+, Rust (stable, MSVC
  toolchain) via rustup, and the Tauri 2 prerequisites (WebView2, present by
  default on current Windows 10/11)

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

## Running the desktop app

The desktop app's native sidecar is `WattScope.Probe.exe` itself (`--serve`)
-- Tauri does not build or bundle the .NET side automatically, so publish it
first:

```
cd desktop
npm install
powershell -File scripts/publish-probe.ps1   # publishes the sidecar binary into src-tauri/binaries/

npm run tauri dev                            # live-reloading dev build
npm run tauri build                          # release installer, under src-tauri/target/release/bundle/
```

`scripts/publish-probe.ps1` must be re-run whenever `WattScope.Probe` or
anything it depends on (`WattScope.Core`/`Hardware`/`Storage`) changes --
Tauri bundles whatever binary is already sitting in `src-tauri/binaries/`,
it does not invoke `dotnet` itself.

The app is local-first: no cloud dependency, no accounts, no telemetry, no
subscriptions. It runs only when you launch it -- it does **not** start with
Windows. Closing the window hides WattScope to the system tray and monitoring
keeps running; use the tray's *Exit WattScope* (or Settings -> *Exit WattScope*)
to stop it. Launching it a second time just shows the existing window.

It never elevates itself. Launched normally, GPU power works and CPU package
power shows an explicit "Partial monitoring" state (never a substituted 0 W,
and no cost for an incomplete estimate) on machines where it needs elevation
-- see `docs/SENSOR_CONTRACT.md` section 4. For complete CPU monitoring, launch
`wattscope.exe` yourself with *Run as administrator*; that is an interim
limitation, with automatic startup and a least-privilege privileged helper
intentionally deferred (`docs/ARCHITECTURE.md`, "Privilege model").

Lifecycle, performance and long-session checks against the real packaged app:
`desktop/scripts/validate-lifecycle.ps1`, `desktop/scripts/measure-background.ps1`
and `desktop/scripts/soak.ps1 -Minutes 240` (memory, handles, CPU and database
growth sampled over time, with per-hour trends)
(all launch the real app but record into a **scratch** database via
`WATTSCOPE_DB` and refuse to run while WattScope is already running; never
point validation at `%LOCALAPPDATA%\WattScope\wattscope.db`). To look at the
UI without a backend (screenshots, layout checks): `npx vite build --mode mock`
renders it on synthetic data in a plain browser (`src/dev/mock.ts`). Procedures that need a
person (sleep/resume, the tray menu, logoff) are in `docs/MANUAL_VALIDATION.md`.
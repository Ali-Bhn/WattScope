# WattScope

A Windows desktop application that continuously monitors how much electricity
your PC uses, accumulates the energy over time and estimates what it costs.
Gaming-session analytics are an optional extra on top of that.

**Version 0.1.0 -- release candidate.** Release notes, the installer checksum
and the known limitations are in [`docs/RELEASE_NOTES.md`](docs/RELEASE_NOTES.md).

## What it does

- **Continuous monitoring** of CPU and GPU power, one reading per second,
  running quietly in the system tray when you close the window.
- **Energy accumulation** in kWh, per monitoring session, day and month.
- **Electricity cost** from the price(s) per kWh you enter (price history is
  supported). A cost is shown only when the estimate is complete and a price
  exists; otherwise you see `--`.
- **History and reports**: past sessions, daily and monthly reports.
- **Optional gaming analytics**: detects a running game and reports the energy
  and estimated cost of that gaming session (see "Gaming analytics" below).
- **Local storage**: everything is kept in a SQLite database on your PC.
- **Languages**: English, German and Persian (with right-to-left layout).
- **Windows only** (see "System requirements").

## Measured, estimated, unavailable

WattScope labels every figure with how it was obtained:

- **MEASURED** -- read from a hardware sensor: CPU package power and GPU board
  power, when your machine and drivers expose them.
- **ESTIMATED** -- calculated. WattScope does **not** measure power at the wall
  socket. The "system" / wall-power figure is an estimate built from the measured
  CPU and GPU power plus assumptions you can change in Settings: a constant for
  everything else in the PC ("other system power": motherboard, RAM, storage,
  fans) and a power-supply efficiency. The defaults (50 W, 90 %) are
  placeholders, not measurements of your machine.
- **UNAVAILABLE** -- the sensor could not be read. This is shown as `--`, never
  as 0.

When an input is unavailable the estimate is **Partial**: it is shown as a
lower bound, kept separate from Complete figures, and **no cost is calculated
for it**. A Partial figure is never presented as a Complete one.

## System requirements

- Windows 10 or 11, 64-bit. No other operating system is supported.
- Microsoft Edge WebView2 runtime. It is included with current Windows 10/11;
  if it is missing, the installer tries to download Microsoft's bootstrapper,
  which needs an internet connection (this case has not been tested).
- Which sensors exist varies from PC to PC. GPU board power is measured when a
  valid sensor exists (NVIDIA via NVML was validated); CPU package power may be
  unavailable, or may need administrator rights, depending on the CPU and
  system.

No minimum RAM, CPU or disk size has been defined or tested.

## Installation

The installer is `WattScope_0.1.0_x64_en-US.msi` (per-machine, installs to
`C:\Program Files\WattScope`, and needs administrator approval). Its SHA-256 is
published in [`docs/RELEASE_NOTES.md`](docs/RELEASE_NOTES.md); compare it
before installing (`Get-FileHash .\WattScope_0.1.0_x64_en-US.msi`).

The installer is **not code-signed yet**, so Windows shows "Unknown
publisher" in its permission prompt, and may show a SmartScreen notice for a
downloaded file. That reflects the missing signature, not a problem found in
the file. Continue only if you obtained the installer from a source you trust
and the checksum matches, and use the standard Windows prompt options.

Uninstalling removes the program but keeps your history in
`%LOCALAPPDATA%\WattScope`. The installer interface is English only.

## Using it

WattScope runs only when you start it; it does **not** start with Windows and
installs no service. Closing the window hides it to the system tray and
monitoring continues; use the tray's *Exit WattScope* (or Settings -> *Exit
WattScope*) to stop it. Starting it a second time just shows the running window.

WattScope never elevates itself. Started normally, GPU power works and, on
machines where CPU package power needs administrator rights, monitoring shows
"Partial monitoring" with no cost. For complete CPU monitoring, start
`wattscope.exe` yourself with *Run as administrator*.

## Measurement limitations

- CPU package power may be unavailable on some machines or without
  administrator rights. WattScope then keeps running in Partial mode.
- Some sensors report an implausible 0 W. WattScope rejects such readings
  instead of storing them as a real measurement.
- Monitors/displays and external devices (peripherals, speakers, network
  gear) are not measured; WattScope has no meter input for them.
- The system/wall figure depends on your "other system power" and PSU
  efficiency assumptions, so treat it as an estimate, not as the reading of a
  calibrated wall meter. Better assumptions give better estimates.
- Sensor coverage differs between PCs; do not expect the same figures on
  every machine.

## Privacy

WattScope is local-first. Monitoring history, prices and settings are stored in
a SQLite database on your PC (`%LOCALAPPDATA%\WattScope\wattscope.db`). It needs
no account and no cloud service, and WattScope itself does not send usage
data or telemetry anywhere. This describes WattScope's own behaviour; the
Windows components it runs on (such as the WebView2 runtime) are separate
software and may make their own network connections.

## Gaming analytics (optional)

WattScope can recognise a running game and report the energy and estimated cost
of that session. It is passive: it reads the operating system's ordinary list
of running programs and the power readings it already collects. It does not
inject code into games, hook them, read their memory, modify game files,
simulate input or touch network traffic. This is how the feature is built, **not
a guarantee about any anti-cheat system**: WattScope has not been tested against
specific anti-cheat products and makes no compatibility claim. If a game or
anti-cheat vendor objects to monitoring software, do not run WattScope with it.
Details: [`docs/GAMING.md`](docs/GAMING.md).

---

# For developers

Implementation status and validation history: `docs/PROJECT_STATE.md`;
remaining real-world validation: `docs/PRE_RELEASE_CHECKLIST.md`.

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

## Build requirements

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
npm run tauri build                          # release installer (MSI), under src-tauri/target/release/bundle/msi/
```

v0.1.0 is distributed as an **MSI only** (`bundle.targets` is `["msi"]`). It
is a per-machine MSI: the program is installed under
`C:\Program Files\WattScope`, and installing needs UAC / administrator
authorization. Each user's WattScope history stays under
`%LOCALAPPDATA%\WattScope`, and uninstalling preserves it. The installer is
not code-signed, so Windows shows "unknown publisher". Not validated yet:
upgrading from one version to the next (there is only one version), and
installing or running as a standard user or from a second Windows account --
see `docs/PRE_RELEASE_CHECKLIST.md`, section C.

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
# Sensor Contract

This document is the authoritative description of what WattScope's hardware
and sensor layer discovers, how it classifies power sensors, and what
guarantees it does and does not make. It is written for whoever implements
Milestone 002+ (the UI, cost calculator, gaming analytics) against
`WattScope.Core` and `WattScope.Hardware`.

## 1. Two independent discovery paths

Hardware **inventory** (what devices exist) and **sensor** data (what values
they report) are discovered separately and must never be conflated:

- `WattScope.Hardware.SystemInfo.WmiHardwareInventoryProvider` queries WMI
  (`Win32_Processor`, `Win32_VideoController`, `Win32_BaseBoard`,
  `Win32_PhysicalMemory`, `Win32_DiskDrive`) to list what hardware exists,
  independent of whether any sensor for it is reachable. This always runs and
  always contributes to `HardwareInventory`/`DiscoverySnapshot.Hardware`.
- Each `ISensorProvider` (currently `LibreHardwareMonitorProvider`,
  `NvmlSensorProvider`) separately reports the hardware *it* can see plus the
  sensors on that hardware. A provider that fails to initialize contributes
  no hardware and no sensors, but WMI's inventory for that same physical
  device is still present.

This is why a discovery snapshot can legitimately list the same physical GPU
three times under `Hardware` (once per source: `Wmi`, `LibreHardwareMonitor`,
`Nvml`) with three different `Id` values -- they are independent views, not
duplicates to be merged. No attempt is made to unify them into one hardware
record in this milestone (see Section 5).

## 2. `SensorReading` field contract

Every reading, from every provider, carries:

| Field | Meaning |
|---|---|
| `ProviderId` | `"LibreHardwareMonitor"` or `"Nvml"` |
| `HardwareId` | Provider-native, stable-within-a-session identifier of the owning device |
| `HardwareName` | Human-readable device name |
| `SensorId` | Provider-native, stable-within-a-session identifier of the sensor |
| `SensorName` | Human-readable sensor name (e.g. `"CPU Package"`) |
| `SensorType` | Provider-native sensor category string (e.g. `"Power"`, `"Temperature"`) |
| `Unit` | e.g. `"W"`, `"°C"`, `"RPM"` |
| `Value` | `double?` -- present only when `Availability == Available` |
| `Availability` | See below |
| `Timestamp` | UTC, set once per discovery/sampling pass |

`SensorId`/`HardwareId` are **preserved verbatim** from the provider
(LibreHardwareMonitor's `Identifier.ToString()`, e.g.
`/intelcpu/0/power/0`; NVML's device UUID). They are not derived from, or
dependent on, the display name.

### `SensorAvailability`

A reading is never silently treated as zero. `Value` is `null` whenever
`Availability != Available`:

- `Available` -- `Value` is a real, finite number.
- `NotSupported` -- the provider recognizes the sensor but the underlying
  query is not supported on this hardware/driver (e.g. NVML power query on
  a GPU that doesn't expose it).
- `NullValue` -- the provider's own value was `null` at read time.
- `NotANumber` -- the provider's own value was `NaN`.
- `Infinite` -- the provider's own value was `+/-Infinity`.
- `UpdateFailed` -- the per-tick refresh call for that hardware threw.
- `DeviceDisconnected` -- the provider reported the device itself is gone
  (currently only distinguishable for NVML's `NVML_ERROR_GPU_IS_LOST`).

## 3. Power sensor roles and the precedence policy

`PowerSensorClassifier` (in `WattScope.Core.Classification`) assigns every
discovered `SensorType == "Power"` reading to one of:

`CpuPackage`, `CpuCores`, `CpuGraphics`, `GpuBoard`, `GpuChip`, `Other`.

**A sensor gets at most one role. Sensors are never summed across roles or
across providers.** `Other` covers both sensors we recognize but
deliberately exclude (e.g. a power *limit*, not a draw) and sensors we don't
recognize at all -- `ClassificationSource` on each candidate distinguishes
these (`KnownNamePattern` vs. `Unclassified`).

### Known name mappings (LibreHardwareMonitor, `SensorType == Power`)

CPU hardware:

| Sensor name | Role |
|---|---|
| `CPU Package` | `CpuPackage` |
| `CPU Cores` | `CpuCores` (already an LHM-computed aggregate; we do not sum per-core sensors ourselves) |
| `CPU Graphics` | `CpuGraphics` (integrated GPU; absent on this dev machine's i9-14900K**F**, which has no iGPU) |
| `CPU Memory` | `Other` (DRAM/RAPL power; not one of our roles) |
| `CPU Core #N` | `Other` (per-core; never summed into `CpuCores`) |

GPU hardware (Nvidia/AMD/Intel):

| Sensor name | Role |
|---|---|
| `GPU Power` / `GPU Package` | `GpuBoard` |
| `GPU Chip Power` | `GpuChip` |
| `GPU Power Limit` | `Other` -- **a configured limit, not a measured draw; must never be read as a value** |

NVML: the single sensor it reports (`nvmlDeviceGetPowerUsage`) is always
labeled `"GPU Board Power"` and mapped to `GpuBoard`.

These tables are the entirety of the mapping. Anything not listed becomes
`Other` / `Unclassified` rather than being guessed from a substring match.
Neither backend exposes a stronger "role" API than (hardware type, sensor
type, name, index); see `ClassificationSource.KnownNamePattern`'s XML doc for
why name matching is the closest thing to reliable metadata available here.

### Selection precedence

For each role, `PowerSensorClassifier` selects **at most one** sensor:

- **CPU roles** (`CpuPackage`, `CpuCores`, `CpuGraphics`): only
  LibreHardwareMonitor ever reports these. The discovered sensor is selected
  directly, per physical device.
- **GPU roles** (`GpuBoard`, `GpuChip`): both LibreHardwareMonitor and NVML
  can report a value for the same physical NVIDIA GPU.
  - If exactly one NVIDIA device is reported by NVML **and** exactly one by
    LibreHardwareMonitor for that role, they are assumed to be the same
    physical GPU, and **NVML is selected** (direct vendor driver query,
    preferred over LibreHardwareMonitor's derived value). The
    LibreHardwareMonitor candidate is kept in `Candidates` with
    `HasOverlap = true` but is not `Selected`.
  - If either provider reports more than one device for that role (multiple
    GPUs), cross-provider correlation is **not attempted** -- there is no
    reliable way to match an NVML device index to a LibreHardwareMonitor
    hardware entry without OS-level device-path correlation, which this
    milestone does not implement. `Selected` is `null`; every candidate from
    every device is still listed, clearly labeled, so no data is hidden --
    it is just not picked for the caller.

No role's selection is ever a sum of multiple sensors. If a package-level
sensor is unavailable, this application does **not** synthesize one by
adding core sensors together -- see Engineering Principle #3/#8 in the
milestone brief.

## 4. Real findings from this development machine

Captured via `WattScope.Probe.exe --discover` / `--monitor`, **not**
fabricated. Machine: Intel Core i9-14900KF, NVIDIA GeForce RTX 5070 Ti,
Windows 11 Pro, unelevated process:

- **GPU board power works out of the box.** Both NVML and
  LibreHardwareMonitor report it; NVML is selected per the policy above.
  Values tracked real load changes tick-to-tick (observed range ~43-57 W
  during idle/light load over a 10+ minute unattended run).
- **`CPU Package` and `CPU Cores` read a steady `0.0 W` for the entire
  session (8s and 10-minute runs both), never varying, in the non-elevated
  process.** (See "Elevated comparison" below for the elevated result.) This was verified
  to *not* be a first-read RAPL cold-start artifact (which would resolve
  itself after the first tick) -- it stayed at exactly 0.0 across hundreds of
  ticks. This machine's process was **not elevated**
  (`WindowsPrincipal.IsInRole(Administrator) == false`), and
  LibreHardwareMonitor's Intel RAPL/MSR power path depends on its WinRing0
  kernel driver, which requires administrator privileges to load. The
  sensors are still *created* (so they appear in `SensorInventory` as
  `Available` with `Value == 0.0`, not as `NullValue`) -- LibreHardwareMonitor
  itself does not distinguish "no driver access" from "genuinely zero" for
  this sensor. **This is a known, documented limitation of the non-elevated
  session, not a WattScope bug.**
- **No `GPU Chip Power` sensor was found** on this GPU/driver combination --
  only board-level power is exposed. `GpuChip` has no selection on this
  machine; this is an accurate reflection of sensor availability, not a
  defect.

### Elevated comparison (brief Section 12) -- completed

The elevated comparison could not be run from the original automated
session (no interactive UAC consent available). It was subsequently run
**manually by the developer** from an elevated prompt
(`WattScope.Probe.exe --discover`) on the same machine (Intel Core
i9-14900KF, NVIDIA GeForce RTX 5070 Ti, MSI PRO Z790-P WIFI). Reported
results:

| | Non-elevated | Elevated |
|---|---|---|
| `Elevated` | False | True |
| LibreHardwareMonitor | Available | Available |
| NVML | Available | Available |
| Total discovered sensors | 159 | 264 |
| `CPU Package` (LHM) | 0.0 W (flat, every tick) | **45.40 W** |
| `CPU Cores` (LHM) | 0.0 W (flat, every tick) | **44.14 W** |
| `GPU Package` (LHM, role `GpuBoard`) | tracked load | 43.01 W |
| `GPU Board Power` (NVML, role `GpuBoard`) | tracked load | 43.01 W |
| Power sensors discovered | -- | 6 |
| Selected `CpuPackage` | LHM (reading 0.0 W) | LHM |
| Selected `GpuBoard` | NVML | NVML |

Conclusions, stated narrowly:

- **On this machine, elevation is associated with real CPU package/core
  power readings.** Non-elevated reads were a constant 0.0 W; elevated reads
  are plausible non-zero values. This is consistent with (but does not by
  itself prove) the explanation that LibreHardwareMonitor's RAPL/MSR access
  path needs a kernel driver that only loads with administrator rights.
- **This is not a claim that elevation is universally required.** Other
  CPUs, other LibreHardwareMonitor versions, or other driver setups may
  expose CPU power without elevation, or may not expose it even with it.
  WattScope must continue to treat CPU power availability as a per-machine,
  per-session runtime fact.
- The larger elevated sensor count (264 vs. 159) means elevation unlocks
  sensors beyond power (e.g. additional motherboard/SuperIO sensors); those
  are not used for energy calculation.
- **The precedence policy held under elevation:** `CPU Cores` (44.14 W) is
  a subset of `CPU Package` (45.40 W) and is never added to it; LHM's
  `GPU Package` and NVML's `GPU Board Power` report the same physical
  quantity (both 43.01 W) and are never added together -- NVML is selected,
  the LHM candidate is kept as an overlapping, unselected candidate.
- **A 0.0 W CPU package reading from a non-elevated session is
  indistinguishable, at the sensor level, from a genuine zero.**
  LibreHardwareMonitor reports it as `Available`. Consumers (e.g. a future
  power estimator) must therefore not treat a non-elevated CPU package
  reading of exactly 0.0 W as trustworthy.

### Future requirement: privileged sensor access without an elevated UI

Running the whole desktop application as administrator to obtain CPU power
is **not acceptable** for the future UI: it widens the attack surface of a
large process (UI framework, web view, file dialogs) and forces a UAC prompt
on every launch. A later milestone must provide a narrowly-scoped privileged
component instead -- e.g. a minimal elevated Windows service or broker that
only performs sensor reads and exposes selected readings over an
authenticated, local-only IPC channel (named pipe with an ACL restricted to
the interactive user), while the UI and the energy/cost engine remain
unelevated. Requirements for that component (not implemented yet):

- Least privilege: reads sensors only; no arbitrary command execution, file
  access, or configuration of hardware.
- Local-only, access-controlled IPC; no network listener.
- Graceful degradation: if the privileged component is absent or refused,
  the unelevated app keeps working with whatever sensors are readable, and
  reports CPU power as unavailable rather than 0.

## 5. Explicit known limitations (Milestone 001 scope)

- Cross-provider device identity for **multiple** GPUs of the same vendor is
  not correlated (Section 3, "Selection precedence"). With one GPU, as on
  this dev machine, this is not an issue.
- LibreHardwareMonitor `Identifier` values are stable *within a process
  session* but are not guaranteed stable across reboots/driver updates if
  enumeration order changes. They are treated as session-scoped identifiers,
  not permanent hardware IDs.
- AMD CPU per-core power sensors, and CPU sensors on AMD/other vendors in
  general, were not validated on real hardware in this milestone (development
  machine is Intel-only); the LibreHardwareMonitor provider code path is
  vendor-agnostic (it reads whatever `Computer.Hardware` exposes), but the
  name-mapping table in Section 3 was only empirically confirmed against an
  Intel CPU.
- No storage or network sensors are read (`IsStorageEnabled` /
  `IsNetworkEnabled` are left `false` on the LibreHardwareMonitor
  `Computer` to keep the always-on sampling path lightweight, per
  Engineering Principle #7). Storage devices still appear in the
  sensor-independent WMI hardware inventory.
- **`--monitor --export` buffers every sensor reading (not just the
  classified power sensors -- all 159 discovered on this dev machine,
  including temperature/clock/load/etc.) for every tick, in memory, for
  the whole run, then writes one JSON file at the end.** Measured on this
  machine: a real 10-minute run at the default 1000 ms interval (600
  ticks) produced a **46 MB** export file. This is fine for the sessions
  this milestone targets (minutes to a couple of hours) but would become
  impractical for very long unattended runs or for exporting only what a
  future cost calculator actually needs. Restricting exported per-tick
  data to the selected power sensors, or streaming ticks to disk instead
  of buffering, are candidates for a later milestone -- neither is
  implemented here, to avoid speculative work beyond this milestone's scope.
  *Milestone 002 update:* continuous monitoring now goes through
  `--record`, which persists only the two selected energy channels to
  SQLite (~26 bytes/sample; see `docs/STORAGE.md`). `--monitor --export`
  is intentionally unchanged and remains a diagnostic tool.

## 6. Exported data

`ExportReport` (`WattScope.Core.Export`) intentionally has **no** field for
usernames, file paths, or hardware serial numbers -- `HardwareDevice` has no
serial-number property at all, and WMI queries never select one. All numeric
values are serialized by `System.Text.Json`, which always writes numbers
using invariant formatting regardless of the process's current culture.

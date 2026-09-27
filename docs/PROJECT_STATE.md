# Project State

Last updated: 2026-09-27, after implementing and validating Milestone 001
and recording the manually-run elevated comparison.

## Status: Milestone 001 implemented and validated (see caveats below)

## Acceptance criteria (brief Section 15)

1. **The solution builds successfully.** ✅ Verified: `dotnet build WattScope.slnx` -- 0 warnings, 0 errors.
2. **Hardware discovery works on the development machine.** ✅ Verified with real
   hardware: `WattScope.Probe.exe --discover` correctly lists CPU (Intel Core
   i9-14900KF), GPU (NVIDIA GeForce RTX 5070 Ti), motherboard, memory modules,
   and storage devices via WMI, independent of sensor availability.
3. **Available power sensors can be read.** ✅ Verified: GPU board power reads
   correctly from both LibreHardwareMonitor and NVML, tracking real load
   changes. CPU package/core power read `0.0 W` non-elevated and real values
   (45.40 W / 44.14 W) when the developer ran `--discover` elevated -- see
   "Elevated-vs-non-elevated comparison" below.
4. **Missing sensors are handled safely.** ✅ Verified via automated tests
   (`SensorAvailability` covers Null/NaN/Infinite/UpdateFailed/
   DeviceDisconnected; `PowerSensorClassifierTests.UnavailableReading_...`)
   and via the real run: no missing sensor ever appeared as a fabricated zero
   from WattScope's own code (LibreHardwareMonitor's own `0.0 W` for
   RAPL-without-driver-access is a limitation of that backend, reported
   verbatim, not something WattScope invents).
5. **No overlapping power measurements are double-counted.** ✅ Verified: the
   classifier never sums sensors (see `docs/SENSOR_CONTRACT.md`). On this
   machine, GPU board power is reported by both LibreHardwareMonitor and
   NVML; only NVML's reading is selected, LibreHardwareMonitor's is kept as
   an overlapping-but-unselected candidate. Covered by
   `PowerSensorClassifierTests.SingleNvidiaGpu_PrefersNvmlOverLibreHardwareMonitor_ForBoardPower`.
6. **Live monitoring runs for at least 10 minutes without an unhandled
   exception.** ✅ Verified with a real, unattended 10-minute run
   (`--monitor --duration 600 --interval 1000 --export`): exactly 600 ticks
   produced, zero gaps, zero errors, clean shutdown, exit code 0, valid 46 MB
   JSON export written. Console log and JSON report are kept locally
   (gitignored) as evidence.
7. **JSON export works.** ✅ Verified: both `--discover --export` and
   `--monitor --export` produce valid, well-formed JSON (manually inspected
   and parsed back with `ConvertFrom-Json`). Invariant numeric formatting
   verified by an automated test that serializes under `de-DE` culture and
   asserts a `.`-formatted number, not `,`.
8. **Automated tests pass.** ✅ 25/25 passing: 14 in `WattScope.Core.Tests`
   (fully mocked), 11 in `WattScope.Hardware.Tests` (real WMI/
   LibreHardwareMonitor/NVML integration on this machine). Actually executed
   via `dotnet test WattScope.slnx`, not merely written.
9. **Sensor limitations are documented.** ✅ `docs/SENSOR_CONTRACT.md`
   section 4 ("Real findings from this development machine") and section 5
   ("Explicit known limitations").
10. **`PROJECT_STATE.md` reflects the actual implementation status.** ✅ This document.

## Elevated-vs-non-elevated comparison (brief Section 12) -- completed manually

Could not be run from the automated session (no interactive UAC consent).
Subsequently run **manually by the developer** from an elevated prompt on
the development machine. Reported results: Elevated=True; LHM and NVML both
available; 264 sensors discovered (vs. 159 unelevated); 6 power sensors;
CPU Package 45.40 W, CPU Cores 44.14 W (vs. flat 0.0 W unelevated), GPU
Package (LHM) 43.01 W, GPU Board Power (NVML) 43.01 W. Selected: CPU Package
from LibreHardwareMonitor, GPU board power from NVML. No double counting:
CPU Cores is not added to CPU Package, LHM GPU Package is not added to NVML
GPU Board Power. Full table and the narrowly-stated conclusion (elevation is
associated with working CPU power *on this machine*; not claimed universal)
in `docs/SENSOR_CONTRACT.md` section 4. The requirement for a future
least-privilege sensor component (so the desktop UI never runs elevated) is
recorded there and in `docs/ARCHITECTURE.md` ("Privilege model").

## Explicitly unverified (brief Section 15: "mark as unverified if it can't be verified")

- **AMD/Intel-iGPU/multi-GPU code paths.** The classifier's multi-GPU
  ambiguity handling and the CPU name-mapping table are covered by unit
  tests with synthetic data, but were never exercised against real AMD CPU
  hardware, a CPU with an integrated GPU, or a machine with two GPUs --
  this development machine has none of those. The code is vendor-agnostic
  by construction (LibreHardwareMonitor's own abstraction), but this is a
  gap between "should work" and "was observed to work."
- **Windows Storage/Network sensor groups** were deliberately left disabled
  (`IsStorageEnabled = false`, `IsNetworkEnabled = false`) and so were never
  exercised at all in this milestone.

## Real findings from validation (not fabricated -- see `docs/SENSOR_CONTRACT.md` for full detail)

- GPU board power (NVML) tracked real load changes correctly throughout a
  10-minute run: ranged roughly 42-58 W depending on system activity.
- CPU package/core power reads a flat `0.0 W` for the entire session, every
  single tick (confirmed across 600 ticks, ruling out a RAPL first-read
  cold-start artifact) in the non-elevated process; an elevated run on the
  same machine read 45.40 W package / 44.14 W cores, so elevation is
  associated with working CPU power here.
- No `GPU Chip Power` sensor exists on this GPU/driver combination -- only
  board-level power is exposed.
- A real, unattended `--monitor --export` run for the full 10 minutes
  produced a 46 MB JSON file (600 ticks x all 159 discovered sensors per
  tick, not filtered to power sensors) -- documented as a known scaling
  limitation for future milestones, not fixed in this one (out of scope).

## Bugs found and fixed during this milestone's own validation

These were caught by actually running the code against real hardware, not
found by inspection -- kept here as a record of what validation actually
did, per the brief's "do not falsely claim runtime validation" instruction:

1. **Duplicate-key crash in `PowerSensorClassifier.Classify`.** Caused by
   `Program.cs` calling `DiscoveryService.Discover()` twice (once for a
   local snapshot, again inside `DiscoverCommand`), which re-initialized
   `NvmlSensorProvider` without clearing its device list, producing two
   identical device entries. Fixed by removing the redundant `Discover()`
   call and making both providers' `Initialize()` idempotent (clears prior
   state before repopulating).
2. **GPU count miscalculation in the cross-provider correlation logic.**
   `PowerSensorClassifier` originally counted `HardwareCategory.GpuNvidia`
   across *all* discovered devices, including the sensor-independent WMI
   inventory entry -- so a single physical GPU (reported once by WMI, once
   by LibreHardwareMonitor, once by NVML) looked like 3 GPUs, and the
   classifier refused to pick a `GpuBoard` selection at all. Fixed by
   counting only the devices that actually produced a `Power`-sensor
   candidate for that role, separately per provider.
3. **Misleading WMI device ID.** `WmiHardwareInventoryProvider` built each
   device's `Id` from the pre-classification fallback category
   (`wmi:GpuOther:0`) instead of the resolved vendor-specific category
   (`wmi:GpuNvidia:0`), even though the `Category` field itself was already
   correct. Cosmetic but confusing in exported JSON; fixed to use the
   resolved category.

## Design decisions worth knowing for Milestone 002+

- `ISensorProvider` is the seam future milestones should build against --
  never reference LibreHardwareMonitor or NVML types outside
  `WattScope.Hardware`.
- Power sensor selection never sums sensors; see
  `docs/SENSOR_CONTRACT.md` section 3 for the full precedence policy before
  building a cost calculator or gaming-analytics feature on top of it.
- Multi-GPU cross-provider correlation is intentionally unimplemented (see
  above) -- a future milestone that needs it will need real OS-level
  device-path correlation (e.g. matching PCI bus/device/function), not a
  heuristic.

## Outstanding issues / next actions

- Design a least-privilege sensor component so the future desktop UI never
  has to run elevated to get CPU power (see `docs/ARCHITECTURE.md`,
  "Privilege model").
- Consider (Milestone 002+, not now): an export mode that only includes
  selected power sensors per tick, or streams ticks to disk, to keep long
  `--monitor --export` sessions practical (see 46 MB finding above).
- Consider validating the CPU/GPU name-mapping table against AMD hardware
  when such a machine is available.

## Explicitly not implemented in this milestone (by design, per the brief)

Desktop UI, SQLite persistence, cost calculator, gaming-session analytics,
background/startup behavior, localization. These are scoped to later
milestones.

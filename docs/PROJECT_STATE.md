# Project State

Last updated: 2026-10-01, after implementing and validating Milestone 003
(sub-bucket electricity pricing and the first desktop application).

## Status

- **Milestone 001** -- complete (details below, unchanged).
- **Milestone 002** -- Energy Calculation, Electricity Cost & Persistent
  Storage: implemented and validated, **including a real elevated
  `--record` session with a correctly computed cost end to end**, with the
  limitations listed in the next section (item 5 has since been fixed by
  Milestone 003 Part A -- see the note there).
- **Milestone 003** -- Desktop Application & Live Dashboard: **implemented
  and validated** -- see the Milestone 003 section below for what was
  verified, how, and the known limitations.

# Milestone 003

Rules for Part A: `docs/ENERGY_MODEL.md` section 4. Architecture for Part
B-L: `docs/ARCHITECTURE.md` ("Data flow (--serve)", "Desktop application",
"Privilege model").

## Part A -- sub-bucket electricity price allocation

| Requirement | Verification |
|---|---|
| Price energy at the effective time of consumption, not bucket-start | `PowerInterval.SplitAt` generalizes the existing bucket-boundary split to arbitrary instants; `EnergyAccumulator.Add` splits at price-schedule instants too, live, while the real power curve still exists |
| Handle a price change within an aggregation bucket | `bucket_price_split` table (migration v2, additive): one row per (bucket, price) actually observed; `CostCalculator.AllocateBucket`'s 3-tier allocation (`Exact`/`ExactReplayed`/`ApproximateProportional`) |
| Preserve historical prices; never reprice the past silently wrong | `ElectricityPriceSchedule` unchanged (append-only); `AllocateBucket` detects when recorded splits no longer match the *current* schedule (a retroactive price add) and only then falls through to replay/approximate |
| Inspect whether persisted aggregates allow exact historical repricing | Yes, within raw retention (`SqliteEnergyStore.TryRepriceBucket` replays raw samples with the same split logic, gated by a sanity check against the bucket's own recorded `MonitoredSeconds`); **no** beyond retention -- an explicit, flagged `ApproximateProportional` result, never presented as exact |
| No unnecessary intermediate rounding | Unrounded `double`/`decimal` throughout; rounding only at final display (`MoneyRounding`), unchanged from Milestone 002 |
| Session/daily/monthly consistency | All three resolve cost through the same `CostCalculator.AllocateBucket`; energy itself (not cost) is untouched by this change |
| Preserve existing database compatibility | `energy_bucket` and every Milestone 002 table/row untouched; `bucket_price_split` is new and additive; a bucket predating this feature (or recorded with no price schedule) falls back to the unchanged bucket-start rule, which is already exact when a single price applied throughout -- the overwhelming majority of real buckets |
| Regression tests for mid-bucket price changes | `PowerIntervalSplitTests` (Core, synthetic), `CostCalculatorTests` (Core, the 3 allocation tiers against hand-computed values), `PriceAllocationTests` (**Storage**, a real SQLite database, a real `EnergyMonitor` recording a deliberate 100W->300W step profile across a price change, then a retroactive price add both with and without raw samples pruned -- the approximate tier's result is asserted to be *visibly different* from the exact one, proving it is a real approximation, not accidentally exact) |
| Full regression suite still passes | `dotnet test`: 133/133 (Core 102, Storage 20, Hardware 11), 0 warnings, 0 errors |
| Real runtime validation, not just unit tests | Ran the actual built `WattScope.Probe.exe`: fresh DB migrates to schema v2, `--set-price` prints the new effective-time-of-consumption message, `--db-info` shows the new "Price-split rows" line, `--selftest` 29/29 |

## Part B-L -- desktop application (`desktop/`)

| Requirement | Verification |
|---|---|
| Tauri 2 + React + TypeScript + Tailwind, local-first | Built; no cloud dependency, no accounts, no telemetry, no subscriptions anywhere in the code |
| Native integration without duplicate monitoring processes | New `--serve` command on `WattScope.Probe` reuses the *same* `EnergyMonitor`/`SamplingEngine` pipeline `--record` uses (not a second implementation); `MonitorLock` still limits one recorder per database |
| No shell command injection | The sidecar is spawned with fixed argv (`tauri-plugin-shell` sidecar, no string-built shell command); requests to it are structured JSON over stdin, not text commands |
| Clean startup/shutdown, safe process lifecycle | Verified with the actual built, installed-shape app (see below): window close sends a JSON `shutdown` request, the sidecar flushes and marks the session `Stopped` (not left `Active`/needing crash recovery), and the process exits before the window finishes closing |
| Dashboard shows exactly the required fields, never a substituted zero | `Dashboard.tsx`: system/CPU/GPU/other power, session duration/energy/cost, sensor availability, estimate completeness, monitoring status; CPU/GPU show an explicit unavailable reason (including `dashboard.notElevated` for `ImplausibleZero`) instead of a dash-to-zero substitution |
| Live chart: bounded history, efficient updates, no unnecessary rerendering | `LiveChart.tsx` subscribes to tick events itself and draws to canvas on a throttled `requestAnimationFrame` loop from a fixed-capacity (300-point) ring buffer kept in a `ref`, entirely outside React's render cycle -- a new sample never re-renders the rest of the UI |
| Only the information the dashboard needs leaves the sidecar | `TickMessage` (Serve/TickMessage.cs) carries ~15 scalar fields, never the full discovered sensor inventory (which can be 150-260+ sensors per `docs/SENSOR_CONTRACT.md`) |
| Historical reports: current session, previous sessions, daily, monthly, cost history; one data store | `Reports.tsx` + `get_summary`/`get_sessions` requests, which call the *same* `EnergyReportBuilder`/`SqliteEnergyStore` methods the CLI's `--summary`/`--sessions` use (`DatabaseCommands.BuildSessionReport`/`BuildPeriodReport`, factored out for this reuse) -- no second database or parallel report logic |
| Settings: price, other-component watts, PSU efficiency, monitoring interval, language, raw retention; measured vs. assumed made explicit | `Settings.tsx`; interval changes restart the sidecar with `--resume` (an honest "Paused" gap recorded, no energy invented across it) since the .NET engine has no live-reconfigure path for sampling interval; validation client-side and server-side (the existing `ArgumentException` paths in `SqliteEnergyStore`/`ElectricityPriceSchedule`/`EstimationSettings`, surfaced as structured JSON errors) |
| Localization: English, German, Persian; real RTL, not mirrored text only | `react-i18next`; `applyDocumentDirection` sets `dir`/`lang` on `<html>`; layout uses CSS logical properties (`border-e`, `text-start`, `ms-`/`me-`) throughout, audited with a repo-wide search for leftover `pl-/pr-/ml-/mr-/text-left/text-right` (none found outside explanatory comments) |
| Numbers/units/identifiers stay readable in RTL | `lib/format.ts` always formats with fixed `en-US` (Latin digit, Gregorian) conventions regardless of UI language, paired with a `<Num>` component (`dir="ltr"`, `unicode-bidi: isolate`) at every call site |
| Minimum privilege investigated; no insecure UAC bypass; UI never runs elevated | See `docs/ARCHITECTURE.md` "Privilege model": sidecar unelevated by default; an explicit, user-initiated Settings action requests a real Windows UAC prompt (`Start-Process -Verb RunAs`) for *only* the small Probe process, never the window. A full privileged-broker service was not built this milestone -- real, separately-scoped work -- which is the honest partial-monitoring state the brief allows in lieu of it |
| Performance: no busy-waiting, no redundant polling, bounded memory | Chart history bounded (above); tick path adds zero DB queries beyond what `--record` already does per sample; Reports' current-session view polls `get_summary` every 10 s (matching the existing 10 s flush interval, not arbitrary); monitoring continues independently of the UI (a separate OS process) |
| .NET test suite, frontend typecheck, frontend build, Tauri build all actually run | `dotnet test`: 133/133. `npm run typecheck`: clean. `npm run build` (Vite): clean, 228 KB JS / 12 KB CSS gzipped to ~76 KB total. `npm run tauri build`: succeeded, producing both `WattScope_0.1.0_x64_en-US.msi` (36.0 MiB) and `WattScope_0.1.0_x64-setup.exe` (26.95 MiB) |
| Real hardware data in the real built app, not mocked frontend data | Launched the actual built `src-tauri/target/release/wattscope.exe` (not `tauri dev`): it spawned a real `msedgewebview2.exe` render process and a real `wattscope-probe.exe --serve` child; `--sessions` against the live database showed a new session actively recording (`monitored 00:00:10`, growing); GPU board power ticks were real, varying values (consistent with the dev machine's earlier-documented real GPU behavior); CPU package showed the honest `ImplausibleZero`/unelevated state, matching Milestone 001/002's own documented finding for this machine -- never a substituted zero. Closing the window produced a session marked `Stopped`, not an orphan needing crash recovery |

### A real bug this validation caught (and fixed)

While smoke-testing `--serve` directly (before wiring up Tauri), the tick
stream was completely empty for several seconds of real wall-clock time
despite a 300 ms sampling interval -- `Console.In.ReadLineAsync` does not
reliably yield control back to its caller before it has a line to return,
so running the stdin request loop as a plain un-awaited async call silently
starved the sampling loop of any chance to run at all. Fixed by moving the
request loop onto its own thread via `Task.Run`. This was found by actually
spawning the process and watching its output with a dedicated harness after
two separate PowerShell-based attempts gave misleading signal (an injected
BOM corrupting the first stdin write, and `Register-ObjectEvent`-based
output capture silently losing events) -- worth recording because it is
exactly the kind of bug code review alone would not have caught.

## Known limitations (Milestone 003)

1. **No privileged broker service.** The sidecar runs unelevated by
   default; CPU package power is unavailable on machines (like this one)
   where it needs elevation. The Settings "restart elevated" action is a
   real, working, user-initiated UAC prompt for the Probe process only --
   but once triggered, the running desktop window loses its connection to
   that process (Windows gives no handle back across a UAC elevation
   boundary) until the app is restarted. A minimal, separately-installed
   elevated component exposing sensor reads over authenticated local IPC
   (`docs/SENSOR_CONTRACT.md`, "Future requirement") remains future work --
   real, scoped work (service packaging, install/uninstall, an ACL'd named
   pipe, a client protocol), not something to shortcut.
2. **Monitoring-interval change is a restart, not a live reconfiguration.**
   `WattScope.Core`'s sampling loop has no concept of changing its own
   interval mid-session; Settings' interval control restarts the sidecar
   with `--resume`, which is honest (a real "Paused" gap is recorded) but
   means a brief live-view interruption on every interval change.
3. **Gaming-performance impact (frame-rate) was not measured.** CPU/memory
   overhead of the sidecar process itself was measured on a real launch
   (~1.2 s CPU time and ~88 MiB working set over a ~27 s run, consistent
   with Milestone 002's own measured `--record` overhead, since it is the
   same code path) -- but no game was run alongside it in this environment,
   so no FPS-impact claim is made one way or the other, per the brief's own
   instruction not to claim this without controlled testing.
4. **Elevated end-to-end desktop validation was not performed.** The real
   launch validation above ran unelevated (consistent with the documented
   default); it was not re-run elevated to confirm the dashboard displays
   complete, costed CPU data end-to-end through the UI (Milestone 002 already
   validated this for the CLI/engine directly; the desktop UI adds a
   presentation layer on top of the same, unmodified data, but that specific
   combination was not re-verified interactively in this session).
5. **This repo's own frontend has no automated test suite** (no Vitest/
   Playwright) -- verification for `desktop/` relied on `tsc --noEmit`,
   `vite build`, `cargo check`, `tauri build`, and the real launch above,
   not unit/component tests. Adding frontend tests is reasonable future
   work, not attempted here to keep this milestone's scope to what the
   brief asked for.
6. The pre-existing `SamplingEngineTests.AfterAStall_DoesNotBurstCatchUpTicks`
   test is timing-sensitive and occasionally flakes under system load when
   the full suite runs in parallel (passes reliably in isolation); this is
   unrelated to Milestone 003 and was not introduced by it.

# Milestone 002

Rules: `docs/ENERGY_MODEL.md`. Database: `docs/STORAGE.md`.

## What was verified, and how

| Requirement | Verification |
|---|---|
| Integration: constant/variable power, irregular spacing, actual elapsed time | Automated (`EnergyIntegratorTests`), synthetic data with analytic answers; also `--selftest` |
| Missing / invalid readings, duplicate timestamps, long gaps, sleep (clock discontinuity), time going backwards | Automated (`EnergyIntegratorTests`, `PowerSampleBuilderTests`) |
| Sleep/resume via OS notification, flush on suspend | Automated with the monitor (`EnergyMonitorTests`); **the real `SystemEvents` suspend/resume path was not exercised** (see limitations) |
| Duplicate power sensor prevention | Automated, using the real classifier with the dev machine's elevated reading set: CPU = 45.40 W (not + 44.14 W Cores), GPU = 43.01 W once (NVML, not + LHM GPU Package); multi-GPU ambiguity, multi-socket sum |
| Estimation completeness, assumptions, validation | Automated (`WallPowerEstimatorTests`) |
| Cost: decimal, rounding, round-once, pricing changes, unpriced energy, daily/monthly incl. DST day | Automated (`CostCalculatorTests`, `EnergyReportTests`) |
| Migrations, persistence round-trip, transactions (atomic batch), recovery, resume, retention, price/settings persistence, lock | Automated against real SQLite files (`WattScope.Storage.Tests`) |
| Storage growth | Automated: 24 h at 1 Hz = **2.14 MB (~26 bytes/sample)** vs. ~46 MB per 10 min for the M001 JSON export |
| Real hardware, 5-min `--record` (unelevated) | Run on the dev machine: 301 samples, 0 gaps, GPU 22.445 Wh measured (coverage 100 %), CPU rejected every tick as `ImplausibleZero` (0 W unelevated) -> estimate **Partial**, cost correctly "n/a" (lower-bound partial cost shown separately); crossed a bucket boundary (2 buckets); `--db-info` integrity ok; read back by separate processes (`--sessions`, `--summary --day`) |
| Real unexpected termination | Dev machine: `--record` force-killed (`Stop-Process -Force`) after ~27 s. Next command recovered session as `Interrupted`, ending at its last flushed observation (18:05:02; kill at 18:05:07 -- the unflushed ~5 s are absent, not estimated). `--record --resume` recorded a 13 s gap and continued the same session (21 s + 14 s monitored) |
| M001 backward compatibility | Real run: `--discover`, `--monitor --duration 3 --export` (valid JSON, 3 ticks), legacy `--discover --interval 500` all exit 0 |
| CLI validation | `--selftest`: 29/29 checks pass (integration, pricing, persistence, crash recovery, resume, summaries) |
| **Real hardware, `--record` elevated, full pipeline including cost** | Run by the developer, elevated, on the dev machine (Session #1, 2026-09-27 18:09:19-18:14:19 local): 00:04:58 monitored, 0 gaps, CPU package 7.487 Wh (coverage 100 %), GPU board 10.718 Wh (coverage 100 %), estimated wall 24.841 Wh, completeness **Complete**. Confirms elevation gives non-zero, valid CPU package power end-to-end through integration (matches the discovery-only elevated reading recorded in `docs/SENSOR_CONTRACT.md`: this session's implied average CPU power, 7.487 Wh / 4:58 ≈ 90 W, is plausible average load, not the earlier single-point 45.40 W reading). Historical pricing was then added and re-verified (see below) -- cost came out to 0.01 EUR (unrounded 0.0086944 EUR) at 0.35 EUR/kWh, matching manual calculation (24.841 Wh = 0.024841 kWh; 0.024841 kWh x 0.35 EUR/kWh = 0.00869435 EUR) to display precision. `--db-info` integrity check: ok, both before and after. |
| Full suite | `dotnet test WattScope.slnx`: **120/120 pass** (Core 92, Storage 17, Hardware 11) |

### Performance (real 5-minute `--record`, 1 s interval, unelevated, dev machine)

- Process CPU: 4.94 s over 5:01 = **1.64 % of one core** (0.05 % of 32 logical CPUs).
- Memory: working set 98 MiB, peak 102 MiB, managed heap 7 MiB. Memory is
  bounded: pending samples are flushed every 10 s (cap 3600 on store
  failure), only open buckets are held, latency stats use a fixed histogram.
- Sampling latency (reading all providers per tick): avg 90 ms, p95 140 ms,
  max 1251 ms (the first ticks). This is LibreHardwareMonitor refreshing all
  enabled hardware -- unchanged M001 behavior, not new M002 work.
- Database flush: 29 flushes, avg 0.55 ms, max 6.6 ms.
- Database: 72 KiB file after 301 samples (mostly fixed schema pages); at
  scale ~26 bytes/sample (automated 24 h test).

## Known limitations (Milestone 002)

1. **Real OS sleep/wake was not exercised.** The `SystemEvents.PowerModeChanged`
   hookup in `--record` is untested on real suspend (putting the dev machine
   to sleep was not done in this session). Independently of it, any sleep is
   caught by the integrator as a `SamplingStall` or `ClockDiscontinuity` gap,
   so no energy can be fabricated across it -- only the gap *reason* would be
   less specific. Whether `Stopwatch` (QPC) advances during sleep on this
   machine was not measured; the design does not depend on it.
2. **Graceful stop on console close / logoff / shutdown (`SIGHUP`/`SIGTERM`
   handlers) was not exercised.** Ctrl+C and duration stops were. If the
   close handler does not finish within Windows' time limit, the session is
   recovered as `Interrupted` (≤ ~10 s of data lost, never fabricated).
3. **CPU power without elevation**: on this machine CPU package reads 0 W
   unelevated, so unelevated sessions produce only a partial (uncosted)
   estimate. A complete, costed estimate needs an elevated process --
   **confirmed working** by a real elevated `--record` run (Session #1,
   above): CPU package 7.487 Wh at 100 % coverage, completeness Complete,
   cost computed correctly once priced. A least-privilege sensor component
   so the desktop UI itself never needs to run elevated remains future work
   (`docs/ARCHITECTURE.md`, "Privilege model").
4. The estimation defaults (50 W other components, 90 % PSU efficiency) are
   placeholders, not measured for this machine. There is no wall-meter input.
5. **Fixed by Milestone 003 Part A** (see `docs/ENERGY_MODEL.md` section 4):
   price changes are now allocated at the effective time of consumption,
   splitting a bucket a price change falls inside instead of pricing the
   whole bucket at whichever price was in effect at its start. The rest of
   this item is kept as the historical record of the Milestone 002 behavior
   and the real edge case it caused. Negative (dynamic-tariff) prices and
   multiple currencies are still not supported. **Observed in practice**
   during elevated validation: a price
   set 24 seconds before a session started (`--set-price` with no
   `--effective`, i.e. "now") did not price that session at all, because
   the session's whole 5-minute run fell inside one 15-minute UTC bucket
   (18:00-18:15 local) whose *start* preceded the price's effective time --
   the documented rule ("a price that becomes effective mid-bucket applies
   from the next bucket", `docs/ENERGY_MODEL.md` section 4) working exactly
   as designed, not a defect, but a sharp edge for a user setting price
   "just before" recording rather than well before the containing bucket
   starts. Fixed for that data by adding a second price entry effective at
   local midnight the same day (`--effective 2026-09-27T00:00:00`); the
   original 18:08:55 entry was left in place (harmless: same price,
   superseded for that bucket by the earlier entry, and the schedule keeps
   both timestamps). The candidate follow-up noted at the time ("price at
   finer-than-bucket granularity for the bucket a price change falls into")
   is exactly what Milestone 003 Part A implemented.
6. A session keeps the estimation settings it was created with, even when
   resumed after settings change.
7. Up to ~10 s of the most recent data is lost on a process crash (flush
   interval); with `synchronous=NORMAL`, the most recent commits may also be
   lost on an OS crash/power loss.
8. Energy is sample-based: spikes shorter than the sampling interval are not
   captured. The trapezoid is exact only for linear change between samples.
9. A single missing reading removes both adjacent intervals from that
   channel's energy (by design: no interpolation across unverified
   readings), so coverage can drop faster than the number of missing samples.
10. Sampling reads the full sensor set each tick (~90 ms/tick here) even
    though only two channels are used. Restricting refresh to the bound
    hardware would cut CPU cost; not done (would change M001 provider behavior).
11. Raw samples are pruned at `--record` start, hourly during recording and
    on `--retention`; there is no background service, so nothing is pruned
    while WattScope is not running.
12. `WattScope.Probe` itself has no automated tests for argument parsing;
    it was exercised manually (above) and via `--selftest`.

# Milestone 001 (retained record)

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
- ~~Consider an export mode that only includes selected power sensors~~ --
  addressed in Milestone 002 by `--record` + SQLite (`docs/STORAGE.md`);
  `--monitor --export` intentionally unchanged as a diagnostic tool.
- Consider validating the CPU/GPU name-mapping table against AMD hardware
  when such a machine is available.

## Explicitly not implemented in this milestone (by design, per the brief)

Desktop UI, SQLite persistence, cost calculator, gaming-session analytics,
background/startup behavior, localization. (SQLite persistence and the cost
calculator have since been delivered by Milestone 002; the rest remain
future work.)

# Project State

Last updated: 2026-10-03, during pre-release validation (feature freeze) on
top of Milestone 006.

## Status

- **Pre-release validation**: Phases 1-5 done (automated gate, flaky test,
  soak, real-database rehearsal on copies, production UI/accessibility) --
  see "Pre-release validation" below. Phase 6 (Administrator Complete
  monitoring) and Phase 7 (real Warzone, unelevated) were run by the user and
  PASSED. **No release blocker found.** Phase 8 (FPS) was cancelled. Phase 9
  (installers) is **done**: a packaging defect in the NSIS installer was found
  by audit, v0.1.0 now ships **MSI only**, and the MSI passed static
  inspection and a real install / run / uninstall / reinstall / uninstall
  lifecycle with the history preserved (details and what stays unverified in
  "Phase 9" below). Phases 10-12 (shutdown/restart/logoff, release candidate)
  not started.

- **Milestone 006** -- Application Completion, UX Polish & Feature-Complete
  Candidate: **implemented; the application is FEATURE COMPLETE** (see the
  Milestone 006 section and "Feature freeze classification" below). All
  real-world validation is **deliberately deferred** and **UNVERIFIED** --
  `docs/PRE_RELEASE_CHECKLIST.md`. Real Warzone, FPS impact, Administrator
  Complete sessions, installing/upgrading/uninstalling, shutdown/restart/logoff
  were not run.

- **Milestone 001** -- complete (details below, unchanged).
- **Milestone 002** -- Energy Calculation, Electricity Cost & Persistent
  Storage: implemented and validated, **including a real elevated
  `--record` session with a correctly computed cost end to end**, with the
  limitations listed in the next section (item 5 has since been fixed by
  Milestone 003 Part A -- see the note there).
- **Milestone 003** -- Desktop Application & Live Dashboard: **implemented
  and validated** -- see the Milestone 003 section below. Two of its
  limitations were changed by Milestone 004 (the "restart elevated" action was
  removed; interval changes no longer restart the process) -- see the notes
  there.
- **Milestone 004** -- Tray, Single Instance & Monitoring Lifecycle:
  **implemented and validated**, with a short list of items that still need a
  human at the machine and are marked **UNVERIFIED** (most of the native tray
  menu, logoff/shutdown, an Administrator launch, installer install/uninstall).
  **Real Windows sleep/resume was manually validated and PASSED** (Session #18,
  below). Automatic Windows startup and a privileged sensor
  helper/service are intentionally deferred.

- **Milestone 005** -- Gaming Session Detection & Energy Analytics:
  **implemented and validated** with synthetic processes (automated) and with
  harmless stand-in processes on the real packaged app. **Call of Duty /
  Warzone through the real Battle.net was validated unelevated on 2026-10-02**
  (Phase 7 below: detected automatically, one Partial session). An elevated
  (Complete) gaming session, a second real game and FPS impact stay UNVERIFIED.

# Pre-release validation (feature freeze)

Baseline `b8100aa`. No product feature was added in this phase; changes are
limited to verified fixes, test infrastructure and documentation. Phases 1-5
(automated gate, flaky test, soak, real-database rehearsal, production UI and
accessibility) were run on 2026-10-02, followed the same day by the user-run
Phase 6 (Administrator) and Phase 7 (real Warzone). Phase 8 (FPS) was
cancelled, Phase 9 (installers, MSI only) was done on 2026-10-02/03. **Phases
10-12 (shutdown/restart/logoff, release candidate) have not been started** and
stay UNVERIFIED in `docs/PRE_RELEASE_CHECKLIST.md`.

**Release blockers found: none.** Two defects were fixed (a test-script race
and high-contrast marking); the known non-blocking issues are listed in the
checklist.

Every run of Phases 1-5 used a scratch database (`WATTSCOPE_DB`, path printed
and checked first); Phases 6, 7 and 9 deliberately ran against the normal
database, as their sections say. For Phases 1-5 the real database
(`%LOCALAPPDATA%\WattScope\wattscope.db`, 159,744 bytes, modified
2026-10-01T22:16:37Z, SHA-256 `87DC59BB...1B556E`) was hashed before the first
step and is byte-identical after the last one; no `-wal`/`-shm`/backup file
appeared next to it.

## Phase 1 -- automated release gate

| Check | Result |
|---|---|
| HEAD / tree at the start | `b8100aa`, clean |
| `dotnet test WattScope.slnx` | baseline **389** passed; **394** after Phase 2 (Core 214, Storage 72, Hardware 11, Probe 97), 0 failed |
| Frontend `npm test` | **183** passed, 12 files (17 of them the localization completeness suite: every `t()` key in EN/DE/FA, no unused key, placeholders, plurals, Persian script) |
| `npm run typecheck`, `npm run build` | clean |
| `cargo test` | **15** passed |
| Sidecar publish + `npm run tauri build` | MSI 36.17 MiB, NSIS 27.07 MiB |
| Production configuration | release profile (`lto`, `panic=abort`), no `devtools` feature, CSP set, the only capability is running the bundled sidecar |
| Mock/dev code excluded | one JS chunk; 0 hits for `mockIPC`, `installMock`, `VITE_MOCK`, `localhost:1420`, `sourceMappingURL`, `mock` in `dist/` |
| Schema | a fresh scratch database is created at **version 4**, integrity ok; `--selftest` 29/29 |
| Migration tests | 7 (fresh -> latest, M004 -> v4 keeps history, v3 repair, backup made only when a migration is pending, failed migration rolled back) -- part of the .NET suite |
| `validate-lifecycle.ps1` (packaged app, scratch DB) | **25/25 on three consecutive runs of the final build** after the script fixes below (24/24 x3 on the previous build before the 25th check was added) |
| Final regression after all fixes | .NET 394, frontend 183, typecheck clean, Rust 15 |

### The lifecycle-script failures (investigated, not re-run until green)

The first two runs each had one failure, a different one each time
("restarts are bounded", then "no duplicate sidecars"). An independent
process watcher (PID, parent and command line every ~250 ms) showed:

- **Script defect, fixed.** The script counted processes *named*
  `wattscope-probe` as "the sidecar", but it also runs that executable as a
  CLI (`--sessions`). A CLI process still visible for a moment was taken for a
  restarted sidecar, so "a new sidecar appears" passed before the real one
  existed and the next check then counted 0. At that moment there was **no**
  second `--serve` process. The sidecar is now identified by its command line
  (`Get-WattScopeSidecar`). This is the same failure M006 recorded as "not
  reproduced".
- **Product behaviour confirmed correct.** In every instrumented run the
  spawn sequence was exactly `--serve`, three `--serve --resume` (back-off
  1/2/4 s), give-up after the 4th crash, and never two sidecars at once. A
  separate run with no CLI calls showed no respawn for 75 s after the give-up.

### The fresh session after a give-up (investigated; cause not proven)

In the very first run a *fresh* session (#2, started 12:48:46, i.e. a
`--serve` without `--resume`) appeared about 9 s after the host had given up
restarting the sidecar. The user confirmed they did not knowingly click Retry
or tray Start. Investigation, without changing production code:

- **Code audit.** A crash restart can only pass `--resume` or `--paused`
  (`spawn_args(_, false)`); `EnergyMonitor.Resume` never creates a session and
  a paused sidecar creates none. A fresh session needs `spawn_args(_, true)`,
  reached only from application start-up and from `tray_start` (tray *Start*
  or the banner's *Retry*), or a `start_monitoring` request, sent only by the
  Start buttons. The host starts no timer or background task besides the
  bounded restart delay (1/2/4 s). No automatic path was found.
- **Reproduction attempts: 0 of 19.** The exact original script from `b8100aa`
  replayed 5 times under a process watcher (PID, parent, command line); the
  crash-loop sequence alone 6 times; 5 + 3 instrumented runs of the fixed
  script. Every run: `--serve`, three `--serve --resume`, give-up, nothing
  afterwards. (The script's own "duplicate sidecar" race *did* reproduce, 3 of
  5 replays.)
- **Evidence in that run's own database.** `first_run_completed` was written
  at 12:47:53.7, 2.1 s after launch. Only activating a button of the first-run
  dialog writes it (`complete_first_run`); nothing in the script or the app
  does so by itself, and it is *not* set in the 20 other untouched runs. So
  **real input events reached the test window during that run**. The same
  happened in 3 of the 5 replays later the same day (at +0.4 s, +2.9 s, +3.1 s),
  while the desktop was in interactive use (foreground windows observed during
  the tests: a browser and Task Manager).
- **Mechanism.** WattScope takes the foreground when it is launched and again
  when a second launch shows the window (the script does both), with the
  dialog's primary button focused: a keystroke or click meant for another
  application lands on it. With the first-run dialog gone, the Retry button
  that appears on give-up is live.
- **Retry reproduces the signature exactly** (verified in Phase 5): one fresh
  `--serve`, one new session, one sidecar.

**Conclusion.** Most probably outside input reached the focus-taking test
window (proven for the first-run button in that run; not proven for the Retry
activation itself, which was not logged). No lifecycle defect was
demonstrated and no automatic path exists in the code, so production code was
not changed. The lifecycle script now ends with a check that no session exists
other than the two it starts, so such contamination fails the run visibly:
**25/25 on three consecutive runs of the final build.** Recommendation: run
it on a desktop that is not otherwise in use.

## Phase 6 -- Administrator Complete monitoring (manual, run by the user) -- PASSED

2026-10-02, the packaged `desktop/src-tauri/target/release/wattscope.exe`
launched by the user from Explorer with *Run as administrator* (UAC prompt
shown and confirmed by the user; nothing was elevated automatically), against
the **normal** database -- the intended production behaviour. Nothing
destructive was done to it; it gained session #20 and the first-run flag.
Evidence: the user's two screenshots and tray report, a read-only process
check while it ran, and the database / CLI afterwards.

| Requirement | Observed |
|---|---|
| UAC user initiated | yes (user's launch and confirmation) |
| Exactly one host, one owned sidecar | 1 `wattscope.exe`, 1 `wattscope-probe.exe` whose parent is that host; session row `process_elevated = 1` |
| CPU package available (i9-14900KF) | 44.4 W MEASURED on the dashboard; LibreHardwareMonitor "CPU Package"; 714 raw samples, none null, none non-OK, minimum 22.3 W -- no missing CPU reading and none treated as zero |
| GPU board available through NVML | 43.2 W MEASURED; Nvml "GPU Board Power"; all samples OK |
| Monitoring state Complete | "Complete monitoring" + COMPLETE badge; Sensors card Complete |
| System power labelled estimated | "Estimated system power 152.9 W", badge ESTIMATED, no `>=`; equals (44.4 + 43.2 + 50) / 0.9 |
| Session energy Complete | Reports: Complete energy 0.033 kWh, Partial `--`, Complete (at 00:07:10); CPU 0.008 kWh and GPU 0.016 kWh both covering the whole time |
| Cost when a price covers the interval | dashboard EUR 0.0049 at 14.0 Wh (= 14.0 Wh x 0.35 EUR/kWh, labelled approximate); Reports EUR 0.01 |
| Tray menu (first time verified by eye) | "Monitoring: complete", "Estimated power: 157.6 W", "Session: 0.045 kWh, 0.0157 EUR" (= 0.045 x 0.35), Start disabled, Stop enabled |
| Clean tray -> Exit | icon and window gone at once; afterwards 0 host, 0 sidecar, 0 WebView2 processes; no `-wal`/`-shm` left |
| Session persisted and ended cleanly | #20 `Stopped` ("Stopped (--serve exiting)"), 16:00:49 -> 16:12:43, 0 gaps, 0 Active sessions, 20 sessions, integrity ok |
| CLI summary agrees with the UI | monitored 00:11:52; CPU 12.293 Wh and GPU 22.950 Wh at 100 % coverage; "ESTIMATED wall energy (not measured)" 50.162 Wh, Complete; cost 0.02 EUR (unrounded 0.01755666 = 50.162 Wh x 0.35 EUR/kWh); same assumptions (50 W, 90 %) |

Notes: the session-energy card carries the badge ESTIMATED (completeness is
stated on the status and Sensors cards). A tray Exit records the end reason
"Stopped (--serve exiting)"; M004's session #18 says "Stopped by user" --
wording only, both are clean stops. **Not covered here:** a second
(unelevated) launch while the elevated instance runs (checklist B4), and a
Complete *gaming* session (B2).

## Phase 7 -- real Warzone, unelevated (manual, run by the user) -- PASSED

2026-10-02, the same packaged `wattscope.exe`, launched by the user from
Explorer **without** elevation, against the normal database. The user started
Battle.net and Call of Duty by hand; WattScope launched nothing and no
production code was changed. Evidence: the user's reports and screenshots, the
database through the read-only CLI (`--gaming`, `--sessions`, `--summary`,
`--db-info`; before and after on scratch copies), and a validation-only
process logger that read what the detector reads (a Toolhelp snapshot every
2 s plus the image path once per new process through
`PROCESS_QUERY_LIMITED_INFORMATION`; no command lines, memory, modules or
windows).

| Requirement | Observed |
|---|---|
| One host, one owned sidecar, unelevated | `wattscope.exe` started by `explorer.exe`, one `wattscope-probe.exe` child; monitoring session #21 Partial (CPU unavailable, GPU measured) |
| Battle.net alone is not a game | `Battle.net.exe` (`C:\Program Files (x86)\Battle.net\`) with six child processes and `Agent.exe` open alone 16:47:40 -> 16:53:50 (6 min); no gaming session, no candidate reported. Spotify, Photos, Snipping Tool and Edge started meanwhile and were not detected |
| **Real executable identity** | **`cod.exe`**, `E:\GAMES\Battlenet\Call of Duty\_retail_\cod.exe`; parent chain `Battle.net.exe` -> `bootstrapper.exe` (same folder) -> `cod.exe` |
| Helpers do not become games | `bootstrapper.exe`, `bootstrapperCrashHandler.exe`, `codCrashHandler.exe`, `CODBrokerService.exe` (a Windows service; path not readable unelevated), `dxdiag.exe`, about 20 short-lived `nvngx_update.exe` children of `cod.exe`: no session from any of them |
| Automatic detection, no mapping | detection source `KnownCatalog` ("Call of Duty"); `game_mapping` empty before and after |
| Detection latency | process first logged 16:53:52 (2 s resolution); session dated from first sight, 16:53:57 -- within the 5 s poll interval. The LIVE card was seen by the user; the moment of confirmation (20 s delay) was not timed |
| Exactly one gaming session | #1 only, while live, in the tray and after the end; monitoring stayed the single session #21 with 0 gaps |
| Partial semantics | energy `>= 0.0943 kWh` (lower bound), system `>= 163.2 W`, CPU average and peak missing (`--` / `n/a`, never 0), cost withheld (`WithheldPartial`; UI "Not shown: the estimate is partial"), lower-bound cost `>= EUR 0.03` labelled separately |
| GPU load visible | average 58.5 W at the menu, 110.3 W after 26 min including play, peak 193.1 W (RTX 5070 Ti, NVML) |
| Tray / background | window closed to the tray for the play period: same host and sidecar PIDs, sampling uninterrupted; tray menu "Monitoring: partial (CPU power unavailable)", "Session: >= 0.167 kWh, cost n/a", Start disabled, Stop enabled |
| Game close | `cod.exe` gone 17:28:31-33 (logger); session end stored as 17:28:36 (first missed poll); still Active at 17:29:05, `Ended: ProcessExited` by 17:29:17 -- the 30 s grace period is not counted as play time. Battle.net stayed open and created nothing |
| Stored session | 16:53:57 -> 17:28:36, duration 00:34:39 = monitored 00:34:39, not monitored 0, 0 gaps; the Gaming history row, detail panel and per-game table show the same values as the CLI |
| Arithmetic | (96.9 W GPU + 50 W) / 0.9 = 163.2 W; 163.2 W x 2079 s = 0.0943 kWh. Session #21: (120.598 Wh GPU + 50 W x 01:06:16) / 0.9 = 195.356 Wh, as stored |
| Clean tray -> Exit | Exit clicked 17:33:00; #21 `Stopped` ("Stopped (--serve exiting)") at 17:33:02; host, sidecar and all six WebView2 processes gone by 17:33:04; no `-wal`/`-shm` left |
| Database afterwards | 21 sessions, 0 Active, 1 gaming session, 0 mappings, schema 4, integrity ok |

Findings (none blocking, no code changed):

- While a gaming session is **running**, the CLI showed duration 8 s longer
  than monitored (30 s during the grace period). The stored session has no
  difference, so no monitored time was lost; the running-session path was not
  traced further.
- The tray line "Estimated power: 118.5 W" carries no `>=` while monitoring is
  Partial; the status line above it says "partial" and the session line has
  `>=`. Wording only (checklist Known issues 7).
- The user gave the quit time as 17:28:40; the process was already gone at
  17:28:33 by the system clock. Taken as a reading difference of a few seconds.

**Not covered here:** an elevated (Complete) gaming session (B2); relaunching
the game inside the grace period; the heuristic layers (launcher child /
library folder) on a real game -- `cod.exe` was matched by the catalog; a
second real game (A3); sleep during a game (A10); FPS impact (A5, Phase 8).
The game ran for 35 minutes with WattScope open and the user reported no
objection from the game; that is one unelevated observation, **not** an
anti-cheat compatibility claim.

## Performance audit -- scoped sensor sampling (S1)

Phase 8 (the manual FPS benchmark) was cancelled by the user; checklist A5
stays UNVERIFIED. A read-only audit instead found one measured inefficiency:
every tick refreshed all LibreHardwareMonitor hardware and returned 158
readings, of which monitoring uses one (CPU Package); the LibreHardwareMonitor
NVIDIA GPU node alone took ~63 ms per tick although GPU board power comes
from NVML. **Change:** after discovery, `--serve` and `--record` call
`PowerSampleBuilder.LimitSamplingToBindings`, and a provider implementing
`IScopedSensorProvider` (LibreHardwareMonitor) then refreshes only the nodes
that own a bound sensor (and their parents) and returns only those sensors.
Derived from the bindings alone -- no vendor is special-cased, a GPU bound to
LibreHardwareMonitor keeps being read. Discovery, `--discover` and `--monitor`
are unfiltered. Interval, timestamps, gap detection, arithmetic, completeness
and persistence are untouched.

Measured on this machine, unelevated, sidecar alone (`--serve`) on scratch
databases, published single-file binaries of `b51af36` and of this change:

| | Before | After |
|---|---|---|
| LibreHardwareMonitor readings per tick | 158 | 1 |
| Provider call (harness, mean of 60): wall / CPU / allocated | 79.2 ms / 12.0 ms / 101 KiB | 12.2 ms / 0.5 ms / 3.7 KiB |
| Sidecar `readAvgMs` / `readMaxMs` (120 s, GPU idle) | 78.3 / 160.7 | 5.5 / 55.7 |
| Sidecar CPU, % of one core: GPU idle (120 s) | 1.44 | 0.39 |
| Sidecar CPU, % of one core: GPU under load (2 x 180 s each) | 2.31, 2.48 | 0.64, 0.63 |
| Sidecar private memory | 49-52 MiB | 39-41 MiB |
| Ticks / gaps (each run) | 1.000 Hz / 0 | 1.000 Hz / 0 |

GPU (NVML) valid in every tick and equal to `nvidia-smi` read at the same
moment; CPU `ImplausibleZero` in every tick, as before (unelevated);
`--discover` inventory identical (159 sensors, same IDs, names, order and
selections). The GPU load during the 180 s runs was whatever the machine was
doing, not a controlled workload. No FPS claim follows from these numbers.

**Elevated smoke test (user-run, 2026-10-02, rebuilt release exe, normal
database, no game):** `wattscope.exe` launched with *Run as administrator*,
about 5.5 minutes of monitoring, then tray Exit without pressing Stop. Session
#22 (`process_elevated = 1`): 334 raw samples over 333.0 s = 1.0000 Hz (interval
983-1016 ms, none above 1.5 s, no duplicates), 0 gaps, CPU Package (LibreHardwareMonitor,
`/intelcpu/0/power/0`) valid in all 334 samples (26.6-204.3 W, 334 distinct
values: not stale) and GPU Board Power (NVML) valid in all 334 (39.2-53.6 W);
no partial energy. Stored complete wall estimate 51,630 J equals an independent
recomputation from the raw samples, (CPU + GPU + 50 W x t) / 0.9, to 0.02 J;
CLI cost 0.0050196 EUR = 14.342 Wh x 0.35 EUR/kWh. Ended `Stopped (--serve
exiting)`, 0 active sessions, integrity ok, no `-wal`/`-shm`, no WattScope
process left. Sessions 1-21 and every other table are byte-for-byte unchanged
apart from the rows of session #22. **Not observed in that run:** the
sidecar's own `readAvgMs`/`readMaxMs` and the per-tick refresh counts (the
process logger was not running during it); that the GPU node is not refreshed
is shown by the unit tests, the unelevated timing runs above and the code, not
by this run. The packaged installers contain the change.

## Phase 9 -- production installer and installation lifecycle -- PASSED

### Distribution decision: MSI only for v0.1.0

Until this phase `bundle.targets` was `"all"`, so every build produced an MSI
and an NSIS setup (the sizes recorded in earlier sections are of those
builds). A read-only audit of the generated installer sources found:

- **The NSIS setup installs, by default, into `%LOCALAPPDATA%\WattScope`** --
  the folder that holds `wattscope.db`. Tauri has no configuration key for the
  NSIS default folder (only a custom `.nsi` template). The setup was never
  installed, so its behaviour there is read from the script, not observed.
- The NSIS uninstaller offers a generic **"Delete the application data"**
  checkbox that removes `%LOCALAPPDATA%\com.wattscope.desktop` (the WebView2
  profile) and never the WattScope history -- misleading in both directions.
- MSI and NSIS are alternative formats, not meant to be installed side by
  side: the MSI takes its folder from the registry key the NSIS setup writes
  and registers a second product; the NSIS setup removes an MSI install first.

Decision (user, 2026-10-02): **v0.1.0 distributes the MSI only**
(`desktop/src-tauri/tauri.conf.json`, `"targets": ["msi"]`; no other change).
The MSI keeps the binaries in `C:\Program Files\WattScope` (not writable
without elevation, which matters because WattScope may be launched elevated)
and the per-user history in `%LOCALAPPDATA%\WattScope`; the database path is
unchanged. Supporting both formats would need custom installer templates and
adds lifecycle ambiguity. NSIS may be reconsidered if installing without
administrator rights becomes a requirement.

### Build and static inspection (2026-10-02) -- PASSED

Built from `5ca97f7` plus the one-line setting above, with the documented
procedure (`scripts/publish-probe.ps1`, then `npm run tauri build`); both
succeeded and the build reported one bundle.

| | |
|---|---|
| File | `WattScope_0.1.0_x64_en-US.msi`, 37,924,864 bytes (36.17 MiB) |
| SHA-256 | `C0695D88D29A18746B2B5FF7EC989892D0A843F7D4055013B36321A1EA7F7526` |
| Product / version / manufacturer | WattScope / 0.1.0 / `wattscope`; language 1033 |
| Architecture, scope | x64; per machine (`ALLUSERS=1`), needs elevation |
| ProductCode (new on every build) | `{6EBA3712-4EDA-4590-A6FA-CC208792CEB9}` |
| UpgradeCode (Tauri default, derived from the product name) | `{F6A26AA7-10F8-5652-890E-BB809C7121AD}` |
| Install folder | `C:\Program Files\WattScope` (a previous location in `HKCU\Software\wattscope\WattScope` would be reused; the key does not exist on this machine) |
| Shortcuts | Start menu `WattScope\WattScope` and a desktop shortcut, both all-users; "Uninstall WattScope" in the install folder |
| Upgrade | major upgrade of any version with the same UpgradeCode, downgrades allowed; no Repair/Modify in Add/Remove Programs |
| Not present | service, scheduled task, Run entry, PATH/environment change, file association, launch condition |
| WebView2 | if no runtime is registered, the installer downloads Microsoft's bootstrapper (needs internet) |
| Signature | **unsigned** (MSI, host and sidecar) |

Administrative extraction (`msiexec /a` into a scratch folder; nothing was
registered) gave exactly two files:

| File | Bytes | SHA-256 | Notes |
|---|---|---|---|
| `wattscope.exe` | 4,755,456 | `5D6DE348...714DCBCB` | x64 GUI, version 0.1.0; differs from `target/release/wattscope.exe` only in the three bytes of Tauri's bundle-type marker (`MSI` in the package, `UNK` in the build folder) |
| `wattscope-probe.exe` | 82,139,068 | `F0B26CC8...25E115AF` | x64 console, product version `1.0.0+5ca97f7ccdaaf4f86d38fab5050c5ffb3549c92e`; byte-identical to the sidecar published in this build (the previous one, stamped `b51af36`, was `1E4C945C...`) |

Production path: the frontend is embedded in the host (the asset names of this
build's `dist/` are in the executable), the `tauri` crate was compiled with
`custom_protocol` (`dev=false`), `dist/` has no `localhost:1420`, mock marker
or source map, and nothing listened on port 1420. Actually running the
installed app without development tooling belongs to the installation steps
below.

Before any installer was built or run, the real data was backed up to
`C:\WattScopeBackup\phase9-20261002-212546\` (291 files, hashes verified;
database SHA-256 `474C6BEA...1CA14146`, 282,624 bytes). Baseline read from a
copy: schema 4, `integrity_check` ok, no foreign-key violation, 22 monitoring
sessions (none Active), 1 gaming session, 0 game mappings, 2 price rows
(0.35 EUR), `first_run_completed` set.

Noted, not blocking: the sidecar's MSI component GUID is regenerated on every
build (harmless with a major upgrade that removes the old product first); the
manufacturer is shown as lowercase `wattscope`; the installer is English only.

### Installation lifecycle (2026-10-02/03, manual steps by the user) -- PASSED

That exact MSI (never rebuilt) was installed, used, uninstalled, reinstalled
and uninstalled again on the development machine, one manual action at a
time, against the **normal** database. After every action the state was
inspected read-only; the database was only ever read from scratch copies.

| Step | Observed |
|---|---|
| Install | UAC "unknown publisher" (unsigned); success. One product registered per machine; `C:\Program Files\WattScope` holds exactly `wattscope.exe`, `wattscope-probe.exe` (hashes identical to the extracted payload) and "Uninstall WattScope"; users have read/execute only. Start menu `WattScope\WattScope` and a Public Desktop shortcut point at the installed host. Add/Remove Programs: WattScope 0.1.0, `wattscope`, uninstall only. No service, task, Run entry or startup item. The installation itself did not touch the database |
| Finish page | "Launch WattScope" is ticked by default and started the app, **unelevated**, although the installation was elevated |
| First launch | One host, one sidecar (`--serve`), both from Program Files, using `%LOCALAPPDATA%\WattScope\wattscope.db` and the existing WebView2 profile; no other database or data folder appeared; no dev server involved |
| Stop, then Start | Session #23 (19 s) ended `Stopped by user`; the sidecar stays alive, idle, by design. Start created exactly one new session, #24, in the same sidecar |
| Monitoring (#24, unelevated) | 1,586 samples in 1,585.0 s = 1.0000 Hz, intervals 978-1,022 ms, 0 gaps. GPU (NVML) valid in every sample; CPU `ImplausibleZero` in every sample, stored as null, never 0. Partial 114.100 Wh = (GPU 290,433 J + 50 W x 1,585.0 s) / 0.9, equal to a recomputation from the raw samples to 0.6 J; Complete energy 0; cost withheld. The dashboard values the user read match the stored samples |
| Close to tray / Open | Window hidden, tray icon stays, same host, sidecar and session, sampling uninterrupted; *Open WattScope* showed the same window and session |
| Tray -> Exit while monitoring | #24 `Stopped (--serve exiting)`; host, sidecar and all six WebView2 processes gone; WAL checkpointed and removed; integrity ok |
| Same MSI started again | Maintenance dialog with Change and Repair disabled and only **Remove** enabled; cancelled, nothing changed. **A same-version reinstall or repair is not offered by this installer** and was therefore not tested |
| Uninstall (Settings, app not running) | Product registration, Add/Remove entry, Program Files folder, both shortcuts, Start-menu folder and `HKCU\Software\wattscope` removed; no data-removal option exists. **`%LOCALAPPDATA%\WattScope\wattscope.db` byte-identical** (SHA-256 unchanged), and the WebView2 profile `%LOCALAPPDATA%\com.wattscope.desktop` also kept. Nothing else in Program Files, the Public Desktop, the Start menu or `%LOCALAPPDATA%` changed |
| Reinstall of the same MSI | Same registration, payload hashes and shortcuts as the first install; database still byte-identical |
| Launch after reinstall | All 24 earlier sessions, the Call of Duty gaming session, prices and settings shown in the UI and unchanged row by row; no migration, no duplicate database; a normal new session #25 at 1 Hz |
| Uninstall **while running** | Windows Installer listed "WattScope" and "WattScope.Probe" as in use; with "Automatically close applications..." the Restart Manager closed both. #25 ended **cleanly** (`Stopped (--serve exiting)`, WAL checkpointed, 0 gaps), no process was left, nothing was restarted afterwards, no reboot was requested and no pending file operation names WattScope. Database intact with 25 sessions |

Final database: schema 4, `integrity_check` ok, no foreign-key violation, 25
sessions (none Active), every row that existed before Phase 9 unchanged; the
only additions are sessions #23-#25. Both safety backups
(`C:\WattScopeBackup\phase9-20261002-212546`,
`...\phase9-preuninstall-20261002-230731`) verify against their hash lists.
WattScope is currently **not installed** on the development machine.

Observations (no code or configuration changed for them):

- One sample of session #25 arrived 0.58 s late (1,582 ms, then 424 ms) while
  the machine was busy with other work; 593 of 595 intervals are within
  50 ms of 1 s. It is far below the 5 s gap limit, so no gap was recorded.
- During the launch after the reinstall, the WebView2 network process of
  WattScope held one HTTPS connection to an address in a Microsoft range. The
  page's content-security policy only allows the app's own content, and no
  such connection existed in three checks of the first run. What it was for
  was **not determined**; it is recorded as an observation of the WebView2
  runtime, not as evidence of anything WattScope sends.
- The sidecar's private memory was 39 MiB idle and about 50 MiB after 30
  minutes of monitoring, flat over a one-minute window (the long soak, R1,
  remains the place to judge growth).

Known non-blockers: the MSI and both executables are **unsigned** (UAC shows
"unknown publisher"); the Finish page launches the app by default; the same
MSI offers uninstall only; the manufacturer is shown as lowercase
`wattscope`; the installer is English only; the UpgradeCode is Tauri's
default derived from the product name and is not pinned in the configuration.

**Still UNVERIFIED:** upgrading from one version to a newer one (no second
version exists; do it before the first update is distributed); SmartScreen
on a copy downloaded from the internet (the tested file had no such mark); a
machine without the WebView2 runtime or without internet; a standard
(non-administrator) user and a second Windows account; an elevated launch of
the *installed* copy (elevated runs were validated on the same build before
packaging, Phase 6 and the S1 smoke test).

## Phase 3 -- long-session stability (soak)

`desktop/scripts/soak.ps1` (new, reproducible): packaged app, scratch
database, dashboard visible, 1 s sampling, one sample of every process group
per minute, trends by least squares after a 10-minute warm-up. **110.2
minutes** were run; a multi-hour run is still **pending** (`soak.ps1 -Minutes
240`, checklist F3).

| After warm-up (100 samples) | First | Last | Min | Max | Slope / hour |
|---|---|---|---|---|---|
| Host private MiB | 6.95 | 8.60 | 6.89 | 8.66 | +0.48 |
| Host handles / threads | 357 / 42 | 357 / 40 | | 359 / 42 | 0 |
| WebView2 processes | 7 | 7 | 7 | 7 | 0 |
| WebView2 tree private MiB | 189.3 | 203.3 | 188.7 | 204.9 | +5.8 |
| WebView2 tree working set MiB | 379.0 | 348.7 | 340.5 | 385.4 | -22.5 |
| WebView2 tree handles | 3303 | 3366 | 3288 | 3381 | +51 |
| Sidecar private MiB | 56.1 | 59.4 | 55.1 | 59.5 | +1.7 |
| Sidecar working set MiB | 106.9 | 86.0 | 83.1 | 109.3 | -15.7 |
| Sidecar handles / threads | 492 / 14 | 489 / 11 | 480 / 11 | 510 / 14 | 0 |
| Total processes | 9 | 9 | 9 | 9 | 0 |
| Database file KiB | 4 | 252 | | | +153 |
| WAL KiB | 1203 | 4035.5 | | 4035.5 | plateau from minute ~35 |

- **Page (read every 5 min through the DevTools port, after a forced GC):**
  JS heap 2.5-2.9 MiB with no trend, **DOM nodes 236 and event listeners 151
  at every one of 22 readings**, one document. No unbounded frontend history.
- **Sampling:** 6,300 raw samples in 6,300 s (1.000 /s), 0 gaps, one session.
- **Provider read latency** (sidecar's own statistics, 6,316 samples): average
  79.8 ms, maximum 426 ms. **Database flush:** 597 flushes, 0 failures,
  average 0.58 ms, maximum 5.9 ms.
- **CPU** (average after warm-up, % of one core): host 0.05, WebView2 0.64,
  sidecar 1.53.
- **Database growth:** ~38 bytes per raw sample (252 KiB for 6,610 samples);
  the WAL stops at SQLite's auto-checkpoint size (~4 MiB) and stays there.
  Integrity ok; the sidecar exited by itself when the host was ended.
- **Reading.** No process, handle or thread leak; no frontend growth. Working
  sets fall. Private memory rises slowly: the WebView2 tree stepped from ~194
  to ~200 MiB around minute 55 and then stayed at 200-203 MiB (a step, not a
  ramp; the JS heap did not move), and the sidecar gained ~3 MiB in 100
  minutes, which is within what the .NET GC keeps before a gen-2 collection.
  **110 minutes cannot rule out a slow leak of that size**; that is exactly
  what the pending multi-hour run is for.
- Code review of long-lived collections found none unbounded (process and
  detection caches are rebuilt to the live processes each poll; pending
  samples are flushed every 10 s; chart history is capped at 300 points).

## Phase 4 -- real-database safety rehearsal (on copies only)

The real database was copied by file copy (the copies' SHA-256 equals the
original's) and never opened. 43 checks through the release sidecar's own
protocol -- the requests the Settings and Reports pages send -- plus an
independent SQLite reader:

| | Result |
|---|---|
| Copy A (unmodified): opens at schema 4 without migrating, no backup created; integrity ok; 19 sessions, 1,589 raw samples, 20 energy totals, 2 prices | pass |
| Reports on the copy: session #1 (elevated, Complete), #18 (sleep test, Partial), a day, both months, price history | pass |
| Deletion guard: preview "older than 30 days" finds 0 (every real session is newer); an age under 30 days, a missing confirmation and a wrong count are all refused; a confirmed delete of 0 deletes nothing; every row identical afterwards | pass |
| Retention 7 -> 1 day + maintenance: exactly the 300 raw samples older than one day were pruned (session #1's); sessions, energy totals, price splits, gaps, prices identical to an untouched copy (per-table comparison); integrity ok | pass |
| Copy B (sessions #1-#12 moved 60 days back **on the copy** so they become eligible): preview = 12 sessions, 12 energy totals, 1 gap, exactly what an independent count gives; preview deletes nothing | pass |
| A stale confirmation (11) is refused and deletes nothing; the confirmed delete (12) removes exactly the previewed scope; no row of a deleted session is left in any table | pass |
| Sessions #13-#19 with all dependent rows and both prices byte-identical; the report for #18 identical to the one from the unmodified copy; integrity and foreign keys ok | pass |
| The original: size, modification time and SHA-256 unchanged | pass |

One of the 43 checks failed as first written: it compared `app_setting` too,
which is where the new retention value is stored. The per-table comparison
above replaced it. The same deletion was then repeated through the **real UI**
(Phase 5): preview "6 sessions, 5 energy totals", cancel by Enter and by
Escape delete nothing, confirm removes exactly that; sessions #1 and #8-#19
identical to the untouched copy.

## Phase 5 -- production UI and accessibility (real WebView2)

The **production `wattscope.exe`** was driven in its real WebView2 (Edge
154.0.4258.48) through the WebView2 remote-debugging port -- an environment
variable on launch, no change to the app -- with a scratch database (empty, or
a copy of the real one) and a scratch WebView2 profile, so the real language
preference was never touched. This replaces the M006 headless-Edge mock
screenshots. About 850 programmatic checks, plus screenshots read by eye.

| Area | Result |
|---|---|
| Layout: 7 views (Dashboard, Gaming, Reports x4, Settings) x EN/DE/FA x 880x600, 1120x740, 1600x1000, maximized (real window resizes) = 84 combinations, plus scrolled Settings/Reports | no horizontal overflow, no truncated text, no raw i18n key / `undefined` / `NaN`, text contrast >= 4.5:1 everywhere |
| Persian / RTL | `dir=rtl`, sidebar mirrored; no Persian digits (Latin by design); all numeric values, times and dates render left-to-right; units Latin; no untranslated English sentences |
| First run (fresh database) | modal `<dialog>`, labelled, fits at 880x600 in all three languages; focus on the primary action; Tab trapped; Escape hides it for the session only; completion persisted |
| Empty states | Gaming and all four Reports tabs on a fresh database: explained, never `0` |
| Keyboard | every control on every page reachable by Tab with a visible focus ring and scrolled into view; navigation by Enter/Space with `aria-current`; tab strips with arrows/Home/End (mirrored in RTL), one tab stop |
| Dialogs | past-price and delete-history: modal, focus starts on Cancel, Tab trapped, Escape/Enter-on-Cancel change nothing, fit at 880x600 |
| Settings forms | 15 invalid inputs rejected with `aria-invalid` and a linked error (numeric fields inline, the price on submit); nothing sent to the backend; save round trip; German decimal comma accepted |
| Honesty rules | unelevated: Partial with `>=`, CPU `--`, cost withheld; Complete and Partial never merged; a Partial lower-bound cost only where labelled as such |
| Gaming with content | a manual session (stand-in name): live card Partial, no cost; ended by keyboard; one gaming session, monitoring session unaffected; listed under Today |
| Error states | sidecar killed: "restarting" notice (role=status), automatic `--resume`, one Active session; crash loop: localized failure (role=alert) with Retry in EN/DE/FA; Reports/Gaming/Settings show a readable message, no stack trace; Retry restores one sidecar and one new session; integrity ok after 5 injected crashes |
| Accessibility tree | every interactive control named; one `main`, named navigation landmarks; one `h1`, no skipped heading level; chart canvas has a text alternative; status glyphs hidden from assistive technology (the word carries the meaning); per-second readings are not in a live region |
| High DPI | device pixel ratio 1.25 / 1.5 / 2: chart canvas backed at the display density, no overflow |
| Reduced motion | transitions effectively off |
| High contrast (forced colours) | **defect found and fixed**: the open page and the selected report tab had no visible mark; they now use the system selection colours; re-verified |

Limits of this pass, stated plainly: **no real screen reader (NVDA/Narrator)
was run**; Windows high-contrast themes and 125/150 % scaling were *emulated*
in the real engine, the Windows settings were not switched; the native tray
menu cannot be driven this way; a native Persian reader has not reviewed the
wording. Two checks first failed for reasons in the check itself (page focus
lost after a debugger-triggered reload; regular expressions mangled in a
template string) and were re-run correctly; they are not counted as findings.

## Phase 2 -- the timing-sensitive sampling test

`SamplingEngineTests.AfterAStall_DoesNotBurstCatchUpTicks` (20 ms interval, a
200 ms stall, "every tick spacing >= 10 ms") failed intermittently under load.

- **Reproduced**, not re-run until green: 2 of 12 runs failed while a release
  build was loading the CPU, each with a *single* short spacing (7.2 ms, 4.0 ms).
- **Cause: the test, not the engine.** The engine keeps a fixed schedule (no
  drift) and re-anchors only when a tick is late by *more than one interval*.
  A tick late by less than that -- ordinary `Task.Delay` jitter on Windows'
  ~15.6 ms timer, which is most of a 20 ms interval -- is legitimately followed
  by one tick that comes sooner, to get back on schedule. That is one short
  spacing, never a burst, and the wall-clock assertion could not tell the two
  apart. At the production interval (1 s) the same jitter is ~1.5 % of an
  interval.
- **No production scheduling defect.** Energy integration uses the measured
  time between samples, so a single closer sample cannot create energy.
- **Fix (test architecture; production semantics unchanged):** `SamplingEngine`
  takes an optional `TimeProvider` (default `TimeProvider.System`, i.e. the
  same `Stopwatch` counter and `Task.Delay` timers as before; the convention
  `EnergyMonitor` and `GamingSessionTracker` already use). The tests run on a
  virtual clock (`VirtualClock`) and assert **exact** tick times.
- **The assertion was strengthened, not weakened:** after a stall every spacing
  must be a full interval (was: half), the tick times are checked exactly
  (`0, 20, 220, 240, ... 380`), exactly one gap of exactly 180 ms is reported,
  for stalls of 41 ms, 1 s and 60 s; plus new coverage for "late by less than
  one interval keeps the schedule" and "no drift when every read takes 7 ms".
- **The test detects the defect it guards against:** with the re-anchoring line
  removed from the engine (mutation check, then restored) the tests fail
  deterministically with zero-spacing catch-up ticks.
- Result: Core 209 -> **214** tests; 15 consecutive runs of the sampling tests,
  0 failures. The other `SamplingEngineTests` still use the real clock with
  tolerant assertions and were not seen to fail.

# Milestone 006

Scope: finish the *application*, not validate a release. No schema change
(still v4), no new page, no new sensor/estimation logic.

## Product audit (Part A) -- what was wrong, and what was done

Baseline `c78cae6`, clean tree. Gap analysis between what the engine could do,
what the UI exposed, and what was unfinished or misleading:

| Finding | Resolution |
|---|---|
| Reports had no Today/Day view although the protocol supported `day`; month picker used `toISOString().slice(0,7)` = the **UTC** month (wrong for hours around every month boundary) | Tabs Today / Day / Month / Monitoring sessions; local day/month keys (`lib/dates.ts`, tested incl. DST and month-end) |
| A Partial session showed **"0.000 kWh"** in the session list and Reports (only Complete energy was displayed) | Complete and Partial shown side by side, never merged; a missing measurement is `--`, never 0 (`lib/reports.ts`) |
| Dashboard "Session energy" for a Partial session showed Complete-only energy; "no price" and "incomplete estimate" were conflated | `tick.sessionPartialKwh` (new field); headline is `>= complete + partial` labelled PARTIAL; `costVisibility` distinguishes `noPrice` from `withheldPartial`; tray shows `>=` for partial |
| Settings: wrong error strings (PSU/retention reused "invalid price"), hard-coded English ("Database", "Schema", "ms", "unpriced", "n/a"), raw enum text ("(implausibleZero)"), unit-less fields, dead "assumption" badge text | Rebuilt in six groups; every value has unit, range, concise help, inline validation (`lib/validation.ts`); all text localized; reading states localized |
| Electricity price: effective-from was a bare datetime field; a price dated in the past silently re-costed recorded energy | Price UX with append-only history, preview of what a past date affects, explicit confirmation, **and a server-side guard** (`confirmHistorical`) |
| No Retry after the host gave up (the dashboard Start button asked a dead sidecar); no starting/restarting/failed UI; failure reasons were English host strings | Backend state machine surfaced (`backend-state` event, `get_backend_state`, `retry_sidecar`), localized banner with Retry |
| No first-run experience | One short dialog, persisted in the database (`first_run_completed`) |
| Settings re-rendered on every tick (three independent subscriptions + per-page state); chart history was lost on navigation; Gaming polled every 5 s even when its data is already in the tick; polls ran while the window was hidden | One window-wide store, slice selectors, bounded history in the store, visibility-gated polling, Gaming poll removed |
| Errors were `String(e)` panels | `ErrorPanel` (localized title + hint, technical details collapsed), per-kind classification (`lib/errors.ts`); stack traces never shown |
| Mapping list (Gaming) had no header, no edit; history/aggregates had thin empty states | Edit/Remove with accessible names, headers, empty states with a reason |
| Muted text 3.1:1 contrast; no visible focus ring; colour-only status; tooltip not keyboard-reachable | `ink-muted` raised, global `:focus-visible` ring, every status is glyph + word, `Hint` is a real button (Esc dismisses), reduced-motion respected |
| `validate-lifecycle.ps1` / `measure-background.ps1` recorded into the **real** database and stop processes by name | Now use a scratch DB (`_scratch-db.ps1`), print both paths, refuse to run if a WattScope is already running |

Navigation was already the four-page structure the brief prefers
(Dashboard, Gaming, Reports, Settings); no page was added. Diagnostics/About
lives inside Settings (Application group), not on a new page.

## What was built

| Area | Implementation |
|---|---|
| Dashboard | Status card (state + sampling interval + Start/Stop), *Power now* (system / CPU / GPU / other), *This monitoring session* (duration / energy / cost), sensor status, live chart. Badges **MEASURED / ESTIMATED / PARTIAL / UNAVAILABLE / COMPLETE / ASSUMED**: glyph + word, one meaning everywhere |
| Reports | Today (auto-refresh while visible) / Day / Month (+ day-by-day table, Complete and Partial columns) / Monitoring sessions; per period: monitored time, estimated system energy (Complete + Partial separately), CPU/GPU measured energy, cost (priced Complete energy; Partial lower bound and unpriced energy reported on their own), gaps, price-precision notices, the estimation settings used; gaming sessions started that day listed (not summed in) |
| Gaming | Honest empty states, start-monitoring shortcut, live card from the tick stream, history, per-game aggregates, edit/remove mappings, manual-session explanation; Partial/Complete consistent with the rest |
| Settings | Monitoring, Energy estimation (formula + "estimate, not wall measurement" stated once, assumptions badged ASSUMED), Electricity (current price, add price, when it applies, history), Data (raw retention, storage, maintenance, guarded history deletion), Language, Application (background/exit, Administrator note, About + Diagnostics) |
| Sidecar protocol (additive) | `get_app_state`, `complete_first_run`, `get_price_info`, `preview_price`, guarded `set_price`, `get_diagnostics`, `check_integrity`, `run_maintenance`, `preview_history_deletion`, `delete_history`; `get_gaming_sessions` accepts `day`; tick gains `sessionPartialKwh` |
| Host (Rust) | `backend-state` event + `get_backend_state` / `retry_sidecar` / `get_host_info`; exit code 4 (database cannot be opened) is never retried; the pending restart no longer spawns if a sidecar is already alive |
| Database | **No schema change.** Pre-migration backup (`VACUUM INTO`), migration failure type, safe reads of damaged settings, bounded incremental vacuum, PASSIVE checkpoint maintenance, guarded deletion (see `docs/STORAGE.md`) |
| Formatting | `lib/format.ts` central, locale-aware separators/date order, always Latin digits + Gregorian + fixed technical units; machine values never pass through it |
| Dev tooling | `desktop/src/dev/mock.ts` + `vite build --mode mock`: renders the UI in a plain browser on synthetic data (used for the screenshots below). Not in production bundles (verified) |

## Verification (all automated, non-disruptive, scratch databases only)

| Check | Result |
|---|---|
| `dotnet test WattScope.slnx` | 389 passed, 0 failed (new: 21 storage, 21 probe tests) |
| Frontend `npm test` (vitest) | 183 passed, 12 files (new: format, dates, validation, errors, reports, store, diagnostics, first-run, language; locales and monitoring extended) |
| `npm run typecheck` / `npm run build` | clean |
| `cargo test` | 15 passed (was 12: database-error never retried, stable reason codes, user retry resets the budget) |
| `npm run tauri build` | MSI **36.16 MiB**, NSIS **27.08 MiB** built; production bundle contains no mock code |
| `desktop/scripts/validate-lifecycle.ps1` (scratch DB, packaged app) | **24 / 24** on the second run. The first run had 1 failure, "no duplicate sidecars", a one-shot process count taken right after the replacement sidecar appeared; not reproduced (second full run passed; a separate 30 s poll of the process count after a kill showed exactly one sidecar throughout). Treated as a script race, not confirmed as a product defect |
| Visual layout | A *sample* of headless-Edge screenshots of the UI against synthetic data (Dashboard complete/partial/failed at 880x600 to 1120x740, Reports, Gaming, Settings tall, first-run in German, Dashboard in Persian RTL at 880 wide): no overlap or horizontal page overflow after one fix (stat-card labels were truncated at narrow widths; they now wrap). Not every page x size x language combination was captured, and **nothing was verified in the real WebView2**, see the checklist |

### Final test counts

**.NET: 389 passed** (Core 209, Storage 72, Hardware 11, Probe 97), 0 failed;
was 347. Frontend 183 (was 43). Rust 15 (was 12).

## Database and operational findings (Part M)

- **WAL / checkpoint**: left on SQLite's default auto-checkpoint (no evidence
  of a problem; a PASSIVE checkpoint is part of the user-triggered
  maintenance and never blocks the recorder). **No VACUUM** is ever run: the
  database uses `auto_vacuum=INCREMENTAL` and returns free pages in steps of
  2048 pages, so maintenance cannot stall the recorder.
- **Indexes**: hot queries verified by `EXPLAIN QUERY PLAN` tests (energy
  buckets by time and session, gaps, raw samples, gaming by game/start/monitoring
  session) -- all index searches. **No new index was needed.**
- **Report cost, measured** on a synthetic year (35,040 buckets, 10 sessions,
  scratch DB): statistics 2 ms, a day report 12 ms, a month report with daily
  breakdown 9 ms, the 50-session list 36 ms, `quick_check` 5 ms. Nothing was
  optimized because nothing was slow.
- **Integrity**: `quick_check` + foreign-key check on demand (Settings -> Data).
- **Migration failure**: each migration is transactional (verified: a forced
  failure rolls back, `user_version` unchanged, data intact); a consistent
  backup (`wattscope.db.pre-migration-vN.bak`) is made first; the sidecar then
  exits with code 4, which the host never retries, and the UI explains it.
- **Damaged persisted values** (retention, estimation settings, a price row)
  fall back to defaults / are skipped with a log line instead of feeding the
  maths or crashing.
- **Retention**: raw samples (default 7 days, 0..3650) are pruned; 15-minute
  totals, costs, gaps and sessions are kept. New: user-triggered deletion of
  *finished* sessions older than >= 30 days, only after a preview and a
  confirmation naming the exact session count; never the running session,
  prices, settings, mappings or the database file. The database is never
  deleted automatically and uninstalling does not remove it by default.

## Performance and memory (Parts O, P)

- **Renders / IPC**: previously three independent subscription sets (each
  page mounted its own) and Settings and Gaming re-rendered on every tick.
  Now one set of event listeners for the whole window; pages read slices;
  Settings no longer re-renders per tick; Gaming re-renders per tick only in
  its live card; the canvas chart never touches React state.
- **Unbounded state**: none found. The chart history (300 points) is bounded
  and now lives in the store (it used to be lost on navigation). Unit-tested:
  capacity, newest-kept, no break between consecutive ticks, a break (not a
  joined line) across a gap.
- **Timers**: every poll goes through one visibility-gated hook; a hidden
  window polls nothing and refreshes when shown. Gaming's 5 s state poll was
  removed (the tick carries it). Reports poll only Today/this month/session list.
- **Listener leaks**: the store's start/stop is tested to subscribe exactly
  once per event and to unsubscribe everything.
- **Memory, packaged build, scratch DB, unelevated, 1 s sampling, 60 s after a
  15 s settle** (`measure-background.ps1`, single run):

  | | Procs | Working set | Private |
  |---|---|---|---|
  | Host (Tauri) | 1 | 30.6 MiB | 6.8 MiB |
  | WebView2 tree | 7 | 373 MiB | 192 MiB |
  | Sidecar (.NET, LibreHardwareMonitor, 3 SQLite connections) | 1 | 96-103 MiB | 48-54 MiB |
  | **Total, window visible** | 9 | **500 MiB** | **247 MiB** |
  | Total, hidden in the tray | 9 | 504 MiB | 249 MiB |

  M004 reported ~470-475 MiB (working set, same method). Working set counts
  shared pages in every WebView2 process, so it overstates real use; private
  memory is about half. Roughly three quarters of the total is the WebView2
  runtime (browser, GPU, renderer, network and utility processes) -- framework
  overhead WattScope cannot remove; the host is 31 MiB; the sidecar is the
  self-contained .NET runtime plus sensor libraries. The ~25-30 MiB rise over
  M004 is within single-run variation and the M005 detector's footprint. No
  avoidable retention was found in review; **long-run (hours) growth was not
  measured** (checklist F3). Hiding the window did not reduce the total
  (WebView2 stays resident), but its CPU fell from 0.68 % to 0.03 % of a core.
  No FPS impact is claimed.
- Chart CPU, per-tick render counts and a profiler pass in the real WebView2
  were not done (UNVERIFIED).

## Localization, formatting, accessibility (Parts Q, T, U, V)

- A test now scans every source file: **every literal `t("...")` key exists in
  EN, DE and FA**, every dynamic key family is enumerated, **no unused key is
  left**, placeholders match across languages, plural forms (`_one`/`_other`)
  exist everywhere, German/Persian differ from English except identifiers,
  Persian strings are in Persian script, and CPU/GPU/W/kWh stay Latin inside
  Persian text. Terminology is centralised in `terms.*` (monitoring session,
  gaming session, estimated system power, CPU package power, GPU board power,
  partial estimate = "Partial (lower bound)", electricity cost).
- Numbers/dates follow the language (German `1.234,6 W`, `02.10.2026`) with
  Latin digits and Gregorian dates in every language; units never change.
  Persisted values are machine-readable; prices are sent as exact decimal text.
- Accessibility: tab strips with arrow keys (mirrored in RTL), native
  `<dialog>` (focus trap, Esc, focus return; Cancel focused for destructive
  actions), labelled fields with `aria-describedby`/`aria-invalid`, a visible
  focus ring, contrast raised, status by glyph + word, reduced motion.
  **No screen-reader, high-contrast or keyboard-only pass was run on the real
  app** (checklist E4/E5).

## Feature freeze classification (Part Z)

**FEATURE COMPLETE.** Nothing further is to be started in this milestone.

1. **RELEASE BLOCKER** -- none known. (The unverified items below become
   blockers only if they fail.)
2. **PRE-RELEASE VALIDATION** -- everything in `docs/PRE_RELEASE_CHECKLIST.md`
   (real Warzone and a second game, FPS impact, Administrator Complete
   sessions, MSI/NSIS install/upgrade/uninstall, shutdown/restart/logoff,
   real-WebView2 layout and screen-reader pass, long-session growth, copy-of-real-database
   open and deletion rehearsal).
3. **OPTIONAL POST-RELEASE IMPROVEMENT** (not started, not promised):
   Windows auto-start and a least-privilege sensor helper (so Complete CPU
   monitoring does not need the whole app elevated); an in-app restore of a
   `.bak`; export of reports to CSV; a configurable close-to-tray choice;
   Persian-calendar display as an opt-in; a real chart hover/tooltip;
   per-day hourly breakdown in Reports (needs 15-minute buckets exposed);
   Gaming cost per game over time; code-signing of the installers.

## Known limitations (Milestone 006)

- The "Gaming sessions that started on this day" list is a list, not part of
  the day's energy total (it is a subset of it, and Partial/Complete are not
  merged).
- Day/month reports attribute a 15-minute bucket to the local day of its start
  (unchanged); a bucket straddling local midnight in a half-hour-offset zone is
  attributed whole.
- Dates for English use the US order (`10/02/2026`); there is no ISO option.
- Estimation settings changes apply to the *next* monitoring session by design
  (the running session keeps the values it was recorded with); the UI says so.
- ~~`SamplingEngineTests.AfterAStall_DoesNotBurstCatchUpTicks` is
  timing-sensitive.~~ **Resolved in pre-release validation** (see "Pre-release
  validation, Phase 2" at the top of this file): the test now runs on a
  virtual clock and is deterministic; no production scheduling defect.

# Milestone 005

Design, rules, state machine, database, anti-cheat boundary and limitations:
`docs/GAMING.md`.

## What was built

| Area | Implementation |
|---|---|
| Model | `GamingSession` distinct from the monitoring session (FK, 0..n per monitoring session); energy derived from the monitor's verified intervals via `IMonitorObserver` (no second integration, no raw-sample copy) |
| Detection | Passive: toolhelp process snapshot every 5 s while monitoring, one `PROCESS_QUERY_LIMITED_INFORMATION` query per new process (cached), layered identification (user mapping > catalog > launcher child > library folder; version metadata only names). Launchers/browsers/chat/capture tools never games |
| State machine | NoGame / Candidate / Confirmed / EndingGrace, thresholds 20 s / 60 s confirm, 30 s grace, 5 s poll; one authoritative context; deterministic multi-candidate ordering |
| Energy | `GamingLedger` per electricity price + peaks; candidate energy kept from first sight; grace tail dropped or merged; gaps/sleep never integrated; Partial/Complete never merged; cost only when Complete and fully priced; retroactive prices flagged approximate |
| Database | Schema v4 (additive): `game_mapping`, `gaming_session`, `gaming_session_energy`; unique single-active index; crash recovery as `Interrupted` |
| Protocol | `tick.gaming` (small live summary) + `get_gaming_*`, mapping, manual-session and running-programs requests; no Rust host change |
| UI | Gaming page (live, history + detail, per-game aggregates, manage games, manual session) in EN/DE/FA, RTL-safe |
| CLI | `--gaming`, `--game-mark`, `--game-exclude`, `--game-unmark` |
| Env switches | `WATTSCOPE_DB` (database path for a whole process tree; validation/tests), `WATTSCOPE_GAME_DETECTION=off` |
| Packaging | MSI 36.12 MiB, NSIS 27.03 MiB build |

## Verification

| Requirement | How verified | Result |
|---|---|---|
| M001-M004 intact | `dotnet test WattScope.slnx` | **347 passed** (Core 209, Storage 51, Hardware 11, Probe 76), 0 failed, 0 warnings; was 187. Two storage tests had their schema-version assertions (3 -> 4) and the hand-made "rewind to M003" fixture updated for v4 |
| New .NET tests (160) | Detector, identifier, tracker (attribution, partial/complete, gaps, sleep via the real `EnergyMonitor`, grace tail, price change/unpriced/retroactive, manual, ownership, recovery), analytics (weighted aggregates), storage (migration from an M004-shaped DB, round trip, single-active, crash recovery, mappings, env override), probe (controller + real SQLite, sidecar restart mid-game, interval change, protocol, CLI, real poller with scripted processes, the real Windows process source) | pass |
| Frontend | `npm test` 43 passed (was 30); `npm run typecheck` clean; `npm run build` clean | pass |
| Rust | `cargo test` 12 passed (host unchanged) | pass |
| Packages | `npm run tauri build` | MSI + NSIS built |
| **Migration of a real M004 database** | Copy of the real database (schema 3, 18 sessions): M004 build confirmed schema 3; M005 build migrated to 4, 18 sessions kept, integrity ok | **pass** |
| **Real packaged app, stand-in processes** (`desktop/scripts/validate-gaming.ps1`, scratch DB via `WATTSCOPE_DB`) | **29 / 29 checks passed**: notepad + an unmapped tool -> no session; a launcher-named stand-in alone (50 s) -> none; launcher + helper in its own folder -> none; a user-mapped test exe -> one session, named, source UserMapping, started 1.7 s after the process (detected after ~24 s), ended `ProcessExited` 1.6 s after the process vanished (not 30 s later), monitored = duration, **Partial** (unelevated: CPU n/a, GPU 69.4 W avg, system `>=` 132.7 W, no cost); same with the window hidden in the tray; host killed mid-game -> sidecar exits by itself, session closed `MonitoringStopped`; relaunch works; 3 sessions persisted; one Active monitoring session; integrity ok | pass |

## Measured overhead (packaged build, this machine, unelevated, 1 s sampling)

`desktop/scripts/measure-gaming-overhead.ps1`: same binary, A/B on
`WATTSCOPE_GAME_DETECTION`, 60 s per phase after a 15 s settle, 32 logical
CPUs, CPU = % of **one** core, single runs on a busy desktop (indicative;
earlier M004 runs on the same machine varied by about +-0.3 between runs).

| | detection OFF (monitoring only) | detection ON, idle | detection ON, active gaming session |
|---|---|---|---|
| Sidecar CPU, window visible | 0.88 | 1.69 | 1.66 |
| Sidecar CPU, hidden in tray | 0.88 | 1.32 | 1.06 |
| Sidecar working set (hidden) | 99.2 MiB | 102.7 MiB | 104.6 MiB |
| Total CPU, visible / hidden | 1.53 / 1.34 | 2.44 / 1.71 | 2.39 / 1.27 |
| Total working set (hidden) | 477.9 MiB | 479.9 MiB | 480.3 MiB |

Reading: detection adds roughly **0.2-0.8 % of one core** (< 0.03 % of the
machine) and **~3-6 MiB** to the sidecar; the active-session column is not
higher than idle, i.e. the per-sample ledger cost is below the noise. The
"M004 baseline" is **not** a different binary: the real database is now
schema 4 (an M004 build cannot open it and the sidecar's database path cannot
be redirected without the new override), so the A/B uses detection OFF in the
M005 binary as the baseline. For cross-reference, the earlier M004 packaged
measurement on the same machine was 2.21 / 1.22 % total CPU (visible /
hidden) and 469 / 475 MiB. **Gaming frame-rate / FPS impact was NOT measured**
(no controlled benchmark with a real game); no claim of zero FPS impact is made.

## Incident during validation (disclosed)

The first validation attempt assumed `LOCALAPPDATA` redirects the database;
it does not (the path comes from the Windows known-folder API). The packaged
app therefore ran for ~40 s against the **real** database: it migrated it to
schema 4 (non-destructive, integrity ok) and recorded one 37-second monitoring
session, **#19** (`Stopped`). Nothing was deleted or altered. The `WATTSCOPE_DB`
override was added and every later run used it; the real database was
re-checked afterwards (19 sessions, schema 4, integrity ok).

## UNVERIFIED (needs a person / a real game)

- **Call of Duty / Warzone through Battle.net**: since validated unelevated
  on 2026-10-02 ("Phase 7" above: the executable is `cod.exe`, matched by the
  catalog; the real Battle.net alone is not a game). Still no anti-cheat
  compatibility claim.
- **Complete** (elevated) gaming sessions with a cost: covered by unit tests
  only; the real runs were unelevated (Partial).
- Heuristic detection (launcher child / library folder) on real software:
  unit-tested only.
- Real Windows sleep during a game (the sleep path is covered by tests with
  the real `EnergyMonitor`, and by the M004 real-sleep result for monitoring).
- The Gaming page itself was type-checked/built and its logic unit-tested but
  **not visually inspected** in a running window in this session (including
  Persian RTL on this page).
- Installing the MSI/NSIS packages (as in M004).

## Known limitations

See `docs/GAMING.md` section 12 (executable-name identity, small catalog,
heuristics can be wrong, 5 s resolution, whole-machine energy, approximate
retroactive-price cost, ~10 s loss on crash).

# Milestone 004

Architecture: `docs/ARCHITECTURE.md` ("Process model (Milestone 004)",
"Privilege model"). Manual procedures: `docs/MANUAL_VALIDATION.md`.

## Product decision (intentional scope, not an implementation failure)

Automatic Windows startup and a least-privilege privileged sensor
helper/service are intentionally deferred. WattScope currently runs only when
explicitly launched by the user. Complete CPU monitoring on hardware requiring
privileged sensor access may require the user to explicitly launch WattScope
with Administrator privileges. Running the whole app elevated is an accepted
**interim** limitation of this development-stage version, only when the user
chooses it. Nothing for auto-start, a Windows Service, a privileged helper,
privileged IPC or automatic elevation was built, and no gaming analytics.

## Retained from the pre-correction work / removed because of the scope change

There was no uncommitted M004 work when the scope was corrected (`git status`
was clean at `56fd6aa`), so nothing was reverted. The one piece of **committed**
Milestone 003 code that conflicted with the revised scope was removed: the
`restart_sidecar_elevated` command and Settings button (it elevated only the
sidecar and left an untracked second recorder outside the host's control,
which the one-sidecar/one-session rules forbid). `restart_sidecar` was also
removed; changing the interval now continues the same session inside the
sidecar (`set_monitoring_interval`).

## What was built

| Area | Implementation |
|---|---|
| Tray | Native icon with tooltip; menu: state, estimated power, session energy/cost (read-only lines refreshed at most every 5 s), Open, Start/Stop monitoring (enabled by state), Settings, Exit. Left-click opens. Strings in EN/DE/FA (`labels.rs`), following the UI language |
| Close vs Exit | Window X hides to the tray; monitoring continues. Exit (tray, or Settings -> *Exit WattScope*) asks the sidecar to stop, waits up to 5 s, then kills the child the host holds the handle of |
| Single instance | `tauri-plugin-single-instance`; a second launch shows the first window |
| Monitoring lifecycle | `MonitoringController` in the sidecar: idempotent start/stop, independent of the process, window and React; `get_monitoring_state` lets a reopened UI attach (includes the last tick); one run at a time |
| Hidden window cost | The host feeds the tray from every tick but forwards ticks to the webview only while visible; the chart now draws strictly on demand |
| Sidecar lifecycle | One owned child by handle + spawn generation; bounded restart (1/2/4 s back-off, max 3 per 60 s, then give up and report); exit code 3 (lock held) never retried; sidecar exits when its stdin closes (no orphan when the host dies); never kills by name or PID; sidecar retries the recording lock for ~3 s so a transient CLI holder is not mistaken for another recorder |
| Session ownership | Schema v3: `ux_session_single_active` (one Active session per database); `ActiveSessionExistsException`; v3 migration repairs databases that already have several Active rows |
| Suspend/resume | `EnergyMonitor.OnResume` is now a no-op unless a suspend preceded it (duplicate/spurious Resume no longer cuts a second gap); duplicate Suspend already safe; elapsed-time fallback retained and tested |
| Partial-monitoring UX | Dashboard banner: Complete / Partial / Sensor unavailable / Monitoring stopped / Monitor not running (+ Starting) with a one-line reason; **no session cost unless the session estimate is Complete** (the old tick cost showed `0` for a partial session -- fixed); Settings shows Administrator state read-only |
| Localization | New keys in EN/DE/FA; locale parity tests |
| Packaging | MSI 36.07 MiB and NSIS 26.99 MiB build; no service/startup entry added |

## Verification

| Requirement | How verified | Result |
|---|---|---|
| Existing M001-M003 tests intact | `dotnet test WattScope.slnx` | **187 passed** (Core 105, Storage 26, Hardware 11, Probe 45), 0 failed; was 133 at the end of M003. 0 warnings |
| New .NET tests | `WattScope.Probe.Tests` (new, 45): controller start/stop idempotency, 12 concurrent starts -> 1 session, restart -> new session, resume continues same session with a Paused gap, orphan recovery, interval validation, snapshot attach, suspend/resume gap + no energy across sleep, duplicate suspend/resume -> 1 gap, resume without suspend -> no gap, missed-suspend elapsed-time fallback (both clock cases), sampling failure -> idle + closed session, partial session never shows a cost or a zero CPU, request processor: malformed/oversized/BOM/unknown/invalid parameters, start/stop via protocol, interval change keeps the session. `Storage.Tests` (+6): second Active refused, two connections racing -> one, reopen conflict, v3 migration repairs duplicate Actives, interval persistence. `Core.Tests` (+3): repeated suspend/resume, resume without suspend, two separate sleeps | pass |
| Rust unit tests | `cargo test`: restart policy (back-off, bound, window ageing, lock-held never retried, user reset), spawn arguments, tray labels in 3 languages | **12 passed** |
| Frontend | `npm test` (vitest) 30 passed (monitoring state machine, cost withheld, locale key parity/translation/Persian script); `npm run typecheck` clean; `npm run build` clean | pass |
| Tauri build | `npm run tauri build` | MSI + NSIS built |
| **Real packaged-app lifecycle** | `desktop/scripts/validate-lifecycle.ps1` against the final `wattscope.exe`: 24 checks | **24 / 24 passed** -- one host + one sidecar after launch; a second launch exits by itself and changes nothing; **8 simultaneous launches** create no extra host/sidecar/session; window X hides it (window gone) while host and the *same* sidecar keep running and the session record keeps advancing; launching again while hidden shows the existing window and the same single session continues; killing the sidecar -> a new one appears, crashed session recovered, one Active; 4 injected crashes -> restarts stop (bounded), host stays alive; relaunch works; killing the host -> the sidecar exits by itself within seconds and the session is closed `Stopped` (not Active/Interrupted) |
| **Real UI actions** (Windows UI Automation against the live window) | Stop monitoring -> session `Stopped`, banner "Monitoring stopped"; Start monitoring -> a new session, exactly one Active; **Exit WattScope** -> host, sidecar and WebView processes all gone, session `Stopped` | pass |
| **Real Windows sleep/resume (manual, run by the user)** | Session #18, unelevated, put to Windows Sleep and resumed, then stopped via tray -> Exit; result read back with the CLI summary | **VERIFIED / PASSED** -- see "Real sleep/resume result" below |
| Partial monitoring shown honestly (real, unelevated, this machine) | Live accessibility tree of the dashboard: "Partial monitoring", the CPU-unavailable explanation, CPU `--` ("Not elevated"), GPU 42.6 W, system 102.9 W "Partial (lower bound)"; ticks carry `cpuWatts: null`, `sessionCompleteness: partial`, `sessionCostApprox: null` | pass |
| Persian RTL (real window screenshot) | Sidebar on the right, right-aligned text, mirrored controls, numbers/units/dates left-to-right inside Persian text | pass (visual) |

### Bugs found by the real validation (and fixed)

1. **Hidden-window CPU**: with the window hidden, WebView CPU only fell from
   3.85 % to 3.15 % of a core. The chart's `requestAnimationFrame` loop
   re-armed forever even with nothing to draw. Now drawing is strictly on
   demand: visible **0.65 %**, hidden **0.03 %** (see measurements).
2. **Lock hazard**: any CLI call that does crash recovery (`--sessions`)
   briefly holds the recording lock; a sidecar starting at that instant would
   exit with code 3, which the host treats as final. The sidecar now retries
   for ~3 s before concluding another recorder owns the database.
3. **Misleading cost**: the running session cost was computed from
   complete-estimate energy only, so a partial session showed a cost of 0. It
   is now withheld unless the session estimate is Complete.

Two failures in the first runs of the validation script were *test* defects,
not app defects (a helper posted `WM_CLOSE` to the single-instance plugin's own
hidden window too; PowerShell 5.1 `.Count` on a single object) -- fixed in the
script.

## Background performance (packaged build, this machine, unelevated)

`desktop/scripts/measure-background.ps1`, 60 s per phase after a 15 s settle,
32 logical CPUs, 1 s sampling interval. CPU is % of **one** core (the machine
column is that divided by 32). Single runs on a busy desktop; indicative.

| | Before the chart fix | | After the chart fix | |
|---|---|---|---|---|
| | visible | hidden | visible | hidden |
| Host (Tauri) | 0 | 0.05 | 0.08 | 0.05 |
| WebView2 tree (7 procs) | 3.85 | 3.15 | **0.65** | **0.03** |
| Sidecar (.NET) | 1.79 | 1.30 | 1.48 | 1.14 |
| **Total CPU** | 5.64 | 4.50 | **2.21** | **1.22** (0.039 % of the machine) |
| Working set (all 9 procs) | 475.8 MiB | 481.4 MiB | 469.3 MiB | 474.9 MiB |
| Processes | 9 | 9 | 9 | 9 |

Sidecar latency (standalone `--serve`, 44 samples): provider read avg
**86.7 ms**, max 125 ms (LibreHardwareMonitor refresh, unchanged from M002);
database flush avg **0.66 ms**, max 1.7 ms; 0 gaps. Hiding the window does not
reduce memory (the WebView stays alive); it removes the CPU. **Gaming/FPS impact
was not measured** (no controlled game benchmark was run) -- no claim is made.

### Real sleep/resume result (manual, user-run) -- VERIFIED

Run by the user on this machine, unelevated, following
`docs/MANUAL_VALIDATION.md` section 1. The figures below are the user's
reported output of the CLI summary for the session; they were not
re-generated in the documentation-only update that recorded them.

| | Observed |
|---|---|
| Session | **#18**, started 2026-10-01 23:27:31 local |
| Sleep | Windows Sleep, then resumed |
| End | tray -> *Exit WattScope*; session `Active` -> **`Stopped: Stopped by user`** |
| Period | 23:27:31 -> 23:36:56 (9 min 25 s) |
| Monitored time | 00:07:28 |
| Monitoring gaps | **exactly 1**, reason **`Suspend`**, 23:29:31 -> 23:31:28, duration **00:01:57**; the CLI states "no energy recorded" for it |
| CPU package | 0.000 Wh, coverage 0.0 % (expected: deliberately unelevated, `ImplausibleZero`) |
| GPU board | 5.251 Wh, coverage 100.0 % |
| Complete estimate | 0.000 Wh, 0.0 % coverage, completeness **Partial** |
| Partial lower-bound estimate | 12.748 Wh over 00:07:28 |
| Complete-estimate cost | n/a |
| Partial lower-bound cost | 0.00446172 EUR, correctly excluded from the complete cost |

Consistency check: 9:25 period - 1:57 gap = 7:28, exactly the monitored time,
so nothing was integrated across the sleep.

What this verifies:

1. The same session (#18) continued across sleep/resume; no new session was
   created after resume.
2. Exactly one gap was recorded, classified `Suspend` (the precise
   `PowerModeChanged` path, not only the elapsed-time fallback), and its
   duration matched the real sleep interval. No duplicate suspend/resume gap.
3. No energy was integrated across the sleep interval, and monitoring resumed
   after wake with no visible catch-up burst.
4. Partial-monitoring semantics held throughout: CPU 0 W was never integrated
   or shown as real power, the estimate stayed Partial, and the partial
   lower-bound cost was kept out of the complete cost.
5. As a side effect, the **tray -> Exit** menu item was exercised for real and
   cleanly stopped the session (`Stopped by user`) -- it had previously only
   been covered through the equivalent Settings -> Exit button.

This closes the "real sleep/resume" UNVERIFIED item for the PC used, in the
mode it used (the OS-delivered `Suspend` notification was received). It does
not establish behaviour on a Modern Standby machine, or for sleeps whose
notification is missed (that case is covered only by the automated
elapsed-time-fallback tests).
## UNVERIFIED (needs a person at the machine; procedures in `docs/MANUAL_VALIDATION.md`)

- **The rest of the native tray** (icon visibility, tooltip, menu item
  enabled/disabled states and live text, Start/Stop/Open/Settings clicks, menu
  language incl. Persian rendering). Only **tray -> Exit** is verified (by the
  user, in the sleep test above). Everything behind the other items is
  exercised through the equivalent request path used by the UI's Start/Stop
  buttons, but the native menu itself could not be driven automatically.
- **Windows logoff / shutdown / restart.** Not tested. Design relies on
  `RunEvent::Exit` (2 s bound), the sidecar noticing stdin closing, and
  Milestone 002 crash recovery as the final net; whether Windows delivers the
  teardown event to a hidden-in-tray Tauri app was not verified.
- **Launch as Administrator**, and the dashboard showing **Complete**
  monitoring with a cost through the new UI. (Milestone 002 validated complete
  elevated recording through the CLI; the new state machine for "complete" is
  unit-tested; the combination was not run elevated in this session.)
- **MSI / NSIS install, upgrade and uninstall** (the packages build and the
  built exe was validated; installing them was not).
- Real validation sessions #3-#18 now exist in the default database
  (`%LOCALAPPDATA%\WattScope\wattscope.db`) from these runs, including the
  user's manual sleep-test session #18 (several earlier ones are
  `Interrupted` by the deliberate crash/kill tests). History is never deleted
  by uninstall.

## Known limitations (Milestone 004)

1. No auto-start, no privileged helper/service: CPU power needs the user to
   launch WattScope as Administrator on this machine (see decision above).
2. The tick cost on the dashboard is an approximate live figure at the price in
   effect when the run started; the exact, split-priced cost is in Reports.
3. A hidden window keeps its WebView memory (~350 MiB across 7 processes); only
   its CPU is removed. Fully destroying/recreating the webview on hide would
   save memory at the cost of a slower reopen and was not done.
4. The host stops forwarding ticks to a hidden window, so the live chart has a
   visual jump over the hidden period when reopened (data is not lost; the
   database has every sample).
5. No automated test drives the native tray; no automated test of a real OS
   suspend (see UNVERIFIED).
6. `Console.In.ReadLineAsync`'s non-yielding behaviour (found in M003) is
   still handled by running the request loop on its own thread.
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

> **Superseded by Milestone 004:** items 1 and 2 below describe the M003 "restart as administrator" action and the interval-change restart. Both were removed/changed in M004 (see above): the whole app is simply launched as Administrator by the user, and interval changes continue the same session.

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
   unrelated to Milestone 003 and was not introduced by it. *(Made
   deterministic in pre-release validation, Phase 2.)*

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

# Project State

Last updated: 2026-10-02, after Milestone 006 (application completion, UX
polish, feature-complete candidate) on top of Milestone 005.

## Status

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
  harmless stand-in processes on the real packaged app. **No real game has been
  run: the Call of Duty / Warzone validation is UNVERIFIED** until the user
  performs `docs/MANUAL_VALIDATION.md` section 7.

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
- The pre-existing `SamplingEngineTests.AfterAStall_DoesNotBurstCatchUpTicks`
  (Core, untouched) asserts a 10 ms tick spacing against a 20 ms interval and
  failed under CPU load (3 of 4 runs while a browser and dev server were
  running) and passed 4/4 when the machine was idle. A timing-sensitive test,
  not a product defect; left as is.

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

- **Call of Duty / Warzone through Battle.net**: not launched, not validated.
  Procedure: `docs/MANUAL_VALIDATION.md` section 7. In particular unverified:
  the game's real executable name vs the catalog (`cod.exe` etc. are best
  guesses), detection latency with the real process tree, and anything about
  anti-cheat interaction (no compatibility claim is made).
- The **real** Battle.net launcher was not exercised (a stand-in named
  `Battle.net.exe` was); it happened not to be running during validation.
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

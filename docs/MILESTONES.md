# Milestones

## Milestone 001 -- Hardware Discovery & Sensor Validation

Establish reliable access to hardware power sensors: hardware inventory
independent of sensors, sensor discovery across LibreHardwareMonitor and
NVML, power-sensor classification with an explicit no-double-counting
precedence policy, a configurable sampling engine, a CLI (`WattScope.Probe`)
to exercise it all, and JSON export. No UI, no database, no cost calculator,
no gaming analytics, no background startup.

Status: **complete**, including the elevated-vs-non-elevated comparison
(run manually by the developer). See `docs/PROJECT_STATE.md`.

## Milestone 002 -- Energy Calculation, Electricity Cost & Persistent Storage

Turn selected power readings into energy and cost, and persist them:

1. Energy integration from timestamped samples (trapezoidal rule, monotonic
   elapsed time, UTC for persistence), with explicit handling of missing
   samples, invalid readings, duplicate timestamps, sampling gaps,
   sleep/resume, shutdown and unexpected termination -- never interpolating
   across an unverified gap.
2. Configurable wall-power estimation (CPU package + GPU board + other
   components, / PSU efficiency), measured and estimated values kept
   separate, with completeness and assumptions reported.
3. Electricity pricing in EUR/kWh (default EUR) with price history; session,
   daily and monthly energy and cost; decimal money arithmetic with
   documented rounding.
4. Local SQLite persistence with migrations: sessions, compact selected power
   samples, energy aggregates, pricing, estimation settings, sensor
   availability, monitoring gaps.
5. Storage optimization: selected channels only, configurable raw retention,
   15-minute historical aggregates, indexing, bounded memory. Diagnostic
   full-sensor export kept separate.
6. Session lifecycle: start, stop, resume, restart, crash recovery, sleep/wake.
7. CLI validation commands in `WattScope.Probe`, M001 commands unchanged.
8. Automated tests on synthetic data (no hardware needed for math).

Out of scope: desktop UI, gaming analytics, Milestone 003.

Status: **implemented** -- see `docs/PROJECT_STATE.md` for what was verified
and the known limitations. Rules: `docs/ENERGY_MODEL.md`,
`docs/STORAGE.md`.

## Milestone 003 -- Desktop Application & Live Dashboard

1. **Correct electricity price allocation** (Part A): price energy at the
   effective time of consumption, splitting a 15-minute bucket a price
   change falls inside instead of pricing the whole bucket at whichever
   price applied at its start. Exact when split live while recording, exact
   when replayed from raw samples still within retention, an explicit
   flagged approximation only when neither is available. Rules:
   `docs/ENERGY_MODEL.md` section 4.
2. The first real desktop application (`desktop/`): Tauri 2 + React +
   TypeScript + Tailwind, local-first (no cloud, no accounts, no telemetry,
   no subscriptions), talking to the unmodified `WattScope.Core`/
   `Hardware`/`Storage` engine through a new `--serve` JSON sidecar command
   on `WattScope.Probe` -- the same recording pipeline `--record` uses, not
   a duplicate.
3. Dark-first UI: live dashboard (system/CPU/GPU/other power, session
   duration/energy/cost, sensor availability, estimate completeness,
   monitoring status -- never a substituted zero for an unavailable
   reading), a bounded-history live power chart, historical reports
   (current session, previous sessions, daily, monthly, cost history), and
   settings (price, other-component watts, PSU efficiency, monitoring
   interval, language, raw retention, an explicit elevation control).
4. Localization: English, German, Persian (`react-i18next`), with a real
   right-to-left layout for Persian (CSS logical properties throughout, not
   just mirrored text) and numbers/units/timestamps kept in a fixed,
   unambiguous format regardless of UI language.
5. Native integration: the sidecar runs unelevated by default (GPU power
   works, CPU package reports an explicit unavailable state rather than a
   substituted zero); a least-privilege elevated *broker service* was not
   built this milestone (real, separately-scoped work) -- instead, an
   explicit, user-initiated action restarts only the small sidecar process
   elevated via a real Windows UAC prompt, never the whole UI. See
   `docs/ARCHITECTURE.md` ("Privilege model") for exactly what that
   trade-off does and does not cover.

Out of scope (per the brief): gaming analytics, Milestone 004.

Status: **implemented** -- see `docs/PROJECT_STATE.md` for what was
verified, build/test results, and known limitations.

## Milestone 004 -- Tray, Single Instance & Monitoring Lifecycle

Make the desktop app behave like a well-mannered background utility **while
the user has it running**, without any auto-start or privileged component:

1. System tray: icon, live status/power/session lines, Open, Start/Stop
   monitoring, Settings, Exit. Closing the window hides it to the tray;
   monitoring continues. Exit is explicit and stops everything cleanly.
2. Single instance: a second launch shows the existing window and creates no
   second sidecar, session or engine.
3. Monitoring lifecycle decoupled from the window and the sidecar process
   (`MonitoringController`): idempotent start/stop, the window attaches to the
   running session, a hidden window receives no tick traffic.
4. Sidecar lifecycle hardened: exactly one, owned by handle, bounded
   crash-restart, no orphan if the host dies, never kills by name/PID.
5. Session ownership enforced by the database (schema v3: one `Active`
   session per database).
6. Suspend/resume: precise event handling plus the elapsed-time fallback, one
   gap per sleep, no energy across it.
7. Honest partial-monitoring UX: Complete / Partial / Sensor unavailable /
   Stopped / Monitor not running, with no cost shown for an incomplete estimate.
8. Background performance measured (visible vs hidden in tray).
9. EN/DE/FA for everything new, RTL verified.

**Intentionally deferred (product decision, not a failure):** automatic
Windows startup and a least-privilege privileged sensor helper/service.
WattScope runs only when the user explicitly launches it; complete CPU
monitoring on hardware needing privileged sensor access may require the
user to launch it with Administrator privileges (an interim limitation).
No Windows Service, privileged helper/IPC, auto-start registration,
automatic elevation or gaming analytics were built.

Status: **implemented and validated**, including a real manual Windows
sleep/resume test (PASSED). A few items still need a human at the machine and
remain UNVERIFIED (most of the native tray menu, logoff/shutdown, an
Administrator launch, installer install/uninstall) -- see
`docs/PROJECT_STATE.md` and `docs/MANUAL_VALIDATION.md`.
## Milestone 005 -- Gaming Session Detection & Energy Analytics

Recognise when the user is playing a PC game and report energy and cost for
that gaming session, on top of the existing monitoring pipeline:

1. A `GamingSession` that is distinct from the monitoring session (0..n per
   monitoring session) with identity, time span, detection source, end reason,
   measured CPU/GPU energy, estimated system energy, completeness and cost.
   Energy is derived from the monitor's own verified intervals -- no second
   integration, no raw-sample copy, gaps and sleep never counted.
2. Passive, low-overhead process-based detection (no injection, hooks, memory
   access or drivers): a layered identification strategy (user mappings,
   known executables, launcher metadata, library folders, version metadata for
   naming), launchers/browsers/capture tools never games.
3. An explicit, deterministic state machine (NoGame / Candidate / Confirmed /
   EndingGrace) with documented thresholds, one authoritative gaming context,
   defined behaviour for multiple processes, helpers, launchers, restarts and
   crashes.
4. Manual control: mark as game, exclude, rename, remove; start/stop a
   session manually (same one-active-session rule).
5. Schema v4 (additive): `game_mapping`, `gaming_session`,
   `gaming_session_energy`, single-active-session unique index; crash recovery.
6. A Gaming page (live session, history with detail, per-game aggregates,
   mappings) in EN/DE/FA with the same honesty rules (Partial is a lower
   bound, cost only when valid).
7. Mathematically careful per-game aggregates (energy-weighted, Complete and
   Partial never merged).
8. Measured overhead against the M004 baseline; the anti-cheat safety
   boundary documented (no claim of universal compatibility).

Out of scope: FPS overlay, frame-time capture, PresentMon, graphics hooks,
injection, anti-cheat integration, network control, process priority/affinity,
overclocking, power limits, automatic optimization, Discord, cloud sync,
accounts, leaderboards, remote monitoring, a Windows Service, a privileged
helper, auto-start and Smart Plug support.

Status: **implemented and validated** with synthetic processes in the
automated suite and with stand-in processes on the real packaged app. **No
real game was run**: the Call of Duty / Warzone validation is a manual
procedure (`docs/MANUAL_VALIDATION.md` section 7) and is **UNVERIFIED** until
the user performs it. See `docs/GAMING.md` and `docs/PROJECT_STATE.md`.

## Milestone 006 -- Application Completion, UX Polish & Feature-Complete Candidate

Finish the application itself (not release validation): audit, a coherent
four-page structure, Dashboard as the status page, genuinely useful Reports
(Today / Day / Month / Monitoring sessions, Complete and Partial never merged),
Gaming polish, Settings in six validated groups, electricity-price UX with
guarded history, estimation-assumption UX, a short persisted first run,
deliberate loading/empty/error states, Diagnostics inside Settings, safe data
management (guarded deletion, maintenance), database hardening (migration
backup/failure handling, damaged-setting fallbacks), visible sidecar
starting/restarting/failed states with a safe Retry, a performance and memory
review, accessibility, RTL/small-window layout, localization audit,
centralised formatting, consistent terminology, tests, and a feature-freeze
classification.

Out of scope and **not done**: any real-world validation (real game, FPS,
Administrator, install/upgrade/uninstall, shutdown/restart/logoff) -- all
UNVERIFIED in `docs/PRE_RELEASE_CHECKLIST.md`; Windows auto-start; a privileged
helper; new pages; new estimation logic.

Status: **implemented -- FEATURE COMPLETE**. See `docs/PROJECT_STATE.md`.

## Later milestones (not started)

Numbering and scope for milestones after 005 are not yet defined in this
repository. Items already identified as future work (see
`docs/PROJECT_STATE.md`): Windows auto-start and a least-privilege privileged
sensor helper/service so complete CPU monitoring does not require running the
whole app as Administrator.

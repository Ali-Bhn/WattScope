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

## Later milestones (not started)

Numbering and scope for milestones after 003 are not yet defined in this
repository. Items already identified as future work (see
`docs/PROJECT_STATE.md`): a least-privilege elevated broker service so the
sidecar never needs a user-triggered UAC restart to get complete CPU power,
and gaming-session analytics.

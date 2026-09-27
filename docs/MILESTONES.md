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

## Later milestones (not started)

Numbering and scope for milestones after 002 are not yet defined in this
repository. Items already identified as future work (see
`docs/PROJECT_STATE.md`): a least-privilege sensor component so the UI
never runs elevated, and the desktop UI itself.

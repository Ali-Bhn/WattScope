# Pre-release checklist

The single list of real-world validation between "feature complete"
(Milestone 006, `b8100aa`) and a release candidate. **Feature freeze is in
effect**: only verified bug fixes, release blockers, reliability, security,
packaging and accessibility fixes, documentation and test infrastructure.

Status words, used strictly:

- **PASSED** -- actually performed, with the date and where the evidence is.
- **PARTLY** -- part of the item was performed; what is left is stated.
- **UNVERIFIED** -- not performed. Never mark an item passed without running it.

Procedures that need a person are in `docs/MANUAL_VALIDATION.md`; results are
recorded in `docs/PROJECT_STATE.md` ("Pre-release validation"). Always point
validation at a scratch database (`WATTSCOPE_DB`) unless the item is
explicitly about the real one; the scripts in `desktop/scripts/` do this and
refuse to run while WattScope is already running.

The list was audited on 2026-10-02 against `PROJECT_STATE.md` and the
implementation. Items added by that audit are marked *(added)*.

## 0. Automated release gate (re-run before every candidate)

| # | Item | Status |
|---|---|---|
| 0.1 | `dotnet test WattScope.slnx` | PASSED 2026-10-02: 394 (Core 214, Storage 72, Hardware 11, Probe 97) |
| 0.2 | Frontend `npm test`, `npm run typecheck`, `npm run build` | PASSED 2026-10-02: 183 tests, 12 files; clean |
| 0.3 | `cargo test` | PASSED 2026-10-02: 15 |
| 0.4 | `npm run tauri build` (MSI + NSIS) | PASSED 2026-10-02 (builds; installing is section C) |
| 0.5 | Production bundle contains no mock/dev code | PASSED 2026-10-02: one JS chunk, no mock markers, no source maps, no `devtools` feature |
| 0.6 | Schema: a fresh database is created at version 4, integrity ok; `--selftest` | PASSED 2026-10-02: 29/29 |
| 0.7 | `desktop/scripts/validate-lifecycle.ps1` (scratch DB, packaged app; run it on a desktop that is not otherwise in use) | PASSED 2026-10-02: 25/25 on three consecutive runs of the final build (see `PROJECT_STATE.md`) |
| 0.8 | Migration tests, localization completeness tests | PASSED 2026-10-02 (part of 0.1 / 0.2: 7 migration/backup tests, 17 locale tests) |

## A. Real gaming (Milestone 005)

| # | Item | How | Status |
|---|---|---|---|
| A1 | Call of Duty / Warzone detected, named, one session, ended with the game; real executable name and detection latency recorded | `MANUAL_VALIDATION.md` section 7 | UNVERIFIED |
| A2 | A launcher (Battle.net/Steam) alone is never a game | same | UNVERIFIED |
| A3 | A second, unrelated real game is detected (catalog / library-folder heuristics) | play it | UNVERIFIED |
| A4 | A game that is not detected can be started and stopped manually | Gaming page | PARTLY: the manual start/stop path was exercised in the production UI with a stand-in name (2026-10-02, scratch copy); not with a real undetected game |
| A5 | FPS / frame-time impact of WattScope running (the project makes **no** FPS claim until measured) | controlled A/B benchmark with a game | UNVERIFIED |
| A6 | Anti-cheat tolerance: WattScope is external and passive, but **no compatibility is claimed** until the game is run with it open | play with WattScope running | UNVERIFIED |
| A7 | *(added)* Gaming while WattScope is hidden in the tray: session still detected, recorded and ended | play with the window closed | UNVERIFIED |
| A8 | *(added)* Exactly one gaming session and no second monitoring session for one play session (CLI `--gaming`, `--sessions` afterwards) | after A1 | UNVERIFIED |
| A9 | *(added)* If automatic detection fails: the reason is recorded first, then the manual mapping fallback is tested separately | after A1 | UNVERIFIED |
| A10 | *(added)* Windows sleep during a game (covered by tests with the real `EnergyMonitor` only) | optional | UNVERIFIED |

## B. Privilege

| # | Item | Status |
|---|---|---|
| B1 | A Complete (not Partial) monitoring session when launched with *Run as administrator*: CPU package power valid, system power labelled estimated, session energy Complete, cost shown when a price exists | PASSED 2026-10-02 (user-run, session #20; `PROJECT_STATE.md` Phase 6) |
| B2 | A Complete gaming session under Administrator (cost shown, no `>=`) | UNVERIFIED |
| B3 | Unelevated launch shows Partial with the explanation and no cost | PASSED 2026-10-02 on the release build, real WebView2, EN/DE/FA (dashboard, reports, gaming live card) |
| B4 | *(added)* Elevated launch: UAC is user initiated, still exactly one host and one sidecar, a second (unelevated) launch does not create a second instance | PARTLY 2026-10-02: UAC user initiated, one host, one owned sidecar. The second (unelevated) launch was not tried |
| B5 | *(added)* Elevated launch: Exit closes every owned process and the session persists as `Stopped` | PASSED 2026-10-02 (tray -> Exit; 0 processes left; #20 `Stopped`, integrity ok; CLI summary agrees with the UI) |

## C. Installers

| # | Item | Status |
|---|---|---|
| C1 | MSI installs, launches, creates its shortcut, registers uninstall | UNVERIFIED |
| C2 | NSIS installs, launches, creates its shortcut, registers uninstall | UNVERIFIED |
| C3 | Upgrade over the previous build keeps the database (schema 4 opens; a pre-migration backup is only made when a migration is pending) | UNVERIFIED |
| C4 | Uninstall removes the program but **keeps** `%LOCALAPPDATA%\WattScope\wattscope.db` | UNVERIFIED |
| C5 | Reinstall after uninstall finds the old history | UNVERIFIED |
| C6 | WebView2 runtime missing on a clean machine: installer behaviour | UNVERIFIED |
| C7 | *(added)* The installed folder contains the sidecar (`wattscope-probe.exe`) and the installed app starts it | UNVERIFIED |
| C8 | *(added)* No startup entry, scheduled task or service is created by either installer (by design WattScope does not auto-start) | UNVERIFIED |
| C9 | *(added)* No orphan `wattscope` / `wattscope-probe` / WebView2 process after uninstall; uninstalling while the app runs | UNVERIFIED |
| C10 | *(added)* The installers are unsigned: record the SmartScreen / UAC publisher prompt a user will see (signing is a deferred post-release item) | UNVERIFIED |

## D. Windows session events

| # | Item | Status |
|---|---|---|
| D1 | Windows **shutdown** with monitoring running: session closed cleanly or recovered as Interrupted on next launch, no orphan sidecar, database not corrupt | UNVERIFIED |
| D2 | Windows **restart** | UNVERIFIED |
| D3 | Windows **log off** | UNVERIFIED |
| D4 | Real sleep/resume (verified once in M004, session #18; repeat on the release build) | re-verify |
| D5 | *(added)* After D1-D3: WattScope did **not** start by itself; launching it by hand works; exactly one Active session | UNVERIFIED |
| D6 | *(added)* Sleep whose notification is missed / Modern Standby (covered only by the elapsed-time-fallback tests; this PC delivered the notification) | UNVERIFIED (needs other hardware; accepted limitation candidate) |

## E. Desktop shell, by eye

| # | Item | Status |
|---|---|---|
| E1 | Native tray menu: every item, language follows the UI, Start/Stop enable/disable, Exit | PARTLY 2026-10-02 (user, English, while recording a Complete session): status / power / session-and-cost lines correct, Start disabled, Stop enabled, Exit clean. Not checked: Open, Settings, Start/Stop clicks, the stopped state, German/Persian |
| E2 | Second launch forwards to the first window | PASSED by script (2026-10-02); confirm visually once |
| E3 | The window at 880x600, 1120x740, 1600x1000 and maximized; English, German and Persian; in the real WebView2 | PASSED 2026-10-02: 84 page x language x size combinations, no overflow, truncation, raw key or contrast failure |
| E4 | Keyboard-only pass through every page (Tab order, focus ring, dialogs) | PASSED 2026-10-02 with one known issue (focus is dropped when a button disables itself; see Known issues) |
| E4b | Screen-reader pass with a real screen reader (NVDA / Narrator) | PARTLY: the accessibility tree was checked (names, roles, landmarks, headings, live regions); **no real screen reader was run** |
| E5 | Windows high-contrast mode and 125 % / 150 % display scaling | PARTLY: forced-colours and device-pixel-ratio 1.25/1.5/2 emulated in the real WebView2 (one defect found and fixed); the Windows settings themselves were not switched |
| E6 | Persian digits/units/mixed-direction text read correctly on real pages | PASSED 2026-10-02 (programmatic on every page + screenshots); a native Persian reader has not reviewed the wording |
| E7 | *(added)* Empty states (fresh database) and first-run dialog in EN/DE/FA | PASSED 2026-10-02 |
| E8 | *(added)* Error states: restarting notice, give-up with Retry, pages while the monitor is down | PASSED 2026-10-02 (EN; banner also DE/FA) |
| E9 | *(added)* Settings forms: validation, units, help, save round trip; price and deletion confirmation dialogs | PASSED 2026-10-02 |

## F. Data safety, on a copy of the real database

| # | Item | Status |
|---|---|---|
| F1 | Open a **copy** of the real (schema 4) database with the release build: sessions and totals intact, integrity ok | PASSED 2026-10-02 (CLI, sidecar protocol and the UI) |
| F2 | Settings -> Data -> *Delete old history* on that **copy**: preview matches what disappears; running session and prices untouched | PASSED 2026-10-02 (protocol on one copy, the real UI on another; sessions were aged on the copy to make them eligible) |
| F3 | A long (hours) real session: database/WAL growth, memory over time (no leak) | PARTLY: 110-minute scripted soak on 2026-10-02 (`desktop/scripts/soak.ps1`): 9 processes throughout, no handle/thread/DOM/listener growth, WAL plateaus at ~4 MiB, 0 gaps, integrity ok; small private-memory rise (sidecar +1.7 MiB/h, WebView2 +5.8 MiB/h, step-shaped) that 110 minutes cannot classify. **Multi-hour run pending:** `soak.ps1 -Minutes 240` |
| F4 | *(added)* Retention on the copy: only raw samples older than the retention are pruned; totals, costs, sessions untouched | PASSED 2026-10-02 |
| F5 | *(added)* The original database is provably unmodified by all of the above (size, modification time, SHA-256) | PASSED 2026-10-02 |

## G. Release candidate *(added)*

| # | Item | Status |
|---|---|---|
| G1 | Final automated regression on the candidate commit (section 0) | UNVERIFIED |
| G2 | Final MSI/NSIS built from that commit; SHA-256 recorded | UNVERIFIED |
| G3 | One product version everywhere (the sidecar currently stamps sessions with its assembly version `1.0.0.0`; the app is `0.1.0`) | UNVERIFIED -- known inconsistency, see Known issues |
| G4 | README / release notes / known limitations updated | UNVERIFIED |

## Required before the release candidate (decided 2026-10-02)

| # | Item | Status |
|---|---|---|
| R1 | The 4-hour soak (`soak.ps1 -Minutes 240`), deferred until the machine can be left unused for four hours | UNVERIFIED |
| R2 | One final **unattended** lifecycle run (`validate-lifecycle.ps1`, nobody using the desktop) | UNVERIFIED |
| R3 | Focus loss when a busy button disables itself: preferably fixed, first investigating a shared UI primitive instead of patching every button; no broad risky change | OPEN |
| R4 | Sidecar session version `1.0.0.0` vs app version `0.1.0`: resolve or explicitly disposition | OPEN |
| R5 | Gaming withheld cost shows `n/a` where the rest of WattScope shows `--`: resolve or explicitly disposition | OPEN |
| R6 | First-run / Retry foreground-input risk: act only if evidence supports a concrete fix | OPEN |
| R7 | The fresh session after a give-up stays an **open observation**; the stray-input mechanism is **not** classified as proven | OPEN |

## Release blockers found so far

**None.**

## Known issues (non-blocking), found in pre-release validation

1. **Keyboard focus is dropped when a button disables itself while it works**
   (Save in Settings, Start/Stop monitoring, Retry, Optimize/Check database,
   Show what would be deleted, Add price incl. after cancelling the past-price
   dialog; opening the manual gaming form). Focus falls back to the document;
   the next Tab continues from the same place, so every page stays operable by
   keyboard, and results are announced through status regions. Not fixed under
   the freeze (it touches every busy button); a candidate accessibility fix.
2. **Native date/month pickers use the Windows display language**, not the
   WattScope language (e.g. "Oktober 2026" in the English or Persian UI).
   Cosmetic; a property of the WebView2 control.
3. **Version stamp**: sessions record the sidecar's assembly version
   (`1.0.0.0`) while the product version is `0.1.0`. Not shown in the UI;
   to be settled when the candidate version is fixed (G3).
4. **Gaming live card shows `n/a`** for a withheld cost where the rest of the
   app shows `--`. Both are honest (never 0); wording inconsistency only.
5. **Open observation (cause not proven):** in one lifecycle run a fresh
   session appeared about 9 s after the host had given up restarting. No
   automatic code path creates one; not reproduced in 19 further runs; that
   run's database proves outside input reached the test window (the first-run
   dialog was confirmed 2.1 s after launch). Details and conclusion:
   `PROJECT_STATE.md`, "The fresh session after a give-up". The script now
   fails visibly if it happens again.
6. **WattScope takes the foreground when launched or re-shown**, with the
   first-run dialog's primary button focused, so a keystroke meant for another
   application can confirm that dialog. Ordinary launch behaviour; harmless
   (the dialog only records that it was seen).

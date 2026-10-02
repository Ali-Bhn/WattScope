# Pre-release checklist

Everything here was **intentionally deferred** by the Milestone 006 brief
("Application Completion"): it needs a person at the machine, real hardware,
a real game or a disruptive action (install, reboot, log off). **Nothing on
this list has been run. Every item is UNVERIFIED**, and none may be marked
passed until someone actually does it and records the result in
`docs/PROJECT_STATE.md`.

Procedures that already exist are in `docs/MANUAL_VALIDATION.md`; this file is
the complete list and the order to do it in. Always point validation at a
scratch database (`WATTSCOPE_DB`) unless the item is explicitly about the real
one; the scripts in `desktop/scripts/` do this for you and refuse to run while
WattScope is already running.

## A. Real gaming (Milestone 005)

| # | Item | How | Status |
|---|---|---|---|
| A1 | Call of Duty / Warzone detected, named, one session, ended with the game | `MANUAL_VALIDATION.md` section 7 | UNVERIFIED |
| A2 | A launcher (Battle.net/Steam) alone is never a game | same | UNVERIFIED |
| A3 | A second, unrelated real game is detected (catalog / library-folder heuristics) | play it | UNVERIFIED |
| A4 | A game that is not detected can be started and stopped manually | Gaming page | UNVERIFIED |
| A5 | FPS / frame-time impact of WattScope running (the project makes **no** FPS claim until measured) | controlled A/B benchmark with a game | UNVERIFIED |
| A6 | Anti-cheat tolerance: WattScope is external and passive, but **no compatibility is claimed** until the game is run with it open | play with WattScope running | UNVERIFIED |

## B. Privilege

| # | Item | Status |
|---|---|---|
| B1 | A Complete (not Partial) monitoring session when launched with *Run as administrator*: CPU package power valid, cost shown | UNVERIFIED |
| B2 | A Complete gaming session under Administrator (cost shown, no `>=`) | UNVERIFIED |
| B3 | Unelevated launch shows Partial with the explanation and no cost | verified on stand-ins in M004/M005; re-check on the release build |

## C. Installers

| # | Item | Status |
|---|---|---|
| C1 | MSI installs, launches, creates its shortcut, registers uninstall | UNVERIFIED |
| C2 | NSIS installs, launches, creates its shortcut, registers uninstall | UNVERIFIED |
| C3 | Upgrade over the previous build keeps the database (schema 4 opens; a pre-migration backup is only made when a migration is pending) | UNVERIFIED |
| C4 | Uninstall removes the program but **keeps** `%LOCALAPPDATA%\WattScope\wattscope.db` | UNVERIFIED |
| C5 | Reinstall after uninstall finds the old history | UNVERIFIED |
| C6 | WebView2 runtime missing on a clean machine: installer behaviour | UNVERIFIED |

## D. Windows session events

| # | Item | Status |
|---|---|---|
| D1 | Windows **shutdown** with monitoring running: session closed cleanly or recovered as Interrupted on next launch, no orphan sidecar | UNVERIFIED |
| D2 | Windows **restart** | UNVERIFIED |
| D3 | Windows **log off** | UNVERIFIED |
| D4 | Real sleep/resume (verified once in M004, session #18; repeat on the release build) | re-verify |

## E. Desktop shell, by eye

| # | Item | Status |
|---|---|---|
| E1 | Native tray menu: every item, language follows the UI, Start/Stop enable/disable, Exit | UNVERIFIED (most of it) |
| E2 | Second launch forwards to the first window (verified by script; confirm visually) | verified by script |
| E3 | The window at 880x600 and at a large/maximized size, English and Persian, in the real WebView2 (the M006 layout checks used headless Edge against synthetic data) | UNVERIFIED in WebView2 |
| E4 | Keyboard-only pass through every page (Tab order, focus ring, dialogs) and a screen-reader pass (NVDA / Narrator) | UNVERIFIED |
| E5 | Windows high-contrast mode and 125 % / 150 % display scaling | UNVERIFIED |
| E6 | Persian digits/units/mixed-direction text read correctly on real pages | UNVERIFIED |

## F. Data safety, on a copy of the real database

| # | Item | Status |
|---|---|---|
| F1 | Open a **copy** of the real (schema 4) database with the release build: sessions and totals intact, integrity ok | UNVERIFIED |
| F2 | Settings -> Data -> *Delete old history* on that **copy**: preview matches what disappears; running session and prices untouched | UNVERIFIED (automated tests cover the rules) |
| F3 | A long (hours) real session: database/WAL growth, memory over time (no leak) | UNVERIFIED |

## Release blockers found so far

None known. Items above must pass or be consciously accepted before a
release; see "Feature freeze classification" in `docs/PROJECT_STATE.md`.

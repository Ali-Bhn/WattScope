# Pre-release checklist

The single list of real-world validation between "feature complete"
(Milestone 006, `5228fe1`) and a release candidate. **Feature freeze is in
effect**: only verified bug fixes, release blockers, reliability, security,
packaging and accessibility fixes, documentation and test infrastructure.

Status words, used strictly:

- **PASSED** -- actually performed, with the date and where the evidence is.
- **PARTLY** -- part of the item was performed; what is left is stated.
- **UNVERIFIED** -- not performed. Never mark an item passed without running it.
- **ACCEPTED NON-BLOCKER** -- investigated; the behaviour is known and accepted
  for the release, with the reason stated.

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
| 0.4 | `npm run tauri build` (MSI; until Phase 9 the build also produced an NSIS setup) | PASSED 2026-10-02 (MSI + NSIS built at the time; MSI only since Phase 9; installing is section C) |
| 0.5 | Production bundle contains no mock/dev code | PASSED 2026-10-02: one JS chunk, no mock markers, no source maps, no `devtools` feature |
| 0.6 | Schema: a fresh database is created at version 4, integrity ok; `--selftest` | PASSED 2026-10-02: 29/29 |
| 0.7 | `desktop/scripts/validate-lifecycle.ps1` (scratch DB, packaged app; run it on a desktop that is not otherwise in use) | PASSED 2026-10-02: 25/25 on three consecutive runs of the final build (see `PROJECT_STATE.md`) |
| 0.8 | Migration tests, localization completeness tests | PASSED 2026-10-02 (part of 0.1 / 0.2: 7 migration/backup tests, 17 locale tests) |

## A. Real gaming (Milestone 005)

| # | Item | How | Status |
|---|---|---|---|
| A1 | Call of Duty / Warzone detected, named, one session, ended with the game; real executable name and detection latency recorded | `MANUAL_VALIDATION.md` section 7 | PASSED 2026-10-02 unelevated (user-run; `PROJECT_STATE.md` Phase 7): `cod.exe`, catalog match, session dated within 5 s of the process start, `Ended: ProcessExited`. Relaunch inside the grace period not tried |
| A2 | A launcher (Battle.net/Steam) alone is never a game | same | PASSED 2026-10-02 for the real Battle.net (6 minutes alone, and again after the game closed). Steam not tried |
| A3 | A second, unrelated real game is detected (catalog / library-folder heuristics) | play it | UNVERIFIED |
| A4 | A game that is not detected can be started and stopped manually | Gaming page | PARTLY: the manual start/stop path was exercised in the production UI with a stand-in name (2026-10-02, scratch copy); not with a real undetected game |
| A5 | FPS / frame-time impact of WattScope running (the project makes **no** FPS claim until measured) | controlled A/B benchmark with a game | UNVERIFIED |
| A6 | Anti-cheat tolerance: WattScope is external and passive, but **no compatibility is claimed** until the game is run with it open | play with WattScope running | PARTLY 2026-10-02: one 35-minute unelevated Warzone session with WattScope running, no objection reported by the user. Not tried elevated (the CPU sensor driver is only loaded then). Still **no compatibility claim** |
| A7 | *(added)* Gaming while WattScope is hidden in the tray: session still detected, recorded and ended | play with the window closed | PASSED 2026-10-02: recorded and ended while in the tray (detection itself happened with the window open) |
| A8 | *(added)* Exactly one gaming session and no second monitoring session for one play session (CLI `--gaming`, `--sessions` afterwards) | after A1 | PASSED 2026-10-02: gaming #1 only, monitoring #21 only, 0 gaps |
| A9 | *(added)* If automatic detection fails: the reason is recorded first, then the manual mapping fallback is tested separately | after A1 | UNVERIFIED -- detection did not fail, so the fallback was not exercised with a real game |
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

**Distribution decision (2026-10-02, Phase 9): v0.1.0 ships the MSI only**
(`bundle.targets` = `["msi"]`); reasons in `PROJECT_STATE.md`, Phase 9.
Earlier entries that mention an NSIS build (0.4 and the milestone records) are
historical. "Either installer" below now means the MSI.

| # | Item | Status |
|---|---|---|
| C0 | *(added)* MSI built from `f634235` + the MSI-only setting; static inspection and administrative extraction (payload, sidecar commit stamp, metadata, no dev-server dependency) | PASSED 2026-10-02 (`PROJECT_STATE.md` Phase 9) |
| C1 | MSI installs, launches, creates its shortcut, registers uninstall | PASSED 2026-10-02 (installed twice; Start menu and Public Desktop shortcuts; Add/Remove Programs entry; the Finish page launches the app unelevated by default) |
| C2 | NSIS installs, launches, creates its shortcut, registers uninstall | NOT APPLICABLE: NSIS is not distributed for v0.1.0 (never installed; its default folder is the database folder) |
| C3 | Upgrade over the previous build keeps the database (schema 4 opens; a pre-migration backup is only made when a migration is pending) | UNVERIFIED -- no second version exists to upgrade from or to. Starting the same MSI again offers **Remove only** (no repair, no same-version reinstall). Verify a real version-to-version upgrade before the first update is distributed |
| C4 | Uninstall removes the program but **keeps** `%LOCALAPPDATA%\WattScope\wattscope.db` | PASSED 2026-10-02 (database byte-identical after the uninstall) and again 2026-10-03 (uninstall while running: every earlier row unchanged, only the running session's own rows added); the WebView2 profile `%LOCALAPPDATA%\com.wattscope.desktop` is kept too; the MSI has no data-removal option |
| C5 | Reinstall after uninstall finds the old history | PASSED 2026-10-02: 24 sessions, gaming history, prices and settings unchanged row by row and shown in the UI; no migration, no duplicate database |
| C6 | WebView2 runtime missing on a clean machine: installer behaviour | UNVERIFIED |
| C7 | *(added)* The installed folder contains the sidecar (`wattscope-probe.exe`) and the installed app starts it | PASSED 2026-10-02: sidecar stamped `5ca97f7`, hash identical to the build; one host, one sidecar from `C:\Program Files\WattScope`; 26 minutes at 1.0000 Hz, 0 gaps, arithmetic verified |
| C8 | *(added)* No startup entry, scheduled task or service is created by either installer (by design WattScope does not auto-start) | PASSED 2026-10-02 for the MSI (checked after the first install and after both uninstalls) |
| C9 | *(added)* No orphan `wattscope` / `wattscope-probe` / WebView2 process after uninstall; uninstalling while the app runs | PASSED 2026-10-03: Windows Installer listed WattScope and WattScope.Probe as in use, the Restart Manager closed both, the session ended as a clean stop, nothing was restarted, no reboot requested, database intact |
| C10 | *(added)* The installers are unsigned: record the SmartScreen / UAC publisher prompt a user will see (signing is a deferred post-release item) | PARTLY 2026-10-02: UAC shows "unknown publisher" for the MSI (install and uninstall). **SmartScreen on a downloaded copy is UNVERIFIED** (the tested file had no internet mark) |
| C11 | *(added)* Install and run as a standard (non-administrator) user; a second Windows account on the same machine (per-machine program, per-user history) | UNVERIFIED (needs another account) |
| C12 | *(added)* *Run as administrator* on the **installed** copy (Complete monitoring from `C:\Program Files\WattScope`) | UNVERIFIED (B1 and the S1 smoke test ran the same build elevated from the build folder, not the installed copy) |

## D. Windows session events

| # | Item | Status |
|---|---|---|
| D1 | Windows **shutdown** with monitoring running: session closed cleanly or recovered as Interrupted on next launch, no orphan sidecar, database not corrupt | PASSED 2026-10-04 (Fast Startup shutdown, PC off 66 min; session #28 recovered as Interrupted at its last observation; integrity ok; `PROJECT_STATE.md` Phase 10) |
| D2 | Windows **restart** | PASSED 2026-10-04 (full boot, window hidden in the tray; session #27 recovered as Interrupted) |
| D3 | Windows **log off** | PASSED 2026-10-04 (session #26 recovered as Interrupted) |
| D4 | Real sleep/resume (verified once in M004, session #18; repeat on the release build) | PASSED 2026-10-04 on the installed build (S3, 5 min 3 s; same session #29, one `Suspend` gap, nothing integrated across it). Not tried: window hidden in the tray, an elevated session, hibernate |
| D5 | *(added)* After D1-D3: WattScope did **not** start by itself; launching it by hand works; exactly one Active session | PASSED 2026-10-04 after each of D1-D3 |
| D6 | *(added)* Sleep whose notification is missed / Modern Standby (covered only by the elapsed-time-fallback tests; this PC delivered the notification) | UNVERIFIED -- **not testable on this PC** (`powercfg /a`: S3 only, no S0 Low Power Idle); needs other hardware; accepted limitation candidate |

## E. Desktop shell, by eye

| # | Item | Status |
|---|---|---|
| E1 | Native tray menu: every item, language follows the UI, Start/Stop enable/disable, Exit | PARTLY 2026-10-02 (user, English, while recording a Complete session): status / power / session-and-cost lines correct, Start disabled, Stop enabled, Exit clean. Also seen in the Partial state during Phase 7 (lines correct apart from Known issue 7; Open and Exit used). Not checked: Settings, Start/Stop clicks, the stopped state, German/Persian |
| E2 | Second launch forwards to the first window | PASSED by script (2026-10-02); confirm visually once |
| E3 | The window at 880x600, 1120x740, 1600x1000 and maximized; English, German and Persian; in the real WebView2 | PASSED 2026-10-02: 84 page x language x size combinations, no overflow, truncation, raw key or contrast failure |
| E4 | Keyboard-only pass through every page (Tab order, focus ring, dialogs) | PASSED 2026-10-02 with one known issue (focus is dropped when a button disables itself), fixed in Phase 11 (R3); the fix is covered by automated tests, the by-hand pass was not repeated |
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
| F3 | A long (hours) real session: database/WAL growth, memory over time (no leak) | PASSED 2026-10-04, see the end of this cell. First, 110-minute scripted soak on 2026-10-02 (`desktop/scripts/soak.ps1`): 9 processes throughout, no handle/thread/DOM/listener growth, WAL plateaus at ~4 MiB, 0 gaps, integrity ok; small private-memory rise (sidecar +1.7 MiB/h, WebView2 +5.8 MiB/h, step-shaped) that 110 minutes cannot classify. **PASSED 2026-10-04 (Phase 12, R1):** 240-minute soak on `8faec3a`; WAL plateaus at ~4 MiB, database 396 KiB; WebView2 private memory flat (+0.07 MiB/h); the sidecar's rose from 50 to ~61 MiB in the first three hours and was flat in the last (-0.2 MiB/h); no leak shown. On a scratch database, unelevated (Partial); a multi-hour **Complete** (elevated) session and a run longer than four hours were not done |
| F4 | *(added)* Retention on the copy: only raw samples older than the retention are pruned; totals, costs, sessions untouched | PASSED 2026-10-02 |
| F5 | *(added)* The original database is provably unmodified by all of the above (size, modification time, SHA-256) | PASSED 2026-10-02 |

## G. Release candidate *(added)*

| # | Item | Status |
|---|---|---|
| G1 | Final automated regression on the candidate commit (section 0) | PASSED 2026-10-04 (Phase 14) on application candidate `8faec3a` (HEAD `9778726`; the two commits after it change only `docs/PRE_RELEASE_CHECKLIST.md` and `docs/PROJECT_STATE.md`): `dotnet test WattScope.slnx` 418/418 (Core 221, Storage 72, Hardware 22, Probe 103; 0 failed, 0 skipped); `npm test` 191/191 in 13 files; `npm run typecheck` and `npm run build` clean; `cargo test` 15/15; product version `0.1.0` in all four places and the built sidecar reads FileVersion `0.1.0.0`. No MSI built (that is G2) |
| G2 | Final MSI built from that commit (MSI only, see section C); SHA-256 recorded | PASSED 2026-10-05 (Phase 15): built from a detached clean worktree of exactly `8faec3a` (`npm ci`, `publish-probe.ps1`, `npm run tauri build`). **`WattScope_0.1.0_x64_en-US.msi`, 37,928,960 bytes, SHA-256 `255CD48895E94F3554FB611EDED3EF9007D0FC5C234F8BD7371A302DCCFC1D85`** (FROZEN; unsigned). ProductVersion `0.1.0`; packaged host `0.1.0`; packaged sidecar FileVersion `0.1.0.0`, ProductVersion `0.1.0+d472211f9b1c038fe2823b7e00199c8522aba840`. Payload is exactly `wattscope.exe` and `wattscope-probe.exe`. Preserved at `<release-artifact-directory>\` (not in git). Details: `PROJECT_STATE.md` Phase 15. Not installed (no install is required by G2) |
| G3 | One product version everywhere | PASSED 2026-10-04 (Phase 11): product version `0.1.0` in `package.json`, `Cargo.toml`, `tauri.conf.json` and, new, `Directory.Build.props` for every .NET project; the sidecar is built as assembly / file version `0.1.0.0` (the four-part Windows form) with informational version `0.1.0+<commit>`, read back from the built binary. `ProductVersionTests` fails if they drift. To re-read from the installed files once the final MSI (G2) exists |
| G4 | README / release notes / known limitations updated | PASSED 2026-10-05 (Phase 16): `README.md` rewritten as user-facing product documentation (monitoring first, gaming optional; MEASURED / ESTIMATED / UNAVAILABLE; requirements; installation; measurement limitations; privacy; conservative gaming wording); new `docs/RELEASE_NOTES.md` (highlights, installer filename / size / SHA-256 / source commit, known limitations). README, release notes, checklist and `PROJECT_STATE.md` were compared for version, MSI name, SHA-256, commit, signing, startup and privacy wording. Documentation only; the frozen MSI was not touched |

## Required before the release candidate (decided 2026-10-02)

| # | Item | Status |
|---|---|---|
| R1 | The 4-hour soak (`soak.ps1 -Minutes 240`), deferred until the machine can be left unused for four hours | PASSED 2026-10-04 (Phase 12) on `8faec3a`, scratch database, unelevated, **PC in normal use** (a real game ran for the first 81 minutes): 240.2 min, one Partial session ended `Stopped`, 14,410 samples at 1.0000 /s, 0 gaps, stored energy equal to a recomputation from the raw samples, integrity ok, 9 processes throughout, no memory / handle / thread / DOM growth. Details and what was not covered: `PROJECT_STATE.md`, Phase 12 |
| R2 | One final **unattended** lifecycle run (`validate-lifecycle.ps1`, nobody using the desktop) | PASSED 2026-10-04 (Phase 13) on the `8faec3a` binaries (HEAD `8f20f36`, documentation only since), scratch database, unelevated: **25/25**, exit code 0, one run, script unchanged. Unattended, but the **desktop was in use** (real input occurred, also while WattScope had the foreground); none of it reached a control: the first-run flag was never written and only the script's two sessions exist. Database integrity ok, 0 Active sessions, no process left. Details: `PROJECT_STATE.md`, Phase 13 |
| R3 | Focus loss when a busy button disables itself: preferably fixed, first investigating a shared UI primitive instead of patching every button; no broad risky change | PASSED 2026-10-04 (Phase 11): defect confirmed and fixed with one shared primitive (`ActionButton`); what is left is in Known issues 1 |
| R4 | Sidecar session version `1.0.0.0` vs app version `0.1.0`: resolve or explicitly disposition | PASSED 2026-10-04 (Phase 11): the sidecar is now version `0.1.0` (stamped as `0.1.0.0`); see G3 |
| R5 | Gaming withheld cost shows `n/a` where the rest of WattScope shows `--`: resolve or explicitly disposition | PASSED 2026-10-04 (Phase 11): every gaming cost cell (live card, history, detail, per-game totals, daily report) now shows `--`, in all three languages |
| R6 | First-run / Retry foreground-input risk: act only if evidence supports a concrete fix | ACCEPTED NON-BLOCKER 2026-10-04 (Phase 11): no evidence supports a fix, no code changed; see Known issues 6 |
| R7 | The fresh session after a give-up stays an **open observation**; the stray-input mechanism is **not** classified as proven | PASSED 2026-10-04 (Phase 11) for the behaviour: a fresh session after a give-up is clean and the only Active one (`GiveUpRetryTests`). The cause of the single unexplained occurrence stays **not proven**; see Known issues 5 |

## Release blockers found so far

**None.**

## Release-candidate determination (2026-10-05, Phase 16)

**Required gates** (sections 0 and G, and R1-R7): all PASSED or, for R6,
ACCEPTED NON-BLOCKER -- R1, R2, R3, R4, R5, R7, G1, G2, G3, G4, F3. No required
item is UNVERIFIED. **Release blockers: none.**

**Remaining UNVERIFIED / PARTLY items are optional, deferred or
hardware-limited, and none is a required gate:** A3, A4 (real undetected game),
A5 (FPS; no FPS claim is made), A6 (no anti-cheat claim is made), A9, A10; B2,
B4 (second launch while elevated); C3 (upgrade, no earlier version exists; do it
before the first update ships), C6, C10 (SmartScreen on a downloaded copy), C11,
C12; D6 (Modern Standby: **not testable** on this PC, no S0 Low Power Idle;
accepted limitation, not a failure); E1, E2 (visual confirmation), E4b, E5. Each
is stated in the user-facing known limitations where it matters.

**WattScope v0.1.0 is RC-ready** (validation and documentation complete). This is
not a published release: nothing is pushed, tagged, signed or uploaded. The
frozen MSI is `WattScope_0.1.0_x64_en-US.msi`, SHA-256
`255CD48895E94F3554FB611EDED3EF9007D0FC5C234F8BD7371A302DCCFC1D85`.

## Known issues (non-blocking), found in pre-release validation

1. **Keyboard focus when a control goes away** -- mostly fixed in Phase 11
   (R3). Buttons that become unavailable through their own action (Save,
   Start/Stop monitoring, Retry, Optimize/Check database, Show what would be
   deleted, Add price, dialog buttons, the report's Next/Today) now stay
   focusable and keep the focus; the manual gaming form takes the focus when
   it opens. Consequence: such a button is a Tab stop even while unavailable
   (announced as unavailable, does nothing). Still dropped to the document,
   because the control itself is removed: Retry once the monitor is back,
   Cancel/Begin of the manual gaming form, Stop manual session. The next Tab
   continues from the same place. Verified by automated tests and a headless
   browser check, not by a repeated by-hand keyboard pass.
2. **Native date/month pickers use the Windows display language**, not the
   WattScope language (e.g. "Oktober 2026" in the English or Persian UI).
   Cosmetic; a property of the WebView2 control.
3. **Version stamp** -- resolved in Phase 11 (R4/G3). Sessions recorded by
   earlier builds keep the stamp `1.0.0.0`; new ones say `0.1.0.0`. Existing
   rows are not rewritten. Diagnostics shows the app as `0.1.0` and the
   monitor as `0.1.0.0`: the same version in the two platform forms.
4. **Gaming cost `n/a`** -- resolved in Phase 11 (R5). Two worded places keep
   "n/a" on purpose, neither is a value cell: the tray line ("cost n/a") and
   unknown text fields of the Diagnostics panel (version, OS, WebView2).
5. **Open observation (cause not proven):** in one lifecycle run a fresh
   session appeared about 9 s after the host had given up restarting. No
   automatic code path creates one; not reproduced in 19 further runs; that
   run's database proves outside input reached the test window (the first-run
   dialog was confirmed 2.1 s after launch). Details and conclusion:
   `PROJECT_STATE.md`, "The fresh session after a give-up". The script now
   fails visibly if it happens again. Phase 11 (R7) added tests showing that
   such a session, however it is started, is clean: the abandoned session is
   closed as Interrupted at its last observation and nothing of it reaches
   the new one.
6. **WattScope takes the foreground when launched or re-shown**, with the
   first-run dialog's primary button focused, so a keystroke meant for another
   application can confirm that dialog. Ordinary launch behaviour; harmless
   (the dialog only records that it was seen). Reviewed in Phase 11 (R6) and
   accepted: the window is only brought forward by a user action (launch,
   second launch, tray), never by a give-up; Retry is never focused
   automatically, so only a click on it or a deliberate Tab reaches it; and
   the host ignores Retry unless the monitor is really down.
7. **Tray power line has no lower-bound mark**: while monitoring is Partial
   the tray shows "Estimated power: 118.5 W" without `>=`. The status line
   above it says "partial" and the session line has `>=`; wording only
   (found in Phase 7).
8. **A Windows sign-out, restart or shutdown ends the session as
   `Interrupted`, not `Stopped`** (Phase 10, all three tests, window open or
   hidden). Windows ends the processes before the session is closed; the next
   launch recovers it at its last persisted observation. Nothing is
   estimated: up to about one persistence interval (~10 s) of real monitoring
   just before the event is absent. Data-safe and honest; the label differs
   from what the code comments describe for this path.
9. **No notice is shown when a previous session was recovered** as
   Interrupted on launch (Phase 10; the sidecar reports it, the UI does not
   surface it).
10. **Open observation:** a `powershell.exe` "application was unable to start
    correctly (0xc0000142)" popup was logged by Windows during the sign-out
    and the restart, not during the shutdown. WattScope never starts
    PowerShell and no evidence links the popup to it; its source was not
    identified.

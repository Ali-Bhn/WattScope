# Manual validation procedures

> Milestone 006: the complete list of deferred real-world checks (with status) is `docs/PRE_RELEASE_CHECKLIST.md`. Run validation against a scratch database (`WATTSCOPE_DB`), never the real one.

Things the automated suites and `desktop/scripts/validate-lifecycle.ps1`
cannot check because they need a person at the machine (real sleep, the
native tray, logoff/shutdown, UAC). Each procedure says exactly what to do
and what a pass looks like. Results are recorded in `docs/PROJECT_STATE.md`;
anything not run there is marked **UNVERIFIED**.

## Status of each procedure

| # | Procedure | Status |
|---|---|---|
| 1 | Sleep / resume | **VERIFIED / PASSED** (2026-10-01, Session #18; details below and in `PROJECT_STATE.md`) |
| 2 | System tray | **Partly verified**: only *Exit WattScope* was exercised (during procedure 1). Icon, tooltip, item states/live text, Start/Stop/Open/Settings, menu language: UNVERIFIED |
| 3 | Logoff / shutdown / restart | UNVERIFIED |
| 4 | Administrator launch | **VERIFIED / PASSED** (2026-10-02, session #20, Complete with cost; `PROJECT_STATE.md` Phase 6) |
| 5 | Persian / RTL visual check | **VERIFIED** on every page of the production app in the real WebView2 (2026-10-02, pre-release validation Phase 5 in `PROJECT_STATE.md`); a native reader has not reviewed the wording |
| 6 | Installers | UNVERIFIED (packages build; installing/uninstalling was not run) |
| 7 | Gaming session with a real game (Call of Duty / Warzone via Battle.net) | **UNVERIFIED** -- not run; the automated suite and the non-game real validation (`PROJECT_STATE.md`, Milestone 005) are done, but no real game was launched |

All procedures use the packaged app (`desktop/src-tauri/target/release/wattscope.exe`
or the installed one) and the CLI that ships beside it
(`wattscope-probe.exe`). Note the validation sessions are real rows in
`%LOCALAPPDATA%\WattScope\wattscope.db`.

## 1. Sleep / resume (Windows suspend)

Do **not** run this unattended; it puts the PC to sleep.

1. Close WattScope if it is running (tray icon -> *Exit WattScope*).
2. Launch WattScope normally. Wait until the dashboard says monitoring is
   running (GPU power updating).
3. Note the session id: `wattscope-probe.exe --sessions` (the `Active` one).
4. Let it run for at least 60 s.
5. Note the wall-clock time, then put the PC to sleep (Start -> Power ->
   Sleep). Leave it asleep for **at least 2 minutes** and note roughly how long.
6. Wake the PC. Wait 30 s.
7. Run `wattscope-probe.exe --summary --session <id>`.

Pass criteria:

- The session is still the same id and still `Active` (monitoring resumed).
- Under *Monitoring gaps* there is **exactly one** gap covering the sleep:
  start ~ when you slept, end ~ when you woke, duration ~ the time asleep.
  Its reason should be `Suspend`. `ClockDiscontinuity` or `SamplingStall`
  also means no energy was integrated across it (the elapsed-time fallback
  caught a missed notification) -- record which one you got.
- *Monitored time* is roughly (time awake), **not** including the sleep.
- Energy did not jump across the sleep: wall-estimate Wh should be about
  (average power x awake time), not inflated.
- The dashboard resumed ticking within a few seconds of waking, with no burst
  of rapid catch-up updates.

**Recorded result (2026-10-01, run by the user, unelevated): PASSED.** Session #18
(started 23:27:31) was put to Windows Sleep and resumed, then stopped via
tray -> Exit. Exactly one gap, reason `Suspend`, 23:29:31 -> 23:31:28
(00:01:57), "no energy recorded"; period 23:27:31 -> 23:36:56 with monitored
time 00:07:28 (= 9:25 - 1:57); same session throughout; GPU 5.251 Wh at
100 % coverage; CPU 0.000 Wh / 0 % (expected, unelevated); estimate Partial;
complete cost n/a; partial lower-bound cost 0.00446172 EUR excluded from it;
final status `Stopped: Stopped by user`. This PC delivered the `Suspend`
notification; Modern Standby behaviour remains untested.

If the PC uses Modern Standby (S0 low-power idle; see `powercfg /a`), the
process may keep running during "sleep" and the expected result differs:
note that in the results instead of forcing a pass.

## 2. System tray (native UI)

1. Launch WattScope. A WattScope icon appears in the notification area
   (check the hidden-icons overflow).
2. Hover: tooltip reads `WattScope - <monitoring state>`.
3. Right-click: lines for state, estimated power, session energy/cost
   (read-only), then *Open WattScope*, *Start monitoring*, *Stop monitoring*,
   *Settings*, *Exit WattScope*. While recording, *Start monitoring* is
   disabled and *Stop monitoring* enabled.
4. *Stop monitoring*: state line becomes "Monitoring stopped", Start becomes
   enabled; `wattscope-probe.exe --sessions` shows the session `Stopped`.
   *Start monitoring*: a **new** session starts (exactly one `Active`).
5. Close the window with X: the window disappears, the tray icon stays,
   `--sessions` shows the session still advancing.
6. Left-click the tray icon (or *Open WattScope*): the same window returns
   showing the same session.
7. *Settings*: the window opens on the Settings page.
8. Switch language to Deutsch and Persian in Settings: the tray menu text
   changes language (Persian is native Windows menu text -- note whether
   it reads correctly).
9. *Exit WattScope*: the icon disappears, no `wattscope*.exe` process
   remains (Task Manager), and `--sessions` shows the session `Stopped`
   (not `Interrupted`).

## 3. Logoff / shutdown / restart

Best effort by design; crash recovery is the safety net.

1. Launch WattScope, note the session id, wait 30 s.
2. Sign out (or restart) Windows. Sign back in.
3. `wattscope-probe.exe --sessions`: the session is `Stopped` or
   `Interrupted` (either is acceptable; `Active` after the recorder is gone
   is not -- it is recovered to `Interrupted` by the next command).
4. Confirm WattScope did **not** start by itself, and there is no
   `wattscope*.exe` process.

## 4. Administrator launch (interim limitation)

1. Right-click `wattscope.exe` -> *Run as administrator* (accept UAC).
2. Dashboard should report **Complete monitoring**, CPU package power
   updating, Settings -> *Administrator* shows "Running as Administrator",
   and a session cost appears if a price is configured.
3. Launch normally while the elevated one runs: the existing window is
   shown (single instance); no second sidecar/session.

## 5. Persian / RTL visual check

Switch to فارسی in Settings, then check: the layout mirrors (sidebar on the
right), text is right-aligned, numbers/units (`W`, `kWh`, timestamps) stay
left-to-right and readable inside Persian sentences, the quality banner and
buttons are not clipped, and the chart still draws left-to-right (time axis
is deliberately not mirrored).

## 6. Installers

- Install the NSIS setup and the MSI (separately, on a clean state): the app
  launches, shows in the Start menu, runs as in section 2.
- Uninstall: WattScope's files and shortcuts are removed; no Windows Service,
  startup entry or scheduled task was ever created (check
  `Task Manager -> Startup`, `services.msc`); `%LOCALAPPDATA%\WattScope`
  (the monitoring history) is left in place.

## 7. Gaming session (Call of Duty / Warzone through Battle.net)

This is the real-game validation of Milestone 005. **WattScope never launches
a game or touches Battle.net** -- you start the game yourself, normally. Run
it twice if you can: once as a normal user (expect a **Partial** session) and
once with WattScope started via *Run as administrator* (expect **Complete**).
Set an electricity price first (Settings) if you want to see a cost.

1. **Launcher alone.** Launch WattScope. On the *Gaming* page it says *No game
   detected*. Open Battle.net (do not start a game) and leave it open for 2+
   minutes. Expected: still *No game detected*; `wattscope-probe.exe --gaming`
   lists no new session. (A launcher is never a game.)
2. **Start the game** from Battle.net. Note the time. In Task Manager ->
   Details note the **executable name** of the game process (for example
   `cod.exe`) -- write it down; it is the most useful finding of this test.
3. **Detection.** Within about 25-70 s the page shows *Possible game,
   confirming: ...* and then the *Gaming Session* card with the game name and
   **LIVE** (a catalog/mapped executable confirms after ~20 s; a heuristic guess
   after ~60 s). If nothing appears after 2 minutes: *Manage games* -> *Pick
   a running program* -> choose the game's executable, name it
   (e.g. "Call of Duty: Warzone"), *Save*, and note that automatic detection
   needed help. A wrong name can be corrected the same way (*Rename* in the
   session's detail panel renames every stored session of that game).
4. **While playing (>= 10 minutes):** the duration counts up; GPU power is
   plausible for the load; CPU power shows a value when elevated and `--` with
   *Unavailable* when not; estimated system power and energy rise; the cost
   is shown only for a Complete session (otherwise *n/a* with the reason);
   unelevated, the amber *Partial* banner is shown and energy is marked `>=`.
   The dashboard chart and the tray keep updating (monitoring continues).
   Closing the WattScope window to the tray must not stop either.
5. **One session.** While playing run `wattscope-probe.exe --gaming`: exactly
   one session, `Active`, the right name, started within ~5 s of when you
   launched the game's process (not 20-60 s later).
6. **Close the game** (exit it fully; leave Battle.net open). Wait ~45 s. The
   card returns to *No game detected* about 30-35 s after the game closed (the
   end grace period). Re-launching the game within 30 s of closing it must
   continue the **same** session.
7. **History.** *Gaming history* shows the session; `--gaming` shows it as
   `Ended: ProcessExited` with an end time within ~5 s of when you closed
   the game (**not** 30 s later), `duration` ~ `monitored` (unless you slept),
   and the per-game totals.
8. **Plausibility.** Energy ~ average system power x monitored time (for
   example 340 W for 1 h = 0.34 kWh; the cost = kWh x your price). Peaks are
   >= averages. GPU average is in the range you see in other tools.
9. **Partial vs Complete.** Unelevated: Partial, CPU `--`/`n/a`, energy and
   system power marked `>=`, **no** cost (a lower-bound cost may appear in
   the detail panel, labelled). Elevated: Complete, a cost if a price covers
   the session.
10. *Optional:* put the PC to sleep mid-game and wake it. Same session; its
    duration includes the sleep, `monitored` does not; the detail panel shows a
    monitoring gap.

Pass: steps 1, 3, 5, 6, 7 and 9 behave as stated, and the numbers in 8 are
plausible. Record in `docs/PROJECT_STATE.md`: the game's executable name, how
long detection took, whether manual correction was needed, and elevated /
unelevated results. Anything not run stays **UNVERIFIED**.

If WattScope is run alongside a game protected by an anti-cheat, remember
that this project makes **no** claim of compatibility with any specific
anti-cheat (`docs/GAMING.md`, "Anti-cheat safety boundary"). If the game or its
anti-cheat objects, close WattScope.
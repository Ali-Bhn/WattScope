# Manual validation procedures

Things the automated suites and `desktop/scripts/validate-lifecycle.ps1`
cannot check because they need a person at the machine (real sleep, the
native tray, logoff/shutdown, UAC). Each procedure says exactly what to do
and what a pass looks like. Results are recorded in `docs/PROJECT_STATE.md`;
anything not run there is marked **UNVERIFIED**.

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

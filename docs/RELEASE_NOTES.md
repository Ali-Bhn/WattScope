# WattScope v0.1.0

**Status: release candidate** (validation and documentation complete; not yet
published, signed or distributed).

WattScope monitors your PC's electricity use on Windows, accumulates the
energy and estimates what it costs. Gaming-session analytics are optional.

## Highlights

- Continuous local monitoring of CPU and GPU power with energy (kWh) and cost.
- Honest figures: every value is MEASURED, ESTIMATED or UNAVAILABLE (`--`);
  incomplete (Partial) estimates are never mixed with Complete ones and are
  never costed.
- Everything stays on your PC; no account or cloud service.
- Runs in the system tray; English, German and Persian.

## Monitoring

- One reading per second while monitoring runs. A gap (for example while the PC
  sleeps) is recorded as a gap; no energy is invented across it.
- CPU package and GPU board power are measured when the machine exposes a valid
  sensor. Implausible 0 W readings are rejected rather than stored.
- System ("wall") power is an **estimate**: measured CPU + GPU power plus an
  assumed "other system power" (default 50 W), divided by an assumed PSU
  efficiency (default 90 %). Both are editable in Settings.
- When CPU power is unavailable (common without administrator rights), WattScope
  continues in **Partial** monitoring: a lower bound, kept separate, no cost.

## Reports and history

- Past sessions, daily and monthly reports, price history (sub-bucket pricing
  when a price changes during a session).
- Data management in Settings: optimize/check the database and delete old
  history, with a preview and an explicit confirmation of the exact scope.

## Gaming analytics (optional)

- Detects a running game and reports its energy and estimated cost; a
  launcher alone is never counted as a game. Games that are not detected can be
  marked or started manually.
- Passive by design: it reads the Windows process list and the power readings it
  already has. It does not inject into, hook or read the memory of games.
  No anti-cheat compatibility is claimed and none has been tested.
- A gaming session's energy is the whole PC's energy during that time, not the
  game's alone. No frame-rate figures are provided.

## Desktop and tray behaviour

- Starts only when you launch it; **no automatic start with Windows**, no
  service. A second launch shows the running window.
- Closing the window hides it to the tray; monitoring continues. Exit from the
  tray menu or Settings.
- It never elevates itself. For Complete CPU monitoring on machines that need
  it, start it with *Run as administrator*.
- If Windows ends the session (sign-out, restart, shutdown), the session is
  recovered on next launch as *Interrupted* at its last recorded observation.

## Languages

English, German and Persian (right-to-left). The installer is English only.

## Privacy and local data

- Sessions, samples, prices and settings are stored in
  `%LOCALAPPDATA%\WattScope\wattscope.db` (SQLite) on your PC.
- No account is needed. WattScope itself has no telemetry and does not upload
  your data. (Windows components such as the WebView2 runtime are separate
  software and may make their own connections.)
- Uninstalling keeps your history.

## Installer

| | |
|---|---|
| File | `WattScope_0.1.0_x64_en-US.msi` |
| Size | 37,928,960 bytes |
| SHA-256 | `255CD48895E94F3554FB611EDED3EF9007D0FC5C234F8BD7371A302DCCFC1D85` |
| Source (application) commit | `cd50c5dfbe1f6a5e993b6bcb10dfc3d8dd1538b0` |
| Platform | Windows 10/11, x64; per-machine install to `C:\Program Files\WattScope` (administrator approval required) |
| Signature | none (unsigned) |

The release artifact was built before a publication-only Git history rewrite, so its embedded build identifier (the sidecar's product version, `0.1.0+d472211...`) uses the corresponding pre-publication commit ID. In the published history that commit is `cd50c5d`.

Verify before installing: `Get-FileHash .\WattScope_0.1.0_x64_en-US.msi`.
Windows shows "Unknown publisher" because the installer is unsigned; this is
not a sign of a problem in itself. Install only a copy you obtained from a
trusted source whose checksum matches. If SmartScreen appears for a downloaded
file, use its normal options; do not change Windows security settings to
install. The WebView2 runtime is normally already present; otherwise the
installer downloads Microsoft's bootstrapper (needs internet).

## Known limitations

1. **Unsigned installer.** Expect "Unknown publisher" (and possibly a
   SmartScreen notice on a downloaded copy). Code signing is planned for later.
2. **CPU sensor availability** depends on hardware and permissions. Where CPU
   package power is unavailable, WattScope runs in Partial mode (no cost).
3. **Whole-system / wall power contains estimates** (other-system power and PSU
   efficiency assumptions). It is not a calibrated wall meter.
4. **Displays and external peripherals are not measured.**
5. **Windows only.** No macOS or Linux.
6. **No automatic startup or service.** WattScope runs only when launched; a
   privileged helper for unattended complete CPU monitoring is not part of
   v0.1.0.
7. **English-only installer** (the application itself has EN/DE/FA).
8. **Version-to-version upgrade has not been tested**, because there is no
   earlier release to upgrade from. Reinstalling the same version offers only
   *Remove*. Uninstall/reinstall with history preserved was tested.
9. **Modern Standby was not tested.** The development PC does not support it
   (S0 Low Power Idle); ordinary sleep/resume was tested.
10. **Hardware and sensor coverage varies between PCs**, and v0.1.0 was
    validated on a single development machine. Not tested: a PC without
    WebView2, installing as a standard user or from a second Windows account,
    Complete monitoring from the installed copy launched as administrator.
11. A game is identified by its executable name; the built-in game catalog is
    small, so some games need to be marked as a game once. See
    `docs/GAMING.md`.
12. Minor: native date pickers use the Windows display language; the tray power
    line lacks the "lower bound" mark while monitoring is Partial.

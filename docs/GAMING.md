# Gaming Session Detection & Energy Analytics (Milestone 005)

WattScope recognises when a PC game is running and reports the energy and
estimated electricity cost of that gaming session, using the monitoring
infrastructure that already exists. It is an **external, passive** feature:
it reads the list of running programs and the power readings it already reads.
It is not an FPS overlay, not frame-time capture, and not an anti-cheat
integration (see "Anti-cheat safety boundary").

Everything described here lives in the .NET sidecar (`WattScope.Core`,
`WattScope.Storage`, `WattScope.Hardware`, `WattScope.Probe`). The React UI
only displays state and sends user decisions; it never detects games.

## 1. Two kinds of session

```
MonitoringSession   (M002: one per recording run; owns samples, buckets, gaps)
    |
    +-- GamingSession A     0..n per monitoring session
    +-- GamingSession B
```

A gaming session **never replaces** a monitoring session and never duplicates
its data. It stores identity, a time span, peak power and per-price energy
totals. Its energy is derived from the *same verified intervals* the monitor
already integrates (`IMonitorObserver`, section 5) -- no second integration, no
raw-sample copy. A gaming session always belongs to exactly one monitoring
session (foreign key); stopping monitoring ends the gaming session.

Detection only runs while monitoring runs: a gaming session is energy
attributed to monitored time, so detecting a game while nothing is monitored
would create a session with no data.

## 2. Detection model

### What is read

| Source | Used for | Cost |
|---|---|---|
| Toolhelp process snapshot (`CreateToolhelp32Snapshot`) | PID, parent PID, executable name of every process | one kernel call per poll, no handles opened |
| `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName` + `GetProcessTimes` | image path and creation time, **once per new process** | cached; steady state opens no handles. Known system/everyday software (`GameCatalog.NeverGame`) is not opened at all |
| `FileVersionInfo` (version resource of the exe) | the product name, to *name* a heuristically detected game | once per distinct path, cached |

`PROCESS_QUERY_LIMITED_INFORMATION` is the least-privileged process right
Windows has. It cannot read or write the process's memory, cannot inject
anything, and cannot control the process. If Windows refuses it (elevated or
protected processes) the process is simply known by name only.

Not used: WMI event subscriptions or continuous WMI queries, filesystem
crawling, executable hashing, window enumeration, GPU/engine counters,
process priority/affinity changes, or anything that touches a game's memory,
threads, modules or network traffic.

### Layers (strongest first) -- `GameIdentifier`

1. **User mapping** -- the user marked the executable as a game (optionally
   with a display name) or **excluded** it. An exclusion always wins, even
   over the catalog. A mapping may be specific to a full path, which beats a
   name-only mapping.
2. **Known executable catalog** -- a small built-in list (`GameCatalog.KnownGames`).
   It is data only; nothing in the detector is specific to one game.
3. **Launcher metadata** -- a non-helper executable whose process-tree
   ancestor is a known game launcher (Steam, Battle.net, Epic, GOG Galaxy, EA,
   Ubisoft Connect, Riot, Xbox/Game Pass), **and** whose path is outside the
   launcher's own folder (so the launcher's helpers/updaters do not count).
4. **Game-library folder** -- the path is under `steamapps\common`,
   `Epic Games\<game>`, `XboxGames`, `GOG Galaxy\Games`, `GOG Games`,
   `Ubisoft Game Launcher\games` or `EA Games`.
5. **Version metadata / product name** -- used to *name* a layer 3/4 game
   ("Hades II"), never to classify one.

Conservative rules applied to layers 3-4: the process must have a known
path; nothing under `C:\Windows`; helper-like names (`crash`, `handler`,
`redist`, `setup`, `install`, `unins`, `update`, `helper`, `report`, `error`,
`anticheat`, `battleye`, `overlay`, `launcher`, `service`, ...) are rejected; a
parent PID that started *after* its child (a reused PID) is not an ancestor.

What is **never** a game by the built-in rules: launchers/stores (Steam,
Battle.net, Epic, ...), browsers, Discord and other chat, OBS and other
capture tools, GPU-vendor overlays, tuning/monitoring tools, the Windows
shell and services, and WattScope itself. Being a GPU user or being
fullscreen is never looked at and never classifies anything. The user can
still mark any of these as a game.

Process verdicts are cached by `(PID, creation time)` and pruned to the live
process set each poll (bounded memory); the cache is cleared when mappings change.

## 3. State machine -- `GameDetector`

```
                       candidate seen
        NoGame ---------------------------------> Candidate
          ^                                         |   |
          |   vanished before confirmation          |   | present for the confirm delay
          +-----------------------------------------+   v
          |                                       Confirmed  <------------------+
          |                                         |                           |
          |                    process missing      v          process back     |
          +--------- grace period elapsed ---- EndingGrace ---------------------+
```

The detector sees only timestamps and process lists; it has no clock or OS
access, so every transition is deterministic and unit-tested.

| Threshold | Default | Meaning |
|---|---|---|
| `PollInterval` | 5 s | how often the process list is examined |
| `ConfirmDelayMapped` | 20 s | a user-mapped / catalog game must be present this long |
| `ConfirmDelayHeuristic` | 60 s | a launcher-child / library-folder guess must be present this long |
| `EndGracePeriod` | 30 s | how long a confirmed game may be missing before its session ends |

All four are `GameDetectorOptions` (internal configuration, not user settings).

Consequences, each covered by a test:

- a transient process (gone before the confirm delay) never creates a session;
- a launcher alone, however long it runs, never creates one;
- a game that restarts within the grace period (a helper restart, a quick
  relaunch with a new PID) stays **one** session;
- a session ends `EndGracePeriod` after the process was first found missing,
  but is dated **from when it was first missed** -- the grace period is never
  counted as gaming time;
- the session starts at **first sight** of the candidate, not at confirmation:
  the candidate's energy is kept in memory and committed when it is
  confirmed (discarded if it vanishes);
- a candidate yields to a better one (a user-mapped/catalog game appearing
  while a heuristic guess is still being confirmed).

### Multiple games, helpers, launchers

**One authoritative gaming context.** While a game is Candidate, Confirmed or
in its grace period every other process is ignored, so one stretch of time
can never be attributed to two games. Therefore:

- *Several candidate processes at once*: the winner is chosen
  deterministically -- strongest detection source first (user mapping,
  catalog, launcher child, library), then executables with a known path, then
  executable name (ordinal), then path -- independent of enumeration order.
- *A second game while one runs*: ignored. It is not back-dated into the
  first game's session; when the first ends, the second becomes a candidate
  **in the same poll** and starts its own session from that moment.
- *Helper/child processes of the running game*: presence is by game identity
  (executable name), so helpers neither create another session nor extend or
  shorten this one.
- *Launcher stays open*: irrelevant to the game's lifetime. The session ends
  when the game's process is gone, not when the launcher closes.
- *The executable restarts*: see grace period. *A crash and relaunch*:
  within the grace period it is one session; after it, two.
- *WattScope's own crash/restart*: see section 6.

## 4. Manual control

Automatic detection will never be perfect. From the Gaming page (or the CLI):

- **Mark as game** with a display name; **exclude** an executable; **rename**
  a game (applies to the running session *and* all stored sessions of that
  game, so history and totals show one name); remove a mapping. Mappings are
  stored locally in `game_mapping`. A running-programs picker lists plausible
  candidates **on demand** (never pushed to the UI).
- **Start / stop a gaming session manually** as a fallback. A manual session
  is marked `is_manual` and `detection_source = Manual`, ends only by manual
  stop or when monitoring stops (processes are ignored for it), requires a
  running monitoring session, and obeys the one-active-session rule: it is
  refused while another gaming session is active, and a pending candidate is
  simply superseded. "Remember as game" can also store the executable as a mapping.

CLI (database only): `--gaming`, `--game-mark <exe> [--name N]`,
`--game-exclude <exe>`, `--game-unmark <exe>`.

## 5. Energy attribution

`EnergyMonitor` accepts an optional `IMonitorObserver` (called under the
monitor's lock, failures contained). `GamingSessionTracker` receives every
accepted sample together with the integrator's result for it:

- an **interval** (a verified stretch between two valid samples of one
  unbroken chain), or
- a **gap** (sleep, stall, clock discontinuity, restart, ...), for which
  there is no interval.

Each interval is clipped to the time the current gaming context has been in
force (`PowerInterval.SplitAt` at the context boundary -- the same
interpolation the 15-minute buckets use) and added to a `GamingLedger`.
Because only verified intervals exist, **gaps can never reach a ledger**:

- *Windows sleep with the game still running*: the monitor cuts a `Suspend`
  gap, nothing is integrated across it, and the gaming session is **not
  split**. Its wall-clock `Duration` includes the sleep; its
  `MonitoredSeconds` and all energy do not. If the game was closed during
  the sleep, the session is dated to end at the suspend. A half-confirmed
  candidate is dropped at suspend (presence across a sleep proves nothing).
- *Sensor interruption / sampling stall*: same -- unmonitored duration, no energy.
- *Interval change* (monitoring restarts inside the same monitoring
  session with a recorded `Paused` gap): the gaming session is kept.
- *Monitoring stopped / WattScope exit*: the gaming session ends with
  `MonitoringStopped`.

Duration and monitored duration are therefore distinguishable
(`Duration - MonitoredSeconds` = unmonitored time) and both are shown.

### What is computed

Per gaming session, from `EnergyTotals` (the existing type) plus peaks:
CPU package energy and GPU board energy (measured, with their own coverage
seconds), estimated system energy (`Complete` and `Partial` kept separate, as
in `docs/ENERGY_MODEL.md` section 3), average power (energy / covered seconds),
peak CPU / GPU / system power, and cost.

`Measured`, `Estimated`, `Partial`, `Unavailable` keep their meaning:

- CPU unavailable (for example WattScope not elevated -> `ImplausibleZero`):
  the session is **Partial**. CPU average and peak are *null* (never 0), CPU
  coverage is 0 s, GPU analytics are still shown, the estimated system
  figure is a **lower bound** (marked `>=`), and **no complete cost** is shown.
  A separate lower-bound cost may be shown, labelled as such.
- No channel measured: `Unavailable` ("no estimate"), no energy, no cost.
- The system figure is always labelled ESTIMATED; WattScope has no wall meter.

### Cost

Energy is kept **per electricity price** (the price in effect when it was
consumed; a change inside a session splits the interval exactly like the
monitor does). A session's cost is shown only when its estimate is Complete
and every joule is priced (`GamingCostStatus.Priced`); otherwise the reason
(`WithheldPartial`, `WithheldUnpriced`, `NoEstimate`) is shown. Energy before
the first price is *unpriced*, never priced at zero. If a price is added
**retroactively** inside a stored session (a row would straddle a boundary it
was never split at), the cost is re-split proportionally by wall time and
flagged `ApproximateProportional` -- never presented as exact. Cost is
computed at read time; it is not stored.

### Per-game aggregates

Over **finished** sessions (a running one has no end yet), per game:
session count, total play time (duration), total monitored time, average
session duration, and:

- averages are **energy-weighted** (total joules / total covered seconds) --
  never an average of per-session averages (test: 3600 s @ 400 W + 600 s @
  200 W -> 371.4 W, not 300 W);
- average CPU / GPU power use total CPU/GPU joules over the seconds those
  channels were actually valid, so missing CPU data does not drag the average
  down;
- **Complete and Partial sessions are never merged** into one energy or cost
  figure: they are separate groups. The Partial group is a lower-bound energy
  with no cost. Cost is summed only over fully priced Complete sessions; the
  count of Complete-but-unpriced sessions is reported next to it;
- the displayed game name is the most recent session's.

## 6. Session ownership and recovery

- **At most one Active gaming session per database**: unique partial index
  `ux_gaming_single_active` on `gaming_session(status) WHERE status = 0`. The
  tracker also owns exactly one context. A second `CreateGamingSession`
  raises `GamingSessionExistsException`; the tracker then abandons the
  context instead of integrating into a session that does not exist.
- The tracker persists progress (absolute totals, peaks, last observation)
  at confirmation, every 10 s of sample time, at grace start, and at suspend.
  Writes are idempotent rewrites of one small row set.
- **Crash recovery**: a gaming session left `Active` by a dead process is
  closed as `Interrupted` at its last persisted observation by
  `RecoverInterruptedSessions` (run at sidecar start and by any CLI command
  that holds the monitor lock), together with its monitoring session. Up to
  ~10 s of the most recent gaming energy is lost on a crash, never invented.
  If the game is still running after a sidecar restart, a **new** gaming
  session is detected and confirmed in the new monitoring session.

## 7. Database (schema v4, additive)

Migration 4 adds three tables; no M001-M004 table or row is touched, and a
database written by an M004 build migrates in place (tested, including data
preservation). Older builds refuse a v4 database (`DatabaseSchemaTooNewException`)
rather than corrupt it -- the established forward-only behaviour.

| Table | Content |
|---|---|
| `game_mapping` | user decisions: `exe_name` (NOCASE), `exe_path` (`''` = any location), `kind` (1 game / 2 excluded), `display_name`; `UNIQUE(exe_name, exe_path)` |
| `gaming_session` | `monitoring_session_id` (FK), `game_key`, `exe_name`, `exe_path`, `display_name`, `detection_source`, `is_manual`, start/end/last-observation (ms), `status` (0 active / 1 ended / 2 interrupted), `end_reason`, four peak columns. CHECKs: status/source/reason ranges, `ended >= started`, an ended session has an end. Indexes by game+start, by start, by monitoring session; the single-active unique index |
| `gaming_session_energy` | per gaming session per price (`price_effective_utc_ms`, `-1` = unpriced): the `EnergyTotals` columns plus the time span (`first_utc_ms`, `last_utc_ms`) of the energy in the row; PK `(gaming_session_id, price_effective_utc_ms)`, `ON DELETE CASCADE` |

Raw power samples are **not** duplicated: a gaming session has a handful of
rows (typically 1-2) regardless of its length.

## 8. Protocol (sidecar <-> desktop)

Extended only as needed (`docs/ARCHITECTURE.md`, "Data flow (--serve)"):

- Every `tick` carries `gaming`: `null` outside gaming, otherwise one small
  `GamingLiveState` (state, session id, game name, exe name, manual flag,
  source, start, duration, monitored seconds, completeness, system kWh and
  whether it is a lower bound, cost status, cost, currency). Process lists
  are never sent. The host forwards ticks unchanged (no Rust change was needed).
- Requests: `get_gaming_state`, `get_gaming_sessions`, `get_gaming_session`,
  `get_game_aggregates`, `get_game_mappings`, `set_game_mapping`,
  `remove_game_mapping`, `start_manual_gaming`, `stop_manual_gaming`,
  `get_running_executables` (on demand). Inputs are typed and validated
  (executable names, 80-char display names without control characters);
  nothing a request names is executed.

## 9. UI

A **Gaming** entry in the existing navigation, same cards/typography, no
animation. *Live*: "No game detected" (or "possible game, confirming"), or a
card with the game name, LIVE/ENDING, Auto-detected/Manual, duration (with
monitored time when they differ), GPU power, CPU power (`--` and a reason
when unavailable), estimated system power, energy (`>=` when partial) and
cost (`n/a` with the reason when not valid), plus a partial banner. *History*:
a table (game, start, duration, monitored, energy, cost, completeness) with a
detail panel (end, unmonitored time, gaps, end reason, detection source,
average and peak CPU/GPU/system power, rename/exclude). *Per game*: the
aggregates above. *Manage games*: mappings, a running-programs picker, manual
session start. All strings exist in English, German and Persian (RTL layout
via logical CSS properties; game names, `CPU`, `GPU`, `W`, `kWh` and numbers
are isolated left-to-right inside Persian text).

## 10. Anti-cheat safety boundary

WattScope M005 is **external and passive**. It does not, and must not:

- inject into any process, load code into a game process, or hook anything
  (no DLL/code injection, no API hooking, no DirectX/Vulkan/OpenGL hooks);
- read or write game memory, scan memory, or read protected memory;
- modify game files, automate gameplay, simulate or intercept player input;
- manipulate, inspect or shape network traffic;
- bypass, disable or interfere with an anti-cheat system;
- use kernel drivers of its own for this feature.

Game detection uses the operating system's ordinary process list plus one
query-only handle per new process (`PROCESS_QUERY_LIMITED_INFORMATION`).
Energy monitoring uses the same sensor providers as M001-M004
(LibreHardwareMonitor, NVML, WMI) -- unchanged by this milestone.

**No claim of universal anti-cheat compatibility is made.** No software can
guarantee how every third-party anti-cheat product will classify every other
application. WattScope reads only what any task manager reads, but it has not
been tested against any specific anti-cheat, and the sensor stack (note:
LibreHardwareMonitor may load a driver for CPU power when elevated -- an M001
fact, unchanged) is outside this milestone's control. If a game or anti-cheat
vendor objects to monitoring software, do not run WattScope alongside it.

## 11. Performance

Design: one toolhelp snapshot every 5 s (a single kernel call over ~300
processes), a per-process cache so steady state opens no handles, an O(1)
lookup per process, no timers or threads while monitoring is stopped, and an
observer call per sample that returns immediately when no game is active.
Measured overhead against the M004 baseline, and what was **not** measured
(frame rate), is in `docs/PROJECT_STATE.md` ("Milestone 005 -- measured
overhead"). No claim of zero FPS impact is made.

Detection can be switched off for a whole WattScope run with the environment
variable `WATTSCOPE_GAME_DETECTION=off` (no process polling at all; monitoring
is unaffected; `get_gaming_state` then reports `available: false`).

## 12. Known limitations

1. **Executable-name identity.** A game is identified by its executable file
   name; two different games sharing a name (`game.exe`) are one game for
   history and totals unless the user maps them by full path. Path-specific
   mappings exist, but sessions are keyed by name.
2. **The catalog is small and its executable names may not match every
   install.** Call of Duty ships under different names per title/version
   (`cod.exe`, `ModernWarfare.exe`, ...); the catalog lists a few and the
   launcher-child/library heuristics and user mappings cover the rest. The
   first launch of an unlisted title may need "mark as game" (60 s heuristic
   delay applies until then).
3. **Heuristics can be wrong.** A non-game started by a launcher outside
   the launcher's folder can look like a game; exclude it once and it is remembered.
4. **Detection resolution is the poll interval (5 s)**; start/end times are
   accurate to about that. Energy during a session's first seconds before
   the first poll is not attributed.
5. **Sessions are attributed from first sight, not from process start.** A
   game already running when monitoring starts is dated from the first poll.
6. **Protected/elevated processes** may expose only their name; heuristics
   need a path, so such a game must be in the catalog or user-mapped.
7. **Overlapping games are not attributed concurrently** (by design: one context).
8. **Retroactive price changes** give an approximate (flagged) gaming cost,
   because gaming sessions keep no raw samples to replay.
9. Up to ~10 s of the newest gaming energy is lost if WattScope crashes.
10. Gaming analytics are only as complete as the sensors: unelevated, CPU
    power is unavailable on the development machine, so its sessions are Partial.
11. Frame rate / frame-time, per-process GPU use and per-game power
    *attribution* beyond "the whole machine's measured power while a game
    runs" are out of scope. The energy of a gaming session is the machine's
    energy during it, including everything else running (browser, chat, ...).

## 13. Manual validation (Warzone / any real game)

See `docs/MANUAL_VALIDATION.md`, "Gaming session (Call of Duty / Warzone)".
Nothing in that procedure launches a game or touches a launcher for you.

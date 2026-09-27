# Storage

Local SQLite persistence for energy monitoring (Milestone 002), implemented
in `WattScope.Storage` (`SqliteEnergyStore`, `Microsoft.Data.Sqlite`). No
cloud or network dependency. Default location:
`%LOCALAPPDATA%\WattScope\wattscope.db` (override with `--db <path>`).

## What is stored -- and what deliberately is not

Continuous monitoring stores **only the two energy channels** (CPU package,
GPU board) plus metadata. The full sensor inventory (159-264 sensors on the
development machine) is never written to the database. The full-sensor JSON
export (`--monitor --export`) remains a separate, opt-in diagnostic tool; it
is unchanged and never writes to the database.

| Table | Content | Growth |
|---|---|---|
| `session` | start/end, last persisted observation, status, end reason, estimation-settings snapshot, elevation flag, app version | 1 row per session |
| `session_sensor` | which provider sensor fed each channel | ~2 rows per session |
| `power_sample` | UTC ms, CPU/GPU power as integer **milliwatts** (NULL when invalid), per-channel `ReadingState` | 1 row per sample; pruned by retention |
| `energy_bucket` | per session per 15-min UTC bucket: monitored s, CPU/GPU J + coverage s, complete/partial estimated J + coverage s | <= 96 rows per recorded day; kept |
| `monitoring_gap` | start, end, reason, detail | 1 row per gap |
| `sensor_availability` | run-length encoded channel state (one row per *change*, not per sample) | tiny |
| `electricity_price` | effective-from, price as exact decimal text, currency | 1 row per price change |
| `estimation_settings` | settings history; latest row is current | 1 row per change |
| `app_setting` | key/value (raw retention days) | constant |

Costs are **not** stored: they are computed at report time from energy
buckets and the price history, so they always reflect the documented
pricing rules (`docs/ENERGY_MODEL.md` section 4).

### Compact raw samples

`power_sample` is a `WITHOUT ROWID` table keyed by `(utc_ms, session_id)`,
with integer milliwatts (SQLite varints: ~3 bytes per power value). Storing
milliwatts rounds each stored raw value to 1 mW -- far below sensor
resolution (NVML itself reports integer mW). Energy is integrated from the
full-precision in-memory values, not from the stored raw samples, so this
rounding never affects energy or cost. Keying by time first makes retention
pruning a primary-key range delete.

Measured (automated test `OneDayAtOneHertz_StaysSmall`, synthetic data
through the real monitor): **24 h at 1 Hz = 86,401 samples + 96 buckets =
2.14 MB, about 26 bytes per sample** including B-tree overhead and
aggregates. For comparison, the Milestone 001 full-sensor JSON export
measured ~46 MB per 10 minutes. See `docs/PROJECT_STATE.md` for the
real-hardware measurement.

### Retention and bounded size

- Raw samples: kept for `raw_retention_days` (default **7**, 0-3650,
  `--retention <days>`). Pruned when `--record` starts, hourly during
  recording (on a separate short-lived connection), and when retention is
  changed. At 1 Hz the default bounds raw data at roughly 7 x 2.1 MB ~= 15 MB.
- The database uses `auto_vacuum = INCREMENTAL` (set at creation) and runs
  `PRAGMA incremental_vacuum` after pruning, so freed pages are returned to
  the file system instead of only being reused.
- 15-minute energy buckets, gaps, availability, sessions and prices are
  kept indefinitely (the long-term history). Worst case with 24/7 recording
  is 35,040 bucket rows per year -- on the order of 2-3 MB/year.

### Indexes

- `power_sample` primary key `(utc_ms, session_id)`: time-range reads and pruning.
- `energy_bucket` primary key `(session_id, bucket_start_utc_s)`: per-session
  totals and upserts; `ix_energy_bucket_start`: day/month range queries across sessions.
- `monitoring_gap`: by session and by start time.
- `sensor_availability (session_id, channel, to_utc_ms)`: finding the open run to close.

## Transactions and durability

- Everything produced since the previous flush (raw samples, bucket
  *deltas* applied with `INSERT ... ON CONFLICT DO UPDATE SET x = x + excluded.x`,
  gaps, availability changes, last-observation time) is written in **one
  transaction** every 10 s. Raw samples and aggregates can therefore never
  disagree, and a failed write leaves nothing half-applied (covered by
  `WriteBatch_IsAtomic_NothingIsPersistedWhenAnyPartFails`).
- Journal mode **WAL**, `synchronous = NORMAL`: a committed transaction
  survives a process crash. After an OS crash or power loss, the most recent
  commits may be lost (the database stays consistent). Combined with the
  10-second flush interval, the maximum data loss on a process crash is the
  unflushed ~10 s, which is simply absent afterwards -- never estimated.
- `foreign_keys = ON`.
- WAL also lets `--summary`/`--sessions` read while `--record` is writing.

## Schema migrations

Forward-only migrations in `SchemaMigrations`, tracked in
`PRAGMA user_version`. Each migration and its version bump run in one
transaction. Opening a database whose version is newer than the build
supports fails with `DatabaseSchemaTooNewException` without modifying it.
Released migrations must never be edited; add a new one instead.
Current version: **1**.

## Concurrency

- One recording process per database, enforced by `MonitorLock`
  (`<db>.lock`, opened with `FileShare.None`; released by the OS if the
  process dies).
- A `SqliteEnergyStore` instance is not thread-safe; `EnergyMonitor`
  serializes all access to its store (sampling loop and OS power-event
  thread).

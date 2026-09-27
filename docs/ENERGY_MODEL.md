# Energy Model

How WattScope turns power readings into energy, estimated wall energy, and
cost (Milestone 002). This is the authoritative description of the
calculation rules. Code lives in `WattScope.Core` (`Energy/`, `Estimation/`,
`Pricing/`, `Reporting/`, `Monitoring/`), which has no hardware or database
dependency. All of it is covered by synthetic, deterministic tests.

## 1. Channel sources (what gets integrated)

Exactly two measured channels are integrated:

| Channel | Fed by (classifier role) | Never fed by |
|---|---|---|
| `CpuPackage` | the selected `CpuPackage` sensor(s) | `CpuCores`, `CpuGraphics`, per-core sensors, `CPU Memory` (all are subsets of, or unrelated to, package power) |
| `GpuBoard` | the selected `GpuBoard` sensor(s) | `GpuChip` (a subset of board power), the unselected overlapping candidate, `GPU Power Limit` |

`PowerSampleBuilder` fixes these bindings once per session from the
discovery-time `PowerClassificationResult` (see `docs/SENSOR_CONTRACT.md`
section 3):

- Only the classifier's `Selected` sensor for the role is used. On the
  development machine that means `CPU Package` (LHM) and `GPU Board Power`
  (NVML); `CPU Cores` and LHM's `GPU Package` are never added.
- Several selections for one role are summed only when they are distinct
  devices from **one** provider (e.g. a dual-socket CPU). Selections for one
  role coming from more than one provider cannot be proven to be different
  physical devices, so the channel is `Ambiguous` (no value) instead.
- If the classifier left the role unselected (multi-GPU ambiguity), the
  channel is `Ambiguous`. If no sensor exists, it is `NotPresent`.

### Per-reading validation (`PowerValidationRules`)

A selected sensor's value becomes a channel value only if it is valid. Any
other outcome gives the channel a `ReadingState` and **no value** -- never
zero:

| State | When |
|---|---|
| `Missing` | the selected sensor is absent from the tick (provider refresh threw) |
| `Unavailable` | provider reported no value (`SensorAvailability` other than `Available`) |
| `NonFinite` | NaN / infinity |
| `Negative` | < 0 W |
| `AboveMaximum` | > 2000 W for a single sensor |
| `ImplausibleZero` | exactly 0 W on the **CPU package** channel |

`ImplausibleZero` exists because LibreHardwareMonitor reports CPU package
power as a normal `0.0 W` reading when it lacks driver access (observed on
the development machine in every non-elevated run). A running CPU never
draws 0 W, so this is rejected rather than integrated as a genuine zero.
GPU board power of 0 W is accepted (a powered-down hybrid-graphics dGPU can
legitimately report it).

If any one sensor of a multi-device channel is invalid, the whole channel
is invalid for that sample (no partial sum).

## 2. Integration

**Method: trapezoidal rule on consecutive valid samples.** For two samples
`s0`, `s1` of the same unbroken chain,

```
E = (P0 + P1) / 2 * dt
```

- `dt` is the elapsed **monotonic** time between the samples
  (`Stopwatch` timestamps captured together with each tick), never an
  assumed nominal interval. A tick that fires late simply has a longer `dt`.
- UTC timestamps are used only to place energy on the calendar (buckets,
  days, months) and for persistence.
- The trapezoid is exact when power changes linearly between samples, and
  its error for smooth curves is O(dt^2) (verified: a 1-minute-period sine
  wave sampled at 1 s integrates to within 0.1 % of the analytic value).
  It is not a model of what happened *between* samples at sub-second scale;
  spikes shorter than the sampling interval are not captured by any
  sample-based method.
- Energy is accumulated as unrounded `double` joules. Values are only
  rounded for display.

### When an interval is *not* integrated

The integrator (`EnergyIntegrator`) only produces an interval when both
samples belong to the same verified chain. Otherwise the time between them
becomes a **monitoring gap** (persisted, with a reason) and receives **no
energy at all** -- nothing is interpolated, extrapolated, or zero-filled:

| Condition | Result |
|---|---|
| Same UTC or monotonic timestamp as the previous sample | sample discarded as a duplicate (counted), never integrated twice |
| Monotonic or UTC time went backwards | gap, `ClockDiscontinuity` |
| Monotonic `dt` > `MaxSampleInterval` (default: max(5 s, 3 x sampling interval)) | gap, `SamplingStall` |
| UTC `dt` and monotonic `dt` differ by > 2 s | gap, `ClockDiscontinuity` (system clock change, or a sleep that only one clock observed) |
| OS suspend/resume notification | gap, `Suspend` |
| Session resumed after a normal stop | gap, `Paused` |
| Session resumed after the recording process died | gap, `Interrupted` |

Per-channel invalid readings are different from gaps: the session is still
being observed, but that channel has no value. An interval contributes
energy to a channel only if **both** endpoints are valid for that channel.
So one missing CPU reading at 1 Hz removes the two adjacent 1-second
intervals from CPU energy (no interpolation across the unverified reading)
while GPU energy for those intervals is unaffected. Every total therefore
carries a **coverage** figure: seconds actually covered vs. monitored
seconds.

### Buckets

Intervals are split at every 15-minute UTC boundary (epoch-aligned) and
accumulated per bucket. Power at a split point is linearly interpolated
*within* the already-verified interval, so the pieces sum exactly to the
interval's energy. 15 minutes is chosen because every real-world UTC offset
is a multiple of 15 minutes, so every bucket lies entirely inside one local
calendar day in any time zone, and daily/monthly totals are exact sums of
buckets (including 23/25-hour DST days).

## 3. Estimation model (wall power)

```
Estimated wall power = (CPU package + GPU board + other components) / PSU efficiency
```

| Setting | Default | Meaning |
|---|---|---|
| `OtherComponentsWatts` | 50 W | assumed constant draw of everything not measured (board, RAM, storage, fans) |
| `PsuEfficiency` | 0.90 | assumed constant AC->DC efficiency |
| `IncludeCpuPackage` / `IncludeGpuBoard` | true / true | which measured inputs are required |

The defaults are placeholders, not measurements of any particular machine;
every report prints the assumptions in effect. Change them with
`--estimation`. A session snapshots the settings at creation and uses them
for its whole lifetime (including resumes), so changing settings never
silently rewrites the past.

Because the model is linear, it is applied per integrated interval to
energies (`(E_cpu + E_gpu + P_other * dt) / efficiency`), which is identical
to integrating the estimated power series.

**Completeness** (`EstimateCompleteness`), per interval and per report:

- `Complete` -- every included input was measured for the interval.
- `Partial` -- at least one, but not all, included inputs were measured. The
  value (available inputs + other components, / efficiency) is a **lower
  bound**. Missing inputs are omitted, never counted as 0 W. Partial energy
  is stored and reported separately and is **excluded from cost**.
- `None` -- no included input was measured; there is no estimate.

A report is `Complete` only if the complete estimate covers all monitored
time. Measured channel energies and estimated wall energy are always
separate fields; the estimate is labeled "ESTIMATED" everywhere it is shown
and is never presented as measured wall power. WattScope has no wall-power
meter input.

Practical consequence on the development machine: without elevation, CPU
package power reads 0 W, is rejected, and every interval is `Partial` -- so
no cost is reported. This is the intended behavior (see
`docs/SENSOR_CONTRACT.md` section 4). If a machine genuinely cannot read CPU
power, the user can set `--include-cpu false`, which makes the estimate
"complete" with respect to a model that explicitly excludes the CPU -- a
visible, user-made assumption rather than a silent substitution.

## 4. Electricity cost

- Prices are `decimal` per kWh, stored as exact decimal text. Default
  currency: EUR (ISO 4217 code, one currency per database; mixed currencies
  are rejected). Negative prices are rejected (dynamic tariffs are out of
  scope).
- Prices form a history: each entry applies from its effective time until
  the next one. Adding a price never changes the cost of energy consumed
  before its effective time.
- **Granularity:** each 15-minute bucket is priced with the entry in effect
  at the bucket's start. A price that becomes effective mid-bucket applies
  from the next bucket.
- Energy consumed before the first price is **unpriced** and reported as
  such; it is never priced at zero. With no price at all, cost is "n/a".
- Only the complete wall estimate is priced. The partial (lower-bound)
  estimate's cost is shown separately, labeled, and never added.

### Conversion and rounding rules

1. Joules (double) -> kWh (decimal): `(decimal)joules / 3 600 000`. The
   double-to-decimal conversion keeps 15 significant digits; no other
   rounding.
2. Cost per bucket: `kWh * price`, full decimal precision (28-29 digits), not
   rounded.
3. Session / day / month totals: the sum of unrounded bucket costs.
4. Display: rounded **once**, to 2 decimals, midpoint **away from zero**
   (`MoneyRounding`: 0.125 -> 0.13, 2.675 -> 2.68). The unrounded amount is
   shown alongside.
5. A monthly total is rounded from the unrounded monthly sum, **not** summed
   from rounded daily figures, so the daily column may not add up to the
   monthly total by up to 0.01 per day (documented in the output).

## 5. Session lifecycle

`EnergyMonitor` owns one session:

| Event | Behavior |
|---|---|
| Start (`--record`) | new session, settings snapshot and sensor bindings stored |
| Samples | integrated; raw sample + bucket deltas + gaps + availability changes batched in memory |
| Every 10 s (sample time) | batch written in **one transaction** |
| Stop (duration, Ctrl+C) | final flush, status `Stopped` |
| OS suspend | chain broken, immediate flush, samples ignored until resume |
| OS resume | chain broken again; the sleep becomes a `Suspend` gap |
| Store write fails | logged; data kept and retried at next flush (raw samples capped at 3600, oldest dropped; bucket deltas always kept) |
| Process killed / crashes | at most the last ~10 s of unflushed data is lost. The session stays `Active` in the database |
| Next start / any DB command | if no process holds the monitor lock, `Active` sessions are orphans: marked `Interrupted`, ended at their **last persisted observation** (nothing after it is assumed) |
| Resume (`--record --resume [id]`) | reopens a `Stopped`/`Interrupted` session; time since its last observation becomes a `Paused`/`Interrupted` gap |

Only one process may record into a database at a time (`MonitorLock`: an
exclusively opened `<db>.lock` file that the OS releases when the process
dies -- which is how "crashed" is told apart from "still running").

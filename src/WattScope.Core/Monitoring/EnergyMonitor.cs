using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Pricing;

namespace WattScope.Core.Monitoring;

public sealed record EnergyMonitorOptions
{
    public IntegrationOptions Integration { get; init; } = new();

    /// <summary>How often (in sample monotonic time) pending data is written. Bounds data lost on a crash.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Raw samples kept in memory while the store is failing. Beyond this, the
    /// oldest raw samples are dropped (their energy is still in the bucket
    /// deltas, which are tiny and always retained).
    /// </summary>
    public int MaxPendingSamples { get; init; } = 3600;

    public bool PersistRawSamples { get; init; } = true;
}

public sealed record MonitorStatistics
{
    public long SamplesAccepted { get; init; }
    public long DuplicatesDiscarded { get; init; }
    public long SamplesIgnoredWhileSuspended { get; init; }
    public long GapsRecorded { get; init; }
    public long Flushes { get; init; }
    public long FlushFailures { get; init; }
    public long RawSamplesDropped { get; init; }
    public TimeSpan TotalFlushDuration { get; init; }
    public TimeSpan MaxFlushDuration { get; init; }
}

/// <summary>
/// Owns one monitoring session's lifecycle: integrates samples into energy,
/// detects gaps, batches everything, and flushes it to an
/// <see cref="IEnergyStore"/>. Thread-safe: samples arrive from the sampling
/// loop while suspend/resume notifications arrive from an OS event thread.
/// Store failures are logged and retried at the next flush; they never escape.
/// </summary>
public sealed class EnergyMonitor
{
    private readonly object _gate = new();
    private readonly IEnergyStore _store;
    private readonly EnergyMonitorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<EnergyMonitor>? _logger;
    private readonly EnergyIntegrator _integrator;
    private readonly EnergyAccumulator _accumulator = new();
    private readonly Func<ElectricityPriceSchedule>? _priceScheduleProvider;
    private ElectricityPriceSchedule? _prices;

    private readonly List<PowerSample> _pendingSamples = [];
    private readonly List<MonitoringGap> _pendingGaps = [];
    private readonly List<AvailabilityChange> _pendingAvailability = [];
    private readonly Dictionary<EnergyChannel, ReadingState> _lastState = [];

    private MonitoringSession? _session;
    private DateTimeOffset? _lastObservationUtc;
    private TimeSpan? _lastFlushMonotonic;
    private bool _suspended;
    private MonitorStatistics _stats = new();

    /// <param name="priceScheduleProvider">
    /// Supplies the electricity price schedule used to split intervals
    /// exactly at any price change that falls inside a bucket while it is
    /// being recorded (see docs/ENERGY_MODEL.md section 4). Read once at
    /// session start and re-read once per successful flush -- never per
    /// sample -- so it should be cheap (e.g. a caller-side cache), not a
    /// fresh database query on every call. Omit to keep the previous
    /// (bucket-start-price-only) behavior.
    /// </param>
    public EnergyMonitor(IEnergyStore store, EnergyMonitorOptions? options = null, TimeProvider? timeProvider = null, ILogger<EnergyMonitor>? logger = null, Func<ElectricityPriceSchedule>? priceScheduleProvider = null)
    {
        _store = store;
        _options = options ?? new EnergyMonitorOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _integrator = new EnergyIntegrator(_options.Integration);
        _priceScheduleProvider = priceScheduleProvider;
        _prices = priceScheduleProvider?.Invoke();
    }

    public MonitoringSession? Session { get { lock (_gate) return _session; } }

    /// <summary>Energy integrated by this process for the current session (excludes earlier runs of a resumed session).</summary>
    public EnergyTotals RunTotals { get { lock (_gate) return _accumulator.RunTotals; } }

    public MonitorStatistics Statistics { get { lock (_gate) return _stats; } }

    public MonitoringSession Start(IReadOnlyList<EnergySensorBinding> sensors, EstimationSettings estimation, bool? processElevated = null, string? appVersion = null)
    {
        estimation.Validate();
        lock (_gate)
        {
            if (_session is not null)
                throw new InvalidOperationException("A session is already running in this monitor.");

            _session = _store.CreateSession(new NewSessionRequest
            {
                StartedUtc = _time.GetUtcNow(),
                Estimation = estimation,
                Sensors = sensors,
                ProcessElevated = processElevated,
                AppVersion = appVersion
            });
            _logger?.LogInformation("Started monitoring session {SessionId}", _session.Id);
            return _session;
        }
    }

    /// <summary>
    /// Continues a Stopped or Interrupted session. The time since its last
    /// observation becomes a gap (reason Paused or Interrupted); no energy is
    /// attributed to it.
    /// </summary>
    public MonitoringSession Resume(long sessionId, IReadOnlyList<EnergySensorBinding> sensors)
    {
        lock (_gate)
        {
            if (_session is not null)
                throw new InvalidOperationException("A session is already running in this monitor.");

            var existing = _store.GetSession(sessionId)
                ?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
            if (existing.Status == SessionStatus.Active)
                throw new InvalidOperationException(
                    $"Session {sessionId} is still marked active. Recover interrupted sessions before resuming it.");

            var before = _store.ReopenSession(sessionId, sensors);
            var reason = before.Status == SessionStatus.Interrupted ? GapReason.Interrupted : GapReason.Paused;
            if (before.LastObservationUtc is { } last)
                _integrator.BreakChain(reason, last, $"Session resumed after status {before.Status}.");

            _session = before with { Status = SessionStatus.Active, EndedUtc = null, EndReason = null };
            _lastObservationUtc = before.LastObservationUtc;
            _logger?.LogInformation("Resumed monitoring session {SessionId} (was {Status})", sessionId, before.Status);
            return _session;
        }
    }

    public void OnSample(PowerSample sample)
    {
        lock (_gate)
        {
            var session = _session ?? throw new InvalidOperationException("No session is running.");

            if (_suspended)
            {
                _stats = _stats with { SamplesIgnoredWhileSuspended = _stats.SamplesIgnoredWhileSuspended + 1 };
                return;
            }

            var step = _integrator.Accept(sample);
            if (step.Outcome == IntegrationOutcome.DuplicateDiscarded)
            {
                _stats = _stats with { DuplicatesDiscarded = _stats.DuplicatesDiscarded + 1 };
                return;
            }

            if (step.Gap is { } gap)
            {
                _pendingGaps.Add(gap);
                _stats = _stats with { GapsRecorded = _stats.GapsRecorded + 1 };
                _logger?.LogWarning("Monitoring gap ({Reason}) from {Start:O} to {End:O}: {Detail}", gap.Reason, gap.StartUtc, gap.EndUtc, gap.Detail);
            }

            if (step.Interval is { } interval)
                _accumulator.Add(interval, session.Estimation, _prices);

            TrackAvailability(EnergyChannel.CpuPackage, sample.CpuPackage.State, sample.UtcTime);
            TrackAvailability(EnergyChannel.GpuBoard, sample.GpuBoard.State, sample.UtcTime);

            if (_options.PersistRawSamples)
            {
                _pendingSamples.Add(sample);
                if (_pendingSamples.Count > _options.MaxPendingSamples)
                {
                    var excess = _pendingSamples.Count - _options.MaxPendingSamples;
                    _pendingSamples.RemoveRange(0, excess);
                    _stats = _stats with { RawSamplesDropped = _stats.RawSamplesDropped + excess };
                }
            }

            _lastObservationUtc = sample.UtcTime;
            _stats = _stats with { SamplesAccepted = _stats.SamplesAccepted + 1 };

            _lastFlushMonotonic ??= sample.MonotonicTime;
            if (sample.MonotonicTime - _lastFlushMonotonic.Value >= _options.FlushInterval)
            {
                FlushLocked();
                _lastFlushMonotonic = sample.MonotonicTime;
            }
        }
    }

    /// <summary>The OS is about to suspend: persist what we have and ignore samples until <see cref="OnResume"/>.</summary>
    public void OnSuspend()
    {
        lock (_gate)
        {
            if (_session is null)
                return;
            _integrator.BreakChain(GapReason.Suspend, detail: "Operating system suspend/resume.");
            _suspended = true;
            FlushLocked();
        }
    }

    /// <summary>
    /// The OS resumed. Only meaningful after <see cref="OnSuspend"/>: Windows
    /// can raise Resume more than once for one sleep, and a repeated Resume
    /// must not cut a second gap out of normal, continuous sampling. If the
    /// matching suspend notification was missed entirely, the integrator's
    /// own elapsed-time checks (monotonic stall / clock discontinuity) still
    /// detect the discontinuity and record the gap -- this method is the
    /// precise path, not the only one.
    /// </summary>
    public void OnResume()
    {
        lock (_gate)
        {
            if (_session is null || !_suspended)
                return;
            _integrator.BreakChain(GapReason.Suspend, detail: "Operating system suspend/resume.");
            _suspended = false;
            _lastFlushMonotonic = null;
        }
    }

    /// <summary>Writes pending data. Returns false (and keeps the data for the next attempt) if the store failed.</summary>
    public bool Flush()
    {
        lock (_gate)
            return FlushLocked();
    }

    /// <summary>Flushes and marks the session Stopped. Returns false if the store could not be updated (the session will then be recovered as Interrupted later).</summary>
    public bool Stop(string reason = "Stopped")
    {
        lock (_gate)
        {
            if (_session is not { } session)
                return true;

            var flushed = FlushLocked();
            try
            {
                _store.EndSession(session.Id, _time.GetUtcNow(), SessionStatus.Stopped, reason);
                _logger?.LogInformation("Stopped monitoring session {SessionId}", session.Id);
                _session = null;
                return flushed;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to mark session {SessionId} stopped; it will be recovered as interrupted", session.Id);
                return false;
            }
        }
    }

    private void TrackAvailability(EnergyChannel channel, ReadingState state, DateTimeOffset atUtc)
    {
        if (_lastState.TryGetValue(channel, out var previous) && previous == state)
            return;
        _lastState[channel] = state;
        _pendingAvailability.Add(new AvailabilityChange(channel, state, atUtc));
    }

    private bool FlushLocked()
    {
        if (_session is not { } session)
            return true;

        var batch = new EnergyWriteBatch
        {
            SessionId = session.Id,
            Samples = _pendingSamples.ToList(),
            BucketDeltas = _accumulator.PendingDeltas(session.Id),
            PriceSplits = _accumulator.PendingPriceSplits(session.Id),
            Gaps = _pendingGaps.ToList(),
            AvailabilityChanges = _pendingAvailability.ToList(),
            LastObservationUtc = _lastObservationUtc
        };
        if (batch.IsEmpty)
            return true;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _store.WriteBatch(batch);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist monitoring data for session {SessionId}; will retry at next flush", session.Id);
            _stats = _stats with { FlushFailures = _stats.FlushFailures + 1 };
            return false;
        }

        var elapsed = stopwatch.Elapsed;
        _pendingSamples.Clear();
        _pendingGaps.Clear();
        _pendingAvailability.Clear();
        _accumulator.ClearPending();
        // Refreshed only once per flush (not per sample) to avoid unnecessary
        // polling; a price added mid-session is picked up within one flush
        // interval, same latency as everything else this loop persists.
        if (_priceScheduleProvider is not null)
            _prices = _priceScheduleProvider();
        _stats = _stats with
        {
            Flushes = _stats.Flushes + 1,
            TotalFlushDuration = _stats.TotalFlushDuration + elapsed,
            MaxFlushDuration = elapsed > _stats.MaxFlushDuration ? elapsed : _stats.MaxFlushDuration
        };
        return true;
    }
}

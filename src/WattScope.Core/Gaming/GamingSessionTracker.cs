using Microsoft.Extensions.Logging;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;

namespace WattScope.Core.Gaming;

/// <summary>The small, UI-facing view of the current gaming context. <c>State</c> is "candidate", "active" or "ending".</summary>
public sealed record GamingLiveState
{
    public required string State { get; init; }
    public long? SessionId { get; init; }
    public required string GameName { get; init; }
    public required string ExeName { get; init; }
    public required bool IsManual { get; init; }
    public required GameDetectionSource Source { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required double DurationSeconds { get; init; }
    public required double MonitoredSeconds { get; init; }
    public required EstimateCompleteness Completeness { get; init; }

    /// <summary>Estimated system energy so far in kWh: the complete estimate, or a lower bound when <see cref="SystemIsLowerBound"/>.</summary>
    public required double SystemKwh { get; init; }
    public required bool SystemIsLowerBound { get; init; }
    public required GamingCostStatus CostStatus { get; init; }
    public decimal? Cost { get; init; }
    public string? Currency { get; init; }
}

/// <summary>
/// Joins the three pieces of gaming analytics, and is the only place they
/// meet: the <see cref="GameDetector"/> (which process is a game, as a state
/// machine), the energy monitor's verified intervals (as an
/// <see cref="IMonitorObserver"/>), and persistence (<see cref="IGamingStore"/>).
///
/// <b>Energy attribution.</b> Every interval the monitor integrates is
/// clipped to the time the current gaming context was in force and added to
/// that context's <see cref="GamingLedger"/>. Intervals that are gaps (sleep,
/// stalls, a stopped monitor) were never intervals, so they can never reach a
/// ledger. A candidate accumulates in memory from first sight, and keeps that
/// energy if it is confirmed (so a session starts when the game started, not
/// when it was confirmed) or discards it if the candidate vanishes. Energy
/// during the end grace period goes to a separate tail that is merged back if
/// the game returns and dropped if it does not.
///
/// <b>Threading.</b> Samples arrive on the monitor's thread, process
/// snapshots on a poller thread, requests on a third; one lock serializes the
/// tracker. It owns its store connection and is the only user of it.
/// Failures are logged and never escape into monitoring.
/// </summary>
public sealed class GamingSessionTracker : IMonitorObserver
{
    private readonly object _gate = new();
    private readonly IGamingStore _store;
    private readonly GameDetector _detector;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly TimeSpan _flushInterval;

    private long? _monitoringSessionId;
    private EstimationSettings _settings = new();
    private Func<ElectricityPriceSchedule>? _pricesProvider;
    private ElectricityPriceSchedule _prices = ElectricityPriceSchedule.Empty;

    private GamingLedger? _main;
    private GamingLedger? _tail;
    private GamingLedger? _target;
    private GamingLedger? _previousTarget;
    private DateTimeOffset _targetFromUtc;
    private long? _gamingSessionId;
    private DateTimeOffset _startedUtc;

    private TimeSpan? _lastFlushMonotonic;
    private DateTimeOffset _lastPresentUtc;
    private DateTimeOffset? _suspendedAtUtc;
    private DateTimeOffset _lastSampleUtc;

    public GamingSessionTracker(IGamingStore store, GameDetector detector, TimeProvider? timeProvider = null, ILogger? logger = null, TimeSpan? flushInterval = null)
    {
        _store = store;
        _detector = detector;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(10);
    }

    public GameDetector Detector => _detector;

    /// <summary>The persisted id of the running gaming session, if one is confirmed.</summary>
    public long? ActiveGamingSessionId { get { lock (_gate) return _gamingSessionId; } }

    // ------------------------------------------------------------- lifecycle

    /// <summary>
    /// Starts attributing to <paramref name="monitoringSessionId"/>. If a game
    /// session is still open from an earlier attachment to a <em>different</em>
    /// monitoring session it is ended first; one carried across an interval
    /// change (same monitoring session) simply continues.
    /// </summary>
    public void Attach(long monitoringSessionId, EstimationSettings settings, Func<ElectricityPriceSchedule> prices)
    {
        lock (_gate)
        {
            if (_monitoringSessionId is { } previous && previous != monitoringSessionId)
                Apply(_detector.Reset(_time.GetUtcNow(), GamingEndReason.MonitoringStopped));

            _monitoringSessionId = monitoringSessionId;
            _settings = settings;
            _pricesProvider = prices;
            _prices = Safe(prices);
        }
    }

    /// <summary>
    /// Monitoring stopped. By default an open game session ends (reason
    /// MonitoringStopped). <paramref name="keepContext"/> keeps it for a
    /// restart of the same monitoring session (an interval change): nothing is
    /// integrated meanwhile (the restart is a recorded gap), so no energy is
    /// attributed to the pause.
    /// </summary>
    public void Detach(bool keepContext = false)
    {
        lock (_gate)
        {
            if (_monitoringSessionId is null)
                return;
            if (keepContext && _detector.Phase is DetectorPhase.Confirmed or DetectorPhase.EndingGrace)
            {
                if (_gamingSessionId is not null && _main is not null)
                    PersistLocked(_lastSampleUtc == default ? _time.GetUtcNow() : _lastSampleUtc);
                return;
            }
            Apply(_detector.Reset(_time.GetUtcNow(), GamingEndReason.MonitoringStopped));
            _monitoringSessionId = null;
        }
    }

    public bool IsAttached { get { lock (_gate) return _monitoringSessionId is not null; } }

    // ------------------------------------------------------------- inputs

    /// <summary>One process snapshot from the poller. No-op unless attached to a monitoring session.</summary>
    public void ObserveProcesses(IReadOnlyList<ProcessInfo> processes) => ObserveProcesses(_time.GetUtcNow(), processes);

    public void ObserveProcesses(DateTimeOffset now, IReadOnlyList<ProcessInfo> processes)
    {
        lock (_gate)
        {
            if (_monitoringSessionId is null)
                return;
            Apply(_detector.Observe(now, processes));
            if (_detector.Phase == DetectorPhase.Confirmed)
                _lastPresentUtc = now;
        }
    }

    public void OnSample(PowerSample sample, IntegrationStep step)
    {
        lock (_gate)
        {
            if (_monitoringSessionId is null)
                return;
            _lastSampleUtc = sample.UtcTime;
            if (_target is null)
                return;

            var ledger = sample.UtcTime >= _targetFromUtc ? _target : _previousTarget;
            ledger?.AddSample(sample, _settings);

            if (step.Interval is { } interval)
            {
                foreach (var piece in interval.SplitAt([_targetFromUtc]))
                {
                    var owner = piece.StartUtc >= _targetFromUtc ? _target : _previousTarget;
                    owner?.AddInterval(piece, _settings, _prices);
                }
            }

            if (_gamingSessionId is not null && _detector.Phase == DetectorPhase.Confirmed)
            {
                _lastFlushMonotonic ??= sample.MonotonicTime;
                if (sample.MonotonicTime - _lastFlushMonotonic.Value >= _flushInterval)
                {
                    PersistLocked(sample.UtcTime);
                    _lastFlushMonotonic = sample.MonotonicTime;
                }
            }
        }
    }

    public void OnSuspended()
    {
        lock (_gate)
        {
            if (_monitoringSessionId is null)
                return;
            _suspendedAtUtc = _time.GetUtcNow();
            // Presence of a half-confirmed candidate across a sleep proves nothing about how long it ran.
            Apply(_detector.DropCandidate(_suspendedAtUtc.Value));
            if (_gamingSessionId is not null && _main is not null && _detector.Phase == DetectorPhase.Confirmed)
                PersistLocked(_lastSampleUtc == default ? _suspendedAtUtc.Value : _lastSampleUtc);
        }
    }

    public void OnResumed()
    {
        // Nothing to attribute: the monitor already cut a Suspend gap, so no energy exists for the sleep.
        // The next process poll decides whether the game is still there.
    }

    // ------------------------------------------------------------- user actions

    /// <summary>Starts a manual session. Refused while a game session is running. Needs an attached monitoring session.</summary>
    public GamingLiveState StartManual(string displayName, string? exeName)
    {
        lock (_gate)
        {
            if (_monitoringSessionId is null)
                throw new InvalidOperationException("Monitoring is not running; a gaming session needs a running monitoring session.");
            Apply(_detector.StartManual(_time.GetUtcNow(), displayName, exeName));
            return LiveLocked() ?? throw new InvalidOperationException("The gaming session could not be started; see the log for the reason.");
        }
    }

    public void StopManual()
    {
        lock (_gate)
            Apply(_detector.StopManual(_time.GetUtcNow()));
    }

    public void ApplyMappings(IReadOnlyList<GameMapping> mappings)
    {
        lock (_gate)
            Apply(_detector.ApplyMappings(_time.GetUtcNow(), mappings));
    }

    // ------------------------------------------------------------- read side

    /// <summary>The current gaming context for the UI, or null when there is none.</summary>
    public GamingLiveState? Live()
    {
        lock (_gate)
            return LiveLocked();
    }

    private GamingLiveState? LiveLocked()
    {
        if (_detector.Context is not { } context || _monitoringSessionId is null)
            return null;
        var now = _time.GetUtcNow();

        if (_detector.Phase == DetectorPhase.Candidate)
        {
            return new GamingLiveState
            {
                State = "candidate",
                GameName = context.Identity.DisplayName,
                ExeName = context.Identity.ExeName,
                IsManual = false,
                Source = context.Identity.Source,
                StartedUtc = context.FirstSeenUtc,
                DurationSeconds = Math.Max(0, (now - context.FirstSeenUtc).TotalSeconds),
                MonitoredSeconds = 0,
                Completeness = EstimateCompleteness.None,
                SystemKwh = 0,
                SystemIsLowerBound = true,
                CostStatus = GamingCostStatus.NoEstimate
            };
        }

        var record = new GamingSessionRecord
        {
            Id = _gamingSessionId ?? 0,
            MonitoringSessionId = _monitoringSessionId.Value,
            GameKey = context.Identity.Key,
            ExeName = context.Identity.ExeName,
            ExePath = context.Identity.ExePath,
            DisplayName = context.Identity.DisplayName,
            Source = context.Identity.Source,
            IsManual = context.IsManual,
            StartedUtc = context.FirstSeenUtc,
            Status = GamingSessionStatus.Active,
            Peaks = _main?.Peaks ?? new GamingPeaks(),
            Energy = _main?.Rows() ?? []
        };
        var summary = GamingAnalytics.Summarize(record, _prices, now);
        return new GamingLiveState
        {
            State = _detector.Phase == DetectorPhase.EndingGrace ? "ending" : "active",
            SessionId = _gamingSessionId,
            GameName = context.Identity.DisplayName,
            ExeName = context.Identity.ExeName,
            IsManual = context.IsManual,
            Source = context.Identity.Source,
            StartedUtc = context.FirstSeenUtc,
            DurationSeconds = summary.DurationSeconds,
            MonitoredSeconds = summary.MonitoredSeconds,
            Completeness = summary.Completeness,
            SystemKwh = (double)EnergyUnits.ToKwh(summary.SystemJoules),
            SystemIsLowerBound = summary.SystemIsLowerBound,
            CostStatus = summary.CostStatus,
            Cost = summary.Cost,
            Currency = summary.Currency
        };
    }

    // ------------------------------------------------------------- transitions

    private void Apply(IReadOnlyList<DetectorEvent> events)
    {
        foreach (var e in events)
        {
            try
            {
                Handle(e);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Handling gaming event {Kind} failed; gaming analytics for this session may be incomplete.", e.Kind);
            }
        }
    }

    private void Handle(DetectorEvent e)
    {
        switch (e.Kind)
        {
            case DetectorEventKind.CandidateStarted:
                _main = new GamingLedger();
                SwitchTarget(_main, e.At);
                _previousTarget = null;
                break;

            case DetectorEventKind.CandidateRejected:
                _main = null;
                SwitchTarget(null, e.At);
                _previousTarget = null;
                break;

            case DetectorEventKind.SessionConfirmed:
                Confirm(e);
                break;

            case DetectorEventKind.GraceStarted:
                if (_gamingSessionId is not null)
                    PersistLocked(e.At);
                _tail = new GamingLedger();
                SwitchTarget(_tail, e.At);
                break;

            case DetectorEventKind.GraceResumed:
                if (_main is not null && _tail is not null)
                    _main.Merge(_tail);
                _tail = null;
                SwitchTarget(_main, e.At);
                // Energy straddling the instant of return belongs to the session too (the grace period is part of it now).
                _previousTarget = _main;
                break;

            case DetectorEventKind.SessionEnded:
                EndSession(e);
                break;

            case DetectorEventKind.NameChanged:
                if (_gamingSessionId is { } id)
                    _store.RenameGamingSession(id, e.Context.Identity.DisplayName);
                break;
        }
    }

    private void Confirm(DetectorEvent e)
    {
        if (e.Context.IsManual)
        {
            _main = new GamingLedger();
            SwitchTarget(_main, e.At);
            _previousTarget = null;
        }

        _startedUtc = e.Context.FirstSeenUtc;
        try
        {
            _gamingSessionId = _store.CreateGamingSession(new NewGamingSession
            {
                MonitoringSessionId = _monitoringSessionId!.Value,
                Game = e.Context.Identity,
                IsManual = e.Context.IsManual,
                StartedUtc = e.Context.FirstSeenUtc
            });
        }
        catch (Exception ex)
        {
            // Could not record it (for example another gaming session is somehow still active in the database):
            // abandon the context rather than keep integrating into a session that does not exist.
            _logger?.LogError(ex, "Could not create the gaming session for {Game}; abandoning it.", e.Context.Identity.DisplayName);
            _gamingSessionId = null;
            _main = null;
            SwitchTarget(null, e.At);
            _previousTarget = null;
            _detector.Reset(e.At);
            return;
        }

        _lastFlushMonotonic = null;
        _lastPresentUtc = e.At;
        PersistLocked(e.At);
        _logger?.LogInformation("Gaming session {Id} started: {Game} ({Source})", _gamingSessionId, e.Context.Identity.DisplayName, e.Context.Identity.Source);
    }

    private void EndSession(DetectorEvent e)
    {
        var endedUtc = e.EndedUtc ?? e.At;
        // The process was alive when the machine went to sleep; it was not necessarily alive when we woke.
        if (e.EndReason == GamingEndReason.ProcessExited && _suspendedAtUtc is { } suspended && suspended >= _lastPresentUtc && suspended < endedUtc)
            endedUtc = suspended;
        if (endedUtc < _startedUtc)
            endedUtc = _startedUtc;

        if (_gamingSessionId is { } id)
        {
            try
            {
                if (_main is not null)
                    _store.SaveGamingProgress(id, endedUtc, _main.Peaks, _main.Rows());
                _store.EndGamingSession(id, endedUtc, GamingSessionStatus.Ended, e.EndReason ?? GamingEndReason.ProcessExited);
                _logger?.LogInformation("Gaming session {Id} ended ({Reason})", id, e.EndReason);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Could not close gaming session {Id}; it will be recovered as interrupted.", id);
            }
        }

        _gamingSessionId = null;
        _main = null;
        _tail = null;
        SwitchTarget(null, e.At);
        _previousTarget = null;
        _suspendedAtUtc = null;
    }

    private void SwitchTarget(GamingLedger? next, DateTimeOffset at)
    {
        _previousTarget = _target;
        _target = next;
        _targetFromUtc = at;
    }

    private void PersistLocked(DateTimeOffset lastObservationUtc)
    {
        if (_gamingSessionId is not { } id || _main is null)
            return;
        try
        {
            _store.SaveGamingProgress(id, lastObservationUtc, _main.Peaks, _main.Rows());
            if (_pricesProvider is not null)
                _prices = Safe(_pricesProvider); // re-read once per flush, never per sample (same cadence as the monitor)
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not save progress of gaming session {Id}; will retry at the next flush.", id);
        }
    }

    private ElectricityPriceSchedule Safe(Func<ElectricityPriceSchedule> provider)
    {
        try { return provider(); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the price schedule; keeping the previous one.");
            return _prices;
        }
    }
}

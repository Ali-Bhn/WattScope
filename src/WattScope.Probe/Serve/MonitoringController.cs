using Microsoft.Extensions.Logging;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using WattScope.Storage;

namespace WattScope.Probe.Serve;

/// <summary>One sample plus its position in the current run (sequence restarts at 0 each run), and how long reading all providers took when known.</summary>
public readonly record struct SampleEnvelope(long Sequence, PowerSample Sample, TimeSpan? ReadDuration = null);

/// <summary>
/// Where samples come from. Production wraps the sensor providers and
/// <c>PowerSampleBuilder</c>; tests supply synthetic samples. Keeps the
/// controller's lifecycle logic testable without hardware.
/// </summary>
public interface ISampleSource
{
    IAsyncEnumerable<SampleEnvelope> RunAsync(TimeSpan interval, CancellationToken cancellationToken);
}

/// <summary>What the UI needs to attach to whatever is already running (it never has to infer this from ticks).</summary>
/// <summary>Cheap running statistics for the current run: provider-read latency and database-flush latency.</summary>
public sealed record MonitoringDiagnostics
{
    public long Samples { get; init; }
    public double ReadAvgMs { get; init; }
    public double ReadMaxMs { get; init; }
    public long Flushes { get; init; }
    public long FlushFailures { get; init; }
    public double FlushAvgMs { get; init; }
    public double FlushMaxMs { get; init; }
    public long GapsRecorded { get; init; }
}

public sealed record MonitoringSnapshot
{
    public MonitoringDiagnostics? Diagnostics { get; init; }

    /// <summary>"idle" (process up, not monitoring) or "recording".</summary>
    public required string State { get; init; }
    public long? SessionId { get; init; }
    public required bool Elevated { get; init; }
    public required bool CpuAvailable { get; init; }
    public required bool GpuAvailable { get; init; }
    public required int IntervalMs { get; init; }
    public TickMessage? LastTick { get; init; }

    /// <summary>True when a start request found monitoring already running and changed nothing.</summary>
    public bool AlreadyRunning { get; init; }
}

public sealed record MonitoringControllerOptions
{
    public required bool Elevated { get; init; }
    public required bool CpuAvailable { get; init; }
    public required bool GpuAvailable { get; init; }
    public string? AppVersion { get; init; }

    /// <summary>Used only when no interval was requested and none is stored.</summary>
    public int DefaultIntervalMs { get; init; } = SqliteEnergyStore.DefaultMonitoringIntervalMs;

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The single authority for "is monitoring running, and in which session".
/// It is deliberately independent of the process, the window and the UI:
/// the sidecar process can be up while monitoring is stopped (idle), and
/// monitoring runs on its own thread-pool task regardless of whether
/// anything is rendering.
///
/// At most one run exists at a time (serialized by <see cref="_gate"/>), and
/// the database refuses a second Active session on its own
/// (<c>ux_session_single_active</c>) as the backstop. The controller owns the
/// recording store; callers must not touch it.
/// </summary>
public sealed class MonitoringController : IAsyncDisposable
{
    private sealed class Run
    {
        public required EnergyMonitor Monitor { get; init; }
        public required MonitoringSession Session { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required int IntervalMs { get; init; }
        public Task Loop { get; set; } = Task.CompletedTask;
        public long Reads;
        public double ReadSumMs;
        public double ReadMaxMs;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteEnergyStore _store;
    private readonly ISampleSource _source;
    private readonly MonitoringControllerOptions _options;
    private readonly Action<object> _emit;
    private readonly ILogger _logger;
    private readonly GamingService? _gaming;
    private Run? _run;
    private TickMessage? _lastTick;
    private int _lastIntervalMs;

    private readonly IReadOnlyList<EnergySensorBinding> _bindingList;

    /// <param name="bindings">The channel-to-sensor bindings fixed once from discovery; stored with each session.</param>
    public MonitoringController(SqliteEnergyStore recordingStore, ISampleSource source, IReadOnlyList<EnergySensorBinding> bindings, MonitoringControllerOptions options, Action<object> emit, ILogger logger, GamingService? gaming = null)
    {
        _gaming = gaming;
        _store = recordingStore;
        _source = source;
        _bindingList = bindings;
        _options = options;
        _emit = emit;
        _logger = logger;
        _lastIntervalMs = _store.GetMonitoringIntervalMs();
    }

    public bool IsRunning { get { lock (_stateLock) return _run is not null; } }
    private readonly object _stateLock = new();

    public MonitoringSnapshot Snapshot(bool alreadyRunning = false)
    {
        lock (_stateLock)
        {
            return new MonitoringSnapshot
            {
                State = _run is null ? "idle" : "recording",
                SessionId = _run?.Session.Id,
                Elevated = _options.Elevated,
                CpuAvailable = _options.CpuAvailable,
                GpuAvailable = _options.GpuAvailable,
                IntervalMs = _run?.IntervalMs ?? _lastIntervalMs,
                LastTick = _run is null ? null : _lastTick,
                Diagnostics = _run is null ? null : DiagnosticsFor(_run),
                AlreadyRunning = alreadyRunning
            };
        }
    }

    private static MonitoringDiagnostics DiagnosticsFor(Run run)
    {
        var stats = run.Monitor.Statistics;
        return new MonitoringDiagnostics
        {
            Samples = stats.SamplesAccepted,
            ReadAvgMs = run.Reads > 0 ? run.ReadSumMs / run.Reads : 0,
            ReadMaxMs = run.ReadMaxMs,
            Flushes = stats.Flushes,
            FlushFailures = stats.FlushFailures,
            FlushAvgMs = stats.Flushes > 0 ? stats.TotalFlushDuration.TotalMilliseconds / stats.Flushes : 0,
            FlushMaxMs = stats.MaxFlushDuration.TotalMilliseconds,
            GapsRecorded = stats.GapsRecorded
        };
    }

    /// <summary>
    /// Starts monitoring if it is not already running; otherwise changes
    /// nothing and says so (idempotent -- a duplicate start from the tray, a
    /// reopened window or a retry can never create a second session).
    /// </summary>
    /// <param name="intervalMs">Explicit interval; null uses the stored setting.</param>
    public async Task<MonitoringSnapshot> StartAsync(bool resume = false, long? resumeSessionId = null, int? intervalMs = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_run is not null)
                return Snapshot(alreadyRunning: true);

            var interval = intervalMs ?? _store.GetMonitoringIntervalMs();
            if (interval is < SqliteEnergyStore.MinMonitoringIntervalMs or > SqliteEnergyStore.MaxMonitoringIntervalMs)
                throw new ArgumentOutOfRangeException(nameof(intervalMs), interval,
                    $"Monitoring interval must be between {SqliteEnergyStore.MinMonitoringIntervalMs} and {SqliteEnergyStore.MaxMonitoringIntervalMs} ms.");

            // This process holds the recording lock, so any Active session
            // is an orphan of a recorder that died: close it before opening ours.
            foreach (var recovered in _store.RecoverInterruptedSessions())
                _emit(new StatusMessage { State = "recovered", SessionId = recovered.Id, Message = $"Previous recording ended unexpectedly; kept up to {(recovered.LastObservationUtc ?? recovered.StartedUtc):O}." });

            var timeSpan = TimeSpan.FromMilliseconds(interval);
            var monitor = new EnergyMonitor(_store,
                new EnergyMonitorOptions { Integration = IntegrationOptions.ForSamplingInterval(timeSpan), FlushInterval = _options.FlushInterval },
                logger: null,
                priceScheduleProvider: _store.GetPriceSchedule,
                observer: _gaming?.Tracker);

            MonitoringSession session;
            try
            {
                session = resume
                    ? monitor.Resume(resumeSessionId ?? _store.GetLatestSession()?.Id ?? throw new InvalidOperationException("There is no session to resume."), _bindingList)
                    : monitor.Start(_bindingList, _store.GetEstimationSettings(), _options.Elevated, _options.AppVersion);
            }
            catch (InvalidOperationException ex)
            {
                _emit(new StatusMessage { State = "error", Message = ex.Message });
                throw;
            }

            var run = new Run { Monitor = monitor, Session = session, Cts = new CancellationTokenSource(), IntervalMs = interval };
            _gaming?.Attach(session);
            var displayPrices = _store.GetPriceSchedule();
            lock (_stateLock)
            {
                _run = run;
                _lastTick = null;
                _lastIntervalMs = interval;
            }

            _emit(new StatusMessage
            {
                State = "recording",
                SessionId = session.Id,
                Elevated = _options.Elevated,
                CpuAvailable = _options.CpuAvailable,
                GpuAvailable = _options.GpuAvailable
            });

            run.Loop = Task.Run(() => SampleLoopAsync(run, timeSpan, displayPrices));
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops monitoring (flushes and marks the session Stopped). A no-op when idle.</summary>
    /// <param name="keepGamingSession">True only for an interval change, which restarts the run inside the same monitoring session: an open gaming session is then kept instead of ended.</param>
    public async Task<MonitoringSnapshot> StopAsync(string reason = "Stopped by user", bool keepGamingSession = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLockedAsync(reason, keepGamingSession).ConfigureAwait(false);
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopLockedAsync(string reason, bool keepGamingSession = false)
    {
        Run? run;
        lock (_stateLock) run = _run;
        if (run is null)
            return;

        run.Cts.Cancel();
        try { await run.Loop.ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Sampling loop ended with an error while stopping."); }

        // Gaming first: its session is attributed to this monitoring session and ends with it.
        _gaming?.Detach(keepGamingSession);
        var saved = run.Monitor.Stop(reason);
        lock (_stateLock)
        {
            _run = null;
            _lastTick = null;
        }
        run.Cts.Dispose();
        _emit(new StatusMessage
        {
            State = "stopped",
            SessionId = run.Session.Id,
            Message = saved ? null : "The session could not be fully saved/closed; it will be recovered as interrupted on the next start."
        });
    }

    /// <summary>OS suspend notification: forwarded to the running monitor only (idle has nothing to protect).</summary>
    public void OnSuspend()
    {
        Run? run;
        lock (_stateLock) run = _run;
        if (run is null)
            return;
        run.Monitor.OnSuspend();
        _emit(new StatusMessage { State = "suspended", SessionId = run.Session.Id });
    }

    public void OnResume()
    {
        Run? run;
        lock (_stateLock) run = _run;
        if (run is null)
            return;
        run.Monitor.OnResume();
        _emit(new StatusMessage { State = "resumed", SessionId = run.Session.Id });
    }

    private async Task SampleLoopAsync(Run run, TimeSpan interval, ElectricityPriceSchedule displayPrices)
    {
        try
        {
            await foreach (var envelope in _source.RunAsync(interval, run.Cts.Token).ConfigureAwait(false))
            {
                run.Monitor.OnSample(envelope.Sample);
                var tick = BuildTick(run, envelope, displayPrices);
                lock (_stateLock)
                {
                    _lastTick = tick;
                    if (envelope.ReadDuration is { } read)
                    {
                        var ms = read.TotalMilliseconds;
                        run.Reads++;
                        run.ReadSumMs += ms;
                        if (ms > run.ReadMaxMs) run.ReadMaxMs = ms;
                    }
                }
                _emit(tick);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            // A failing sample source must not leave a session marked Active
            // with nothing feeding it: surface it, and go idle.
            _logger.LogError(ex, "Sampling failed; monitoring stopped.");
            _emit(new StatusMessage { State = "error", SessionId = run.Session.Id, Message = $"Sampling failed: {ex.Message}" });
            _ = Task.Run(async () =>
            {
                try { await StopAsync("Sampling failed").ConfigureAwait(false); }
                catch (Exception stopEx) { _logger.LogError(stopEx, "Stopping after a sampling failure also failed."); }
            });
        }
    }

    private TickMessage BuildTick(Run run, SampleEnvelope envelope, ElectricityPriceSchedule prices)
    {
        var sample = envelope.Sample;
        var estimation = run.Session.Estimation;
        var wall = WallPowerEstimator.EstimateWatts(sample, estimation);
        var totals = run.Monitor.RunTotals;
        var completeness = EnergyReportBuilder.CompletenessOf(totals);
        var price = prices.PriceAt(sample.UtcTime);

        return new TickMessage
        {
            SessionId = run.Session.Id,
            Sequence = envelope.Sequence,
            Utc = sample.UtcTime,
            CpuWatts = sample.CpuPackage.IsValid ? sample.CpuPackage.Watts : null,
            CpuState = sample.CpuPackage.State,
            GpuWatts = sample.GpuBoard.IsValid ? sample.GpuBoard.Watts : null,
            GpuState = sample.GpuBoard.State,
            OtherComponentsWatts = estimation.OtherComponentsWatts,
            EstimatedTotalWatts = wall.Value,
            EstimateCompleteness = wall.Completeness,
            SessionCompleteness = completeness,
            SessionSeconds = totals.MonitoredSeconds,
            SessionKwh = (double)EnergyUnits.ToKwh(totals.EstimatedCompleteJoules),
            // A running cost is only shown for a session whose estimate is
            // Complete. A partial/none session has no complete wall estimate
            // to price; showing the (tiny or zero) complete-only figure would
            // read as "this session cost ~nothing", which is false.
            SessionCostApprox = completeness == EstimateCompleteness.Complete && price is not null
                ? CostCalculator.Cost(totals.EstimatedCompleteJoules, price.PricePerKwh)
                : null,
            Currency = prices.Currency,
            Elevated = _options.Elevated,
            Gaming = _gaming?.Tracker.Live()
        };
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync("Stopped (shutdown)").ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stopping during dispose failed."); }
        _gate.Dispose();
    }
}


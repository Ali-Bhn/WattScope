using Microsoft.Extensions.Logging;
using WattScope.Core.Estimation;
using WattScope.Core.Gaming;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Storage;

namespace WattScope.Probe.Serve;

/// <summary>
/// Hosts game detection inside the sidecar (never in the UI): owns the gaming
/// store connection, the <see cref="GamingSessionTracker"/> and the slow
/// process poll that feeds it. The poll runs only while a monitoring session is
/// attached -- a gaming session is energy attributed to monitored time, so
/// detecting games while nothing is monitored would produce sessions with no
/// data.
///
/// The poll interval is the only polling there is (default 5 s, one toolhelp
/// snapshot per poll; docs/GAMING.md "Performance"). There is no event
/// subscription, WMI query, filesystem scan or executable hashing.
/// </summary>
public sealed class GamingService : IDisposable
{
    private readonly object _pollGate = new();
    private readonly SqliteEnergyStore _store;
    private readonly IProcessSource _processes;
    private readonly GameDetectorOptions _options;
    private readonly ILogger _logger;
    private CancellationTokenSource? _pollCts;
    private Task _pollTask = Task.CompletedTask;

    /// <param name="gamingStore">A connection used only by the tracker (and this service while holding the tracker's lock).</param>
    public GamingService(SqliteEnergyStore gamingStore, IProcessSource processes, IExecutableMetadata? metadata, GameDetectorOptions? options, ILogger logger, TimeProvider? timeProvider = null)
    {
        _store = gamingStore;
        _processes = processes;
        _options = options ?? new GameDetectorOptions();
        _logger = logger;
        var detector = new GameDetector(_options, new GameIdentifier(metadata));
        Tracker = new GamingSessionTracker(gamingStore, detector, timeProvider, logger);
        Tracker.ApplyMappings(gamingStore.GetGameMappings());
    }

    public GamingSessionTracker Tracker { get; }

    public GameDetectorOptions Options => _options;

    /// <summary>The price schedule read through the tracker's own connection (the recording connection belongs to the energy monitor).</summary>
    public Func<ElectricityPriceSchedule> PriceProvider => _store.GetPriceSchedule;

    public void Attach(MonitoringSession session)
    {
        Tracker.Attach(session.Id, session.Estimation, PriceProvider);
        StartPolling();
    }

    /// <param name="keepContext">True for a restart of the same monitoring session (interval change): an open game session continues.</param>
    public void Detach(bool keepContext)
    {
        StopPolling();
        Tracker.Detach(keepContext);
    }

    /// <summary>A one-off process snapshot for the "mark a running program as a game" picker. On demand only; never pushed.</summary>
    public IReadOnlyList<ProcessInfo> RunningProcesses()
    {
        lock (_pollGate)
            return _processes.Snapshot();
    }

    private void StartPolling()
    {
        lock (_pollGate)
        {
            if (_pollCts is not null)
                return;
            var cts = new CancellationTokenSource();
            _pollCts = cts;
            _pollTask = Task.Run(() => PollLoopAsync(cts.Token));
        }
    }

    private void StopPolling()
    {
        Task task;
        CancellationTokenSource? cts;
        lock (_pollGate)
        {
            cts = _pollCts;
            task = _pollTask;
            _pollCts = null;
        }
        if (cts is null)
            return;
        cts.Cancel();
        try { task.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* a failed or cancelled poll is not a failed stop */ }
        cts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            do
            {
                try
                {
                    IReadOnlyList<ProcessInfo> snapshot;
                    lock (_pollGate)
                        snapshot = _processes.Snapshot();
                    Tracker.ObserveProcesses(snapshot);
                }
                catch (Exception ex)
                {
                    // One bad poll must not end detection (and never touches monitoring).
                    _logger.LogWarning(ex, "Game detection poll failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Detached.
        }
    }

    public void Dispose() => StopPolling();
}

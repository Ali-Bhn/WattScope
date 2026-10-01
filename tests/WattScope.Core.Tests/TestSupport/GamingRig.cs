using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Gaming;
using WattScope.Core.Pricing;

namespace WattScope.Core.Tests.TestSupport;

/// <summary>Hand-driven clock for the tracker (it reads "now" only for attach/detach/manual actions and live views).</summary>
public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = Synthetic.BaseUtc;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An in-memory <see cref="IGamingStore"/> that enforces the same single-active rule the SQLite one does.</summary>
public sealed class InMemoryGamingStore : IGamingStore
{
    public List<GamingSessionRecord> Sessions { get; } = [];
    public int ProgressSaves { get; private set; }
    public bool FailCreate { get; set; }
    private long _nextId = 1;

    public long CreateGamingSession(NewGamingSession session)
    {
        if (FailCreate)
            throw new InvalidOperationException("simulated create failure");
        if (Sessions.Any(s => s.Status == GamingSessionStatus.Active))
            throw new GamingSessionExistsException();
        var id = _nextId++;
        Sessions.Add(new GamingSessionRecord
        {
            Id = id,
            MonitoringSessionId = session.MonitoringSessionId,
            GameKey = session.Game.Key,
            ExeName = session.Game.ExeName,
            ExePath = session.Game.ExePath,
            DisplayName = session.Game.DisplayName,
            Source = session.IsManual ? GameDetectionSource.Manual : session.Game.Source,
            IsManual = session.IsManual,
            StartedUtc = session.StartedUtc,
            LastObservationUtc = session.StartedUtc,
            Status = GamingSessionStatus.Active,
            Peaks = new GamingPeaks(),
            Energy = []
        });
        return id;
    }

    public void SaveGamingProgress(long id, DateTimeOffset lastObservationUtc, GamingPeaks peaks, IReadOnlyList<GamingEnergyRow> energy)
    {
        ProgressSaves++;
        Update(id, s => s with { LastObservationUtc = lastObservationUtc, Peaks = peaks, Energy = energy.ToList() });
    }

    public void EndGamingSession(long id, DateTimeOffset endedUtc, GamingSessionStatus status, GamingEndReason reason) =>
        Update(id, s => s with { Status = status, EndedUtc = endedUtc, EndReason = reason });

    public void RenameGamingSession(long id, string displayName) => Update(id, s => s with { DisplayName = displayName });

    public GamingSessionRecord Get(long id) => Sessions.Single(s => s.Id == id);

    private void Update(long id, Func<GamingSessionRecord, GamingSessionRecord> change)
    {
        var index = Sessions.FindIndex(s => s.Id == id);
        Sessions[index] = change(Sessions[index]);
    }
}

/// <summary>
/// A deterministic gaming-analytics rig: synthetic process lists, synthetic power samples run through the
/// <em>real</em> <see cref="EnergyIntegrator"/>, the real detector and tracker, and an in-memory store.
/// Nothing here needs a game, hardware, a database or a wall clock.
/// </summary>
public sealed class GamingRig
{
    public GamingRig(GameDetectorOptions? options = null, ElectricityPriceSchedule? prices = null, EstimationSettings? settings = null)
    {
        Detector = new GameDetector(options ?? new GameDetectorOptions(), new GameIdentifier());
        Tracker = new GamingSessionTracker(Store, Detector, Time);
        Prices = prices ?? ElectricityPriceSchedule.Empty;
        Settings = settings ?? new EstimationSettings();
    }

    public ManualTime Time { get; } = new();
    public InMemoryGamingStore Store { get; } = new();
    public GameDetector Detector { get; }
    public GamingSessionTracker Tracker { get; }
    public EnergyIntegrator Integrator { get; set; } = new();
    public ElectricityPriceSchedule Prices { get; set; }
    public EstimationSettings Settings { get; }

    public static DateTimeOffset At(double seconds) => Synthetic.BaseUtc + TimeSpan.FromSeconds(seconds);

    public void Attach(long monitoringSessionId = 1) => Tracker.Attach(monitoringSessionId, Settings, () => Prices);

    /// <summary>Gives the tracker one sample (and what the integrator made of it), as the monitor would.</summary>
    public IntegrationStep Feed(double seconds, double? cpu, double? gpu, double? utcSeconds = null)
    {
        var sample = Synthetic.Sample(seconds, cpu, gpu, utcSeconds);
        var step = Integrator.Accept(sample);
        Time.Now = sample.UtcTime;
        Tracker.OnSample(sample, step);
        return step;
    }

    public void Poll(double seconds, params ProcessInfo[] processes)
    {
        Time.Now = At(seconds);
        Tracker.ObserveProcesses(At(seconds), processes);
    }

    /// <summary>Samples and polls once per second over [from, to] inclusive, the way monitoring + the poller interleave.</summary>
    public void Run(double from, double to, double? cpu, double? gpu, params ProcessInfo[] processes)
    {
        for (var t = from; t <= to; t++)
        {
            Feed(t, cpu, gpu);
            Poll(t, processes);
        }
    }

    public static ProcessInfo Proc(int pid, string name, string? path = null, int parent = 1, DateTimeOffset? start = null) =>
        new(pid, parent, name, path, start);

    public GamingSessionSummary Summary(long id) => GamingAnalytics.Summarize(Store.Get(id), Prices, Time.Now);
}

using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Tests.TestSupport;
using static WattScope.Core.Tests.TestSupport.Synthetic;

namespace WattScope.Core.Tests.Monitoring;

public class EnergyMonitorTests
{
    private static readonly EstimationSettings Settings = new();

    private static (EnergyMonitor Monitor, InMemoryEnergyStore Store) NewMonitor(EnergyMonitorOptions? options = null)
    {
        var store = new InMemoryEnergyStore();
        return (new EnergyMonitor(store, options), store);
    }

    [Fact]
    public void SleepAndWake_RecordsASuspendGap_AndNoEnergyForTheSleep()
    {
        var (monitor, store) = NewMonitor();
        var session = monitor.Start([], Settings);

        foreach (var s in Constant(0, 5, 1, 100, 100)) monitor.OnSample(s);
        monitor.OnSuspend();
        monitor.OnSample(Sample(6, 100, 100));          // arrives while suspending: ignored
        monitor.OnResume();
        foreach (var s in Constant(3600, 3605, 1, 100, 100)) monitor.OnSample(s);
        monitor.Stop();

        var totals = store.TotalsFor(session.Id);
        Assert.Equal(10, totals.MonitoredSeconds, 9);
        Assert.Equal(1000, totals.CpuPackageJoules, 9);
        var gap = Assert.Single(store.Gaps);
        Assert.Equal(GapReason.Suspend, gap.Reason);
        Assert.Equal(BaseUtc.AddSeconds(5), gap.StartUtc);
        Assert.Equal(BaseUtc.AddSeconds(3600), gap.EndUtc);
        Assert.Equal(1, monitor.Statistics.SamplesIgnoredWhileSuspended);
    }

    [Fact]
    public void SuspendFlushesImmediately()
    {
        var (monitor, store) = NewMonitor();
        monitor.Start([], Settings);
        monitor.OnSample(Sample(0, 100, 100));
        monitor.OnSample(Sample(1, 100, 100));

        monitor.OnSuspend();

        Assert.Single(store.Batches);
        Assert.Equal(2, store.Batches[0].Samples.Count);
    }

    [Fact]
    public void FailedFlush_KeepsDataAndRetries_WithoutLosingOrDuplicatingEnergy()
    {
        var (monitor, store) = NewMonitor(new EnergyMonitorOptions { FlushInterval = TimeSpan.FromSeconds(5) });
        var session = monitor.Start([], Settings);
        store.FailNextWrites = 1;

        foreach (var s in Constant(0, 20, 1, 100, 100)) monitor.OnSample(s);
        monitor.Stop();

        Assert.Equal(1, monitor.Statistics.FlushFailures);
        Assert.Equal(2000, store.TotalsFor(session.Id).CpuPackageJoules, 9);
        Assert.Equal(21, store.Batches.Sum(b => b.Samples.Count));
    }

    [Fact]
    public void PendingRawSamples_AreBounded_WhileTheStoreIsFailing_ButEnergyIsKept()
    {
        var (monitor, store) = NewMonitor(new EnergyMonitorOptions { FlushInterval = TimeSpan.FromSeconds(1), MaxPendingSamples = 10 });
        var session = monitor.Start([], Settings);
        store.FailNextWrites = 1000;

        foreach (var s in Constant(0, 100, 1, 100, 100)) monitor.OnSample(s);
        store.FailNextWrites = 0;
        monitor.Stop();

        Assert.Equal(10, store.Batches.Single().Samples.Count);
        Assert.Equal(91, monitor.Statistics.RawSamplesDropped);
        Assert.Equal(10000, store.TotalsFor(session.Id).CpuPackageJoules, 9);
    }

    [Fact]
    public void DuplicateSamples_AreCountedAndDiscarded()
    {
        var (monitor, store) = NewMonitor();
        var session = monitor.Start([], Settings);

        monitor.OnSample(Sample(0, 100, 100));
        monitor.OnSample(Sample(1, 100, 100));
        monitor.OnSample(Sample(1, 100, 100));
        monitor.Stop();

        Assert.Equal(1, monitor.Statistics.DuplicatesDiscarded);
        Assert.Equal(2, store.Batches.Sum(b => b.Samples.Count));
        Assert.Equal(100, store.TotalsFor(session.Id).CpuPackageJoules, 9);
    }

    [Fact]
    public void AvailabilityChanges_AreRunLengthEncoded()
    {
        var (monitor, store) = NewMonitor();
        monitor.Start([], Settings);

        monitor.OnSample(Sample(0, 100, 100));
        monitor.OnSample(Sample(1, 100, 100));
        monitor.OnSample(Sample(2, null, 100));
        monitor.OnSample(Sample(3, null, 100));
        monitor.OnSample(Sample(4, 100, 100));
        monitor.Stop();

        var changes = store.Batches.SelectMany(b => b.AvailabilityChanges).ToList();
        Assert.Equal(4, changes.Count); // CPU valid, GPU valid, CPU unavailable, CPU valid
        Assert.Equal(3, changes.Count(c => c.Channel == EnergyChannel.CpuPackage));
    }

    [Fact]
    public void ResumingAnInterruptedSession_RecordsAnInterruptedGap_FromTheLastObservation()
    {
        var store = new InMemoryEnergyStore();
        store.Seed(new MonitoringSession
        {
            Id = 7,
            StartedUtc = BaseUtc.AddHours(-1),
            LastObservationUtc = BaseUtc.AddMinutes(-10),
            Status = SessionStatus.Interrupted,
            Estimation = Settings,
            Sensors = []
        });
        var monitor = new EnergyMonitor(store);

        monitor.Resume(7, []);
        foreach (var s in Constant(0, 3, 1, 100, 100)) monitor.OnSample(s);
        monitor.Stop();

        var gap = Assert.Single(store.Gaps);
        Assert.Equal(GapReason.Interrupted, gap.Reason);
        Assert.Equal(BaseUtc.AddMinutes(-10), gap.StartUtc);
        Assert.Equal(BaseUtc, gap.EndUtc);
        Assert.Equal(300, store.TotalsFor(7).CpuPackageJoules, 9);
        Assert.Equal(SessionStatus.Stopped, store.GetSession(7)!.Status);
    }

    [Fact]
    public void ResumingAStoppedSession_RecordsAPausedGap()
    {
        var store = new InMemoryEnergyStore();
        store.Seed(new MonitoringSession
        {
            Id = 3, StartedUtc = BaseUtc.AddHours(-1), LastObservationUtc = BaseUtc.AddMinutes(-1),
            Status = SessionStatus.Stopped, Estimation = Settings, Sensors = []
        });
        var monitor = new EnergyMonitor(store);

        monitor.Resume(3, []);
        monitor.OnSample(Sample(0, 100, 100));
        monitor.Stop();

        Assert.Equal(GapReason.Paused, Assert.Single(store.Gaps).Reason);
    }

    [Fact]
    public void ResumingAnActiveSession_IsRefused_UntilRecovered()
    {
        var store = new InMemoryEnergyStore();
        store.Seed(new MonitoringSession { Id = 1, StartedUtc = BaseUtc, Status = SessionStatus.Active, Estimation = Settings, Sensors = [] });

        Assert.Throws<InvalidOperationException>(() => new EnergyMonitor(store).Resume(1, []));
    }

    [Fact]
    public void SessionUsesItsOwnEstimationSnapshot()
    {
        var (monitor, store) = NewMonitor();
        var custom = new EstimationSettings { OtherComponentsWatts = 0, PsuEfficiency = 1.0 };
        var session = monitor.Start([], custom);

        foreach (var s in Constant(0, 10, 1, 100, 100)) monitor.OnSample(s);
        monitor.Stop();

        // (100 + 100 + 0) / 1.0 over 10 s
        Assert.Equal(2000, store.TotalsFor(session.Id).EstimatedCompleteJoules, 9);
    }
}

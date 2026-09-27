using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using static WattScope.Storage.Tests.TestDatabase;

namespace WattScope.Storage.Tests;

public class SessionPersistenceTests
{
    private static readonly EstimationSettings Settings = new() { OtherComponentsWatts = 50, PsuEfficiency = 0.9 };

    [Fact]
    public void RecordedSession_RoundTripsThroughTheDatabase()
    {
        using var db = new TestDatabase();
        long sessionId;
        using (var store = db.Open())
        {
            var monitor = new EnergyMonitor(store);
            sessionId = monitor.Start(DevMachineBindings, Settings, processElevated: true).Id;
            foreach (var s in Constant(0, 3600, 45.40, 43.01))
                monitor.OnSample(s);
            Assert.True(monitor.Stop());
        }

        using (var store = db.Open())
        {
            var session = store.GetSession(sessionId)!;
            Assert.Equal(SessionStatus.Stopped, session.Status);
            Assert.Equal(BaseUtc.AddSeconds(3600), session.LastObservationUtc);
            Assert.Equal(Settings, session.Estimation);
            Assert.Equal(2, session.Sensors.Count);
            Assert.Contains(session.Sensors, b => b.Channel == EnergyChannel.GpuBoard && b.ProviderId == "Nvml");

            var buckets = store.GetSessionBuckets(sessionId);
            Assert.Equal(4, buckets.Count);
            var totals = EnergyReportBuilder.Sum(buckets);
            Assert.Equal(3600, totals.MonitoredSeconds, 6);
            Assert.Equal(45.40 * 3600, totals.CpuPackageJoules, 6);
            Assert.Equal(43.01 * 3600, totals.GpuBoardJoules, 6);
            Assert.Equal((45.40 + 43.01 + 50) / 0.9 * 3600, totals.EstimatedCompleteJoules, 6);

            var samples = store.GetSamples(sessionId);
            Assert.Equal(3601, samples.Count);
            Assert.Equal(45.40, samples[0].CpuPackageWatts);   // stored as 45400 mW
            Assert.Equal(ReadingState.Valid, samples[0].GpuBoardState);
        }
    }

    [Fact]
    public void GapsAndAvailability_ArePersisted_AndAvailabilityRunsAreClosedOnStop()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var monitor = new EnergyMonitor(store);
        var id = monitor.Start(DevMachineBindings, Settings).Id;

        foreach (var s in Constant(0, 10, 45, 43)) monitor.OnSample(s);
        foreach (var s in Constant(11, 20, null, 43)) monitor.OnSample(s);   // CPU reads 0 W (rejected)
        foreach (var s in Constant(100, 110, 45, 43)) monitor.OnSample(s);   // 80 s stall
        monitor.Stop();

        var gap = Assert.Single(store.GetSessionGaps(id));
        Assert.Equal(GapReason.SamplingStall, gap.Reason);
        Assert.Equal(BaseUtc.AddSeconds(20), gap.StartUtc);
        Assert.Equal(BaseUtc.AddSeconds(100), gap.EndUtc);

        var availability = store.GetAvailability(id);
        var cpu = availability.Where(a => a.Channel == EnergyChannel.CpuPackage).ToList();
        Assert.Equal([ReadingState.Valid, ReadingState.ImplausibleZero, ReadingState.Valid], cpu.Select(a => a.State));
        Assert.All(availability, a => Assert.NotNull(a.ToUtc));
        Assert.Equal(BaseUtc.AddSeconds(110), cpu[^1].ToUtc);

        var totals = EnergyReportBuilder.Sum(store.GetSessionBuckets(id));
        Assert.Equal(30, totals.MonitoredSeconds, 6);          // 10 + 9 + 10; nothing for the stall
        Assert.Equal(20, totals.CpuPackageSeconds, 6);         // 0 W CPU samples contribute nothing
        Assert.Equal(10, totals.EstimatedPartialSeconds, 6);
    }

    [Fact]
    public void UnexpectedTermination_IsRecovered_WithoutFabricatingEnergy()
    {
        using var db = new TestDatabase();
        long id;

        // Recording process: flushes every 10 s, then "crashes" at t=25 without Stop().
        using (var store = db.Open())
        {
            var monitor = new EnergyMonitor(store, new EnergyMonitorOptions { FlushInterval = TimeSpan.FromSeconds(10) });
            id = monitor.Start(DevMachineBindings, Settings).Id;
            foreach (var s in Constant(0, 25, 100, 100))
                monitor.OnSample(s);
        }

        // Next process start.
        using (var store = db.Open())
        {
            Assert.Equal(SessionStatus.Active, store.GetSession(id)!.Status);
            using var monitorLock = MonitorLock.TryAcquire(db.FilePath);
            Assert.NotNull(monitorLock);

            var recovered = Assert.Single(store.RecoverInterruptedSessions());
            Assert.Equal(id, recovered.Id);
            Assert.Equal(SessionStatus.Interrupted, recovered.Status);
            Assert.Equal(BaseUtc.AddSeconds(20), recovered.LastObservationUtc);  // last flushed sample
            Assert.Equal(recovered.LastObservationUtc, recovered.EndedUtc);

            // Only the flushed 20 s exist; the unflushed 5 s are lost, not estimated.
            var totals = EnergyReportBuilder.Sum(store.GetSessionBuckets(id));
            Assert.Equal(20, totals.MonitoredSeconds, 6);
            Assert.Equal(2000, totals.CpuPackageJoules, 6);
            Assert.Equal(21, store.GetSamples(id).Count);
            Assert.Empty(store.RecoverInterruptedSessions());
        }
    }

    [Fact]
    public void RecoveredSession_CanBeResumed_WithAnInterruptedGap()
    {
        using var db = new TestDatabase();
        long id;
        using (var store = db.Open())
        {
            var monitor = new EnergyMonitor(store);
            id = monitor.Start(DevMachineBindings, Settings).Id;
            foreach (var s in Constant(0, 10, 100, 100)) monitor.OnSample(s);
            monitor.Flush();
        }

        using (var store = db.Open())
        {
            store.RecoverInterruptedSessions();
            var monitor = new EnergyMonitor(store);
            monitor.Resume(id, DevMachineBindings);
            // New process: monotonic clock restarts, UTC continues 10 minutes later.
            foreach (var s in Constant(0, 10, 100, 100, baseUtc: BaseUtc.AddMinutes(10))) monitor.OnSample(s);
            monitor.Stop();

            var session = store.GetSession(id)!;
            Assert.Equal(SessionStatus.Stopped, session.Status);
            var gap = Assert.Single(store.GetSessionGaps(id));
            Assert.Equal(GapReason.Interrupted, gap.Reason);
            Assert.Equal(BaseUtc.AddSeconds(10), gap.StartUtc);
            Assert.Equal(BaseUtc.AddMinutes(10), gap.EndUtc);
            Assert.Equal(2000, EnergyReportBuilder.Sum(store.GetSessionBuckets(id)).CpuPackageJoules, 6);
        }
    }

    [Fact]
    public void StoppedSession_CanBeResumed_AndAccumulatesIntoTheSameBuckets()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var first = new EnergyMonitor(store);
        var id = first.Start(DevMachineBindings, Settings).Id;
        foreach (var s in Constant(0, 10, 100, 100)) first.OnSample(s);
        first.Stop();

        var second = new EnergyMonitor(store);
        second.Resume(id, DevMachineBindings);
        foreach (var s in Constant(0, 10, 100, 100, baseUtc: BaseUtc.AddSeconds(60))) second.OnSample(s);
        second.Stop();

        var buckets = store.GetSessionBuckets(id);
        Assert.Single(buckets);
        Assert.Equal(20, buckets[0].Totals.MonitoredSeconds, 6);
        Assert.Equal(GapReason.Paused, Assert.Single(store.GetSessionGaps(id)).Reason);
    }

    [Fact]
    public void WriteBatch_IsAtomic_NothingIsPersistedWhenAnyPartFails()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var id = store.CreateSession(new NewSessionRequest { StartedUtc = BaseUtc, Estimation = Settings, Sensors = [] }).Id;

        var batch = new EnergyWriteBatch
        {
            SessionId = id,
            Samples = [Sample(0, 100, 100), Sample(1, 100, 100)],
            BucketDeltas = [new EnergyBucket { SessionId = id + 1, BucketStartUtc = BaseUtc, Totals = new EnergyTotals { MonitoredSeconds = 1 } }],
            Gaps = [],
            AvailabilityChanges = [],
            LastObservationUtc = BaseUtc.AddSeconds(1)
        };

        Assert.ThrowsAny<Exception>(() => store.WriteBatch(batch));

        Assert.Empty(store.GetSamples(id));
        Assert.Null(store.GetSession(id)!.LastObservationUtc);
    }

    [Fact]
    public void RawSamplesWithTheSameMillisecond_AreStoredOnce()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var id = store.CreateSession(new NewSessionRequest { StartedUtc = BaseUtc, Estimation = Settings, Sensors = [] }).Id;

        store.WriteBatch(new EnergyWriteBatch
        {
            SessionId = id,
            Samples = [Sample(0, 100, 100), Sample(0, 999, 999)],
            BucketDeltas = [],
            Gaps = [],
            AvailabilityChanges = []
        });

        Assert.Equal(100, Assert.Single(store.GetSamples(id)).CpuPackageWatts);
    }

    [Fact]
    public void MonitorLock_IsExclusive()
    {
        using var db = new TestDatabase();
        using (var first = MonitorLock.TryAcquire(db.FilePath))
        {
            Assert.NotNull(first);
            Assert.Null(MonitorLock.TryAcquire(db.FilePath));
        }
        using var again = MonitorLock.TryAcquire(db.FilePath);
        Assert.NotNull(again);
    }

    [Fact]
    public void SessionAndDailyReports_FromTheDatabase_MatchExpectedCost()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(-1), PricePerKwh = 0.30m }, BaseUtc);
        var settings = new EstimationSettings { OtherComponentsWatts = 0, PsuEfficiency = 1.0 };
        var monitor = new EnergyMonitor(store);
        var id = monitor.Start(DevMachineBindings, settings).Id;
        // 500 W CPU + 500 W GPU for one hour = 1 kWh estimated = 0.30 EUR.
        foreach (var s in Constant(0, 3600, 500, 500)) monitor.OnSample(s);
        monitor.Stop();

        var session = store.GetSession(id)!;
        var report = EnergyReportBuilder.Build("s", session.StartedUtc, session.EndedUtc!.Value,
            store.GetSessionBuckets(id), store.GetSessionGaps(id), store.GetPriceSchedule(), [session.Estimation]);
        Assert.Equal(EstimateCompleteness.Complete, report.Completeness);
        Assert.Equal(0.30m, report.Cost.EstimatedCostRounded);

        var utc = TimeZoneInfo.Utc;
        var (dayStart, dayEnd) = ReportPeriods.LocalDay(DateOnly.FromDateTime(BaseUtc.UtcDateTime), utc);
        var daily = EnergyReportBuilder.DailyBreakdown(store.GetBuckets(dayStart, dayEnd), utc, store.GetPriceSchedule());
        Assert.Equal(0.30m, Assert.Single(daily).Cost.EstimatedCostRounded);
    }
}

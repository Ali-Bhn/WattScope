using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Gaming;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Tests.TestSupport;
using static WattScope.Core.Tests.TestSupport.GamingRig;

namespace WattScope.Core.Tests.Gaming;

/// <summary>
/// Energy attribution and session ownership, end to end through the real integrator, detector and tracker
/// with synthetic processes and samples. Defaults: 100 W CPU + 200 W GPU + 50 W other, 90 % PSU
/// => 388.888... W estimated system power; confirm delay 20 s, end grace 30 s.
/// </summary>
public class GamingSessionTrackerTests
{
    private static readonly ProcessInfo Cod = Proc(100, "cod.exe", @"D:\Games\Call of Duty\cod.exe");
    private const double SystemWatts = 350 / 0.9;

    private static ElectricityPriceSchedule Price(double secondsFromBase, decimal perKwh, params (double Seconds, decimal Price)[] more) =>
        new([new ElectricityPrice { EffectiveFromUtc = At(secondsFromBase), PricePerKwh = perKwh }, .. more.Select(m => new ElectricityPrice { EffectiveFromUtc = At(m.Seconds), PricePerKwh = m.Price })]);

    /// <summary>A game from t=10..100, then closed; runs long enough for the grace period to elapse.</summary>
    private static GamingRig PlayOneSession(double? cpu, double? gpu, ElectricityPriceSchedule? prices = null)
    {
        var rig = new GamingRig(prices: prices);
        rig.Attach();
        rig.Run(0, 9, cpu, gpu);
        rig.Run(10, 100, cpu, gpu, Cod);
        rig.Run(101, 140, cpu, gpu);
        return rig;
    }

    // ---------------------------------------------------------------- start / stop / complete data

    [Fact]
    public void GameSession_IsRecorded_FromFirstSightUntilTheProcessWasMissed()
    {
        var rig = PlayOneSession(100, 200);

        var session = Assert.Single(rig.Store.Sessions);
        Assert.Equal("cod.exe", session.GameKey);
        Assert.Equal("Call of Duty", session.DisplayName);
        Assert.Equal(GameDetectionSource.KnownCatalog, session.Source);
        Assert.False(session.IsManual);
        Assert.Equal(1, session.MonitoringSessionId);
        Assert.Equal(At(10), session.StartedUtc);                       // back-dated to first sight, not to the 20 s confirmation
        Assert.Equal(At(101), session.EndedUtc);                        // the grace period is not gaming time
        Assert.Equal(GamingSessionStatus.Ended, session.Status);
        Assert.Equal(GamingEndReason.ProcessExited, session.EndReason);
    }

    [Fact]
    public void CompleteSensors_GiveCompleteMeasuredAndEstimatedEnergy()
    {
        var rig = PlayOneSession(100, 200);

        var s = rig.Summary(1);
        Assert.Equal(91, s.DurationSeconds, 6);
        Assert.Equal(91, s.MonitoredSeconds, 6);
        Assert.Equal(100.0 * 91, s.Totals.CpuPackageJoules, 6);
        Assert.Equal(200.0 * 91, s.Totals.GpuBoardJoules, 6);
        Assert.Equal(SystemWatts * 91, s.Totals.EstimatedCompleteJoules, 6);
        Assert.Equal(EstimateCompleteness.Complete, s.Completeness);
        Assert.False(s.SystemIsLowerBound);
        Assert.Equal(100, s.AvgCpuWatts!.Value, 6);
        Assert.Equal(200, s.AvgGpuWatts!.Value, 6);
        Assert.Equal(SystemWatts, s.AvgSystemWatts!.Value, 6);
        Assert.Equal(100, s.PeakCpuWatts);
        Assert.Equal(200, s.PeakGpuWatts);
        Assert.Equal(SystemWatts, s.PeakSystemWatts!.Value, 6);
    }

    [Fact]
    public void GamingEnergy_IsDerivedFromTheSameIntervals_SoItNeverExceedsTheMonitoringSession()
    {
        var rig = PlayOneSession(100, 200);

        // Whole-run integration of the same samples: 0..140 s at the same power.
        var whole = Synthetic.Integrate(Synthetic.Constant(0, 140, 1, 100, 200));
        var gaming = rig.Summary(1);

        Assert.True(gaming.Totals.CpuPackageJoules < whole.Totals.CpuPackageJoules);
        Assert.True(gaming.Totals.EstimatedCompleteJoules < whole.Totals.EstimatedCompleteJoules);
        Assert.Equal(whole.Totals.CpuPackageJoules * 91 / 140, gaming.Totals.CpuPackageJoules, 6);   // constant power: strictly proportional to time
    }

    [Fact]
    public void ACandidateThatVanishesBeforeConfirmation_CreatesNothing_AndKeepsNoEnergy()
    {
        var rig = new GamingRig();
        rig.Attach();

        rig.Run(0, 4, 100, 200);
        rig.Run(5, 9, 100, 200, Cod);               // 5 s of a "game"
        rig.Run(10, 60, 100, 200);                   // gone

        Assert.Empty(rig.Store.Sessions);
        Assert.Null(rig.Tracker.Live());
    }

    [Fact]
    public void LauncherOpenAndClosed_NeverCreatesAGamingSession()
    {
        var rig = new GamingRig();
        rig.Attach();
        var launcher = Proc(5, "Battle.net.exe", @"C:\Program Files (x86)\Battle.net\Battle.net.exe");

        rig.Run(0, 300, 100, 200, launcher);
        rig.Run(301, 330, 100, 200);

        Assert.Empty(rig.Store.Sessions);
    }

    [Fact]
    public void Live_ShowsACandidate_ThenAnActiveSession_ThenNothing()
    {
        var rig = new GamingRig();
        rig.Attach();

        rig.Run(0, 9, 100, 200);
        Assert.Null(rig.Tracker.Live());

        rig.Run(10, 15, 100, 200, Cod);
        Assert.Equal("candidate", rig.Tracker.Live()!.State);

        rig.Run(16, 40, 100, 200, Cod);
        var live = rig.Tracker.Live()!;
        Assert.Equal("active", live.State);
        Assert.Equal("Call of Duty", live.GameName);
        Assert.Equal(1, live.SessionId);
        Assert.True(live.SystemKwh > 0);

        rig.Run(41, 100, 100, 200);                  // closes
        Assert.Null(rig.Tracker.Live());
    }

    // ---------------------------------------------------------------- partial sensor availability

    [Fact]
    public void MissingCpu_IsPartial_NeverZero_AndHasNoCompleteCost()
    {
        var rig = PlayOneSession(cpu: null, gpu: 200, prices: Price(-3600, 0.30m));

        var s = rig.Summary(1);
        Assert.Equal(EstimateCompleteness.Partial, s.Completeness);
        Assert.Equal(0, s.Totals.CpuPackageJoules);
        Assert.Equal(0, s.Totals.CpuPackageSeconds);            // no CPU coverage at all -- not "zero watts for 91 s"
        Assert.Null(s.AvgCpuWatts);
        Assert.Null(s.PeakCpuWatts);
        Assert.Equal(200, s.AvgGpuWatts!.Value, 6);             // GPU analytics are still available
        Assert.Equal(200, s.PeakGpuWatts);
        Assert.True(s.SystemIsLowerBound);
        Assert.Equal(250 / 0.9, s.AvgSystemWatts!.Value, 6);    // GPU + other, never CPU-as-zero presented as complete
        Assert.Equal(250 / 0.9, s.PeakSystemWatts!.Value, 6);
        Assert.Equal(GamingCostStatus.WithheldPartial, s.CostStatus);
        Assert.Null(s.Cost);                                    // a complete cost is never fabricated
        Assert.NotNull(s.LowerBoundCost);                       // the lower-bound cost is reported separately
        Assert.Equal(0, s.Totals.EstimatedCompleteJoules);
    }

    [Fact]
    public void BothChannelsMissing_HasNoEstimateAtAll()
    {
        var rig = PlayOneSession(cpu: null, gpu: null);

        var s = rig.Summary(1);
        Assert.Equal(EstimateCompleteness.None, s.Completeness);
        Assert.Equal(GamingCostStatus.NoEstimate, s.CostStatus);
        Assert.Null(s.AvgSystemWatts);
        Assert.Equal(0, s.SystemJoules);
    }

    [Fact]
    public void SensorThatComesAndGoes_MakesTheSessionPartial_NotComplete()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 9, 100, 200);
        rig.Run(10, 40, 100, 200, Cod);
        rig.Run(41, 70, null, 200, Cod);                         // CPU lost halfway
        rig.Run(71, 100, 100, 200, Cod);
        rig.Run(101, 140, 100, 200);

        var s = rig.Summary(1);
        Assert.Equal(EstimateCompleteness.Partial, s.Completeness);
        Assert.True(s.Totals.EstimatedCompleteSeconds > 0);
        Assert.True(s.Totals.EstimatedPartialSeconds > 0);
        Assert.True(s.Totals.CpuPackageSeconds < s.MonitoredSeconds);     // CPU coverage is honestly lower than monitored time
        Assert.Equal(GamingCostStatus.WithheldPartial, s.CostStatus);
    }

    // ---------------------------------------------------------------- gaps: sleep, stalls, restarts

    [Fact]
    public void SamplingGapDuringAGame_IsNotIntegrated_AndMonitoredTimeIsLessThanDuration()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 9, 100, 200);
        rig.Run(10, 50, 100, 200, Cod);
        for (var t = 51; t < 70; t++)                              // the sampler stalls for 20 s; the game keeps running
            rig.Poll(t, Cod);
        rig.Run(70, 100, 100, 200, Cod);
        rig.Run(101, 140, 100, 200);

        var s = rig.Summary(1);
        Assert.Equal(91, s.DurationSeconds, 6);
        Assert.Equal(40 + 31, s.MonitoredSeconds, 6);               // [10..50] + [70..101]; nothing for the 20 s stall
        Assert.Equal(100.0 * 71, s.Totals.CpuPackageJoules, 6);
        Assert.Single(rig.Store.Sessions);                          // a gap does not split the session
    }

    [Fact]
    public void SleepDuringAGame_DoesNotCountAsMonitoredGamingEnergy_AndDoesNotSplitTheSession()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 9, 100, 200);
        rig.Run(10, 50, 100, 200, Cod);

        rig.Time.Now = At(50);                                      // the machine sleeps for an hour
        rig.Tracker.OnSuspended();
        rig.Integrator.BreakChain(GapReason.Suspend, detail: "sleep");
        rig.Tracker.OnResumed();

        for (var t = 3650; t <= 3700; t++)
        {
            var step = rig.Feed(t - 3600 + 50, 100, 200, utcSeconds: t);   // monotonic clock barely moved; UTC jumped an hour
            rig.Poll(t, Cod);
            if (t == 3650) Assert.Null(step.Interval);                      // the first post-resume sample starts a new chain
        }
        rig.Feed(3701 - 3600 + 50, 100, 200, utcSeconds: 3701);
        rig.Poll(3701);
        rig.Poll(3735);                                                      // grace over

        var session = Assert.Single(rig.Store.Sessions);
        var s = rig.Summary(session.Id);
        Assert.True(s.DurationSeconds > 3600);                       // the game was alive across the sleep: wall-clock duration includes it
        Assert.Equal(40 + 51, s.MonitoredSeconds, 6);                // but only monitored time carries energy
        Assert.Equal(100.0 * (40 + 51), s.Totals.CpuPackageJoules, 6);
    }

    [Fact]
    public void GameClosedWhileTheMachineSlept_EndsAtTheSuspend_NotAtWake()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 9, 100, 200);
        rig.Run(10, 50, 100, 200, Cod);

        rig.Time.Now = At(50);
        rig.Tracker.OnSuspended();
        rig.Integrator.BreakChain(GapReason.Suspend);
        rig.Tracker.OnResumed();
        rig.Poll(30_000);                                            // hours later the game is gone
        rig.Poll(30_040);

        var session = Assert.Single(rig.Store.Sessions);
        Assert.Equal(GamingEndReason.ProcessExited, session.EndReason);
        Assert.Equal(At(50), session.EndedUtc);                      // it was alive when the machine slept; nothing is claimed after
    }

    [Fact]
    public void SleepInTheMiddleOfCandidateConfirmation_RestartsTheCandidate()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 9, 100, 200);
        rig.Run(10, 15, 100, 200, Cod);                              // 5 s of presence...
        rig.Time.Now = At(15);
        rig.Tracker.OnSuspended();                                   // ...then sleep: presence across it proves nothing

        Assert.Null(rig.Tracker.Live());
        Assert.Empty(rig.Store.Sessions);
    }

    [Fact]
    public void RealEnergyMonitor_WithTheTrackerAsObserver_ExcludesTheSleepFromGamingEnergy()
    {
        var store = new InMemoryEnergyStore();
        var gamingStore = new InMemoryGamingStore();
        var time = new ManualTime();
        var detector = new GameDetector(new GameDetectorOptions { ConfirmDelayMapped = TimeSpan.FromSeconds(2) }, new GameIdentifier());
        var tracker = new GamingSessionTracker(gamingStore, detector, time);
        var monitor = new EnergyMonitor(store, observer: tracker);
        var session = monitor.Start([], new EstimationSettings());
        tracker.Attach(session.Id, new EstimationSettings(), () => ElectricityPriceSchedule.Empty);

        foreach (var s in Synthetic.Constant(0, 10, 1, 100, 200)) { time.Now = s.UtcTime; monitor.OnSample(s); tracker.ObserveProcesses(s.UtcTime, [Cod]); }
        time.Now = At(10);
        monitor.OnSuspend();
        monitor.OnResume();
        foreach (var s in Synthetic.Constant(3600, 3610, 1, 100, 200)) { time.Now = s.UtcTime; monitor.OnSample(s); tracker.ObserveProcesses(s.UtcTime, [Cod]); }
        monitor.Stop();

        var record = gamingStore.Sessions.Single();
        var summary = GamingAnalytics.Summarize(record, ElectricityPriceSchedule.Empty, time.Now);
        var monitorTotals = store.TotalsFor(session.Id);

        Assert.Equal(20, monitorTotals.MonitoredSeconds, 9);          // the monitor: 10 s + 10 s, nothing for the sleep
        Assert.Equal(20, summary.MonitoredSeconds, 6);                // gaming sees exactly the same monitored seconds
        Assert.True(summary.DurationSeconds > 3000);
    }

    // ---------------------------------------------------------------- grace: tail dropped / merged

    [Fact]
    public void GraceTail_IsDiscarded_WhenTheGameDoesNotReturn()
    {
        var rig = PlayOneSession(100, 200);

        var s = rig.Summary(1);
        Assert.Equal(91, s.MonitoredSeconds, 6);                      // not 121: the 30 s of grace were never gaming
    }

    [Fact]
    public void ShortRestart_KeepsOneSession_AndTheGraceTimeBelongsToIt()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 9, 100, 200);
        rig.Run(10, 50, 100, 200, Cod);
        rig.Run(51, 60, 100, 200);                                   // helper restarts: 10 s without the process
        rig.Run(61, 100, 100, 200, Proc(200, "cod.exe", @"D:\Games\Call of Duty\cod.exe"));
        rig.Run(101, 140, 100, 200);

        var session = Assert.Single(rig.Store.Sessions);
        var s = rig.Summary(session.Id);
        Assert.Equal(At(10), session.StartedUtc);
        Assert.Equal(At(101), session.EndedUtc);
        Assert.Equal(91, s.MonitoredSeconds, 6);                      // continuous: the restart gap was part of the same session
    }

    // ---------------------------------------------------------------- pricing

    [Fact]
    public void PriceChangeDuringTheSession_IsPricedAtTheTimeOfConsumption()
    {
        var rig = PlayOneSession(100, 200, Price(-3600, 0.30m, (60, 0.60m)));

        var s = rig.Summary(1);
        var kwh1 = (decimal)(SystemWatts * 50) / 3_600_000m;          // [10, 60)
        var kwh2 = (decimal)(SystemWatts * 41) / 3_600_000m;          // [60, 101)
        Assert.Equal(GamingCostStatus.Priced, s.CostStatus);
        Assert.Equal(kwh1 * 0.30m + kwh2 * 0.60m, s.Cost!.Value, 8);
        Assert.Equal(PriceAllocationPrecision.Exact, s.CostPrecision);
        Assert.Equal(2, rig.Store.Get(1).Energy.Count);               // one row per price actually in effect
    }

    [Fact]
    public void EnergyBeforeAnyPrice_IsUnpriced_NotPricedAtZero()
    {
        var rig = PlayOneSession(100, 200, Price(60, 0.30m));         // the first price starts mid-session

        var s = rig.Summary(1);
        Assert.Equal(GamingCostStatus.WithheldUnpriced, s.CostStatus);
        Assert.Null(s.Cost);                                          // a cost covering only part of the energy is not "the cost"
    }

    [Fact]
    public void NoPriceAtAll_MeansNoCost()
    {
        var s = PlayOneSession(100, 200).Summary(1);

        Assert.Equal(GamingCostStatus.WithheldUnpriced, s.CostStatus);
        Assert.Null(s.Cost);
    }

    [Fact]
    public void PriceAddedRetroactively_IsAnApproximation_AndSaysSo()
    {
        var rig = PlayOneSession(100, 200);                           // recorded with no prices known
        rig.Prices = Price(-3600, 0.30m, (60, 0.60m));                // ... then the user backfills prices

        var s = rig.Summary(1);
        Assert.Equal(PriceAllocationPrecision.ApproximateProportional, s.CostPrecision);
        var totalKwh = (decimal)(SystemWatts * 91) / 3_600_000m;
        var expected = totalKwh * (50m / 91m) * 0.30m + totalKwh * (41m / 91m) * 0.60m;   // split by wall time, assuming uniform power
        Assert.Equal((double)expected, (double)s.Cost!.Value, 6);
    }

    [Fact]
    public void PriceChange_IsPickedUpAtTheNextFlush_WithoutRestartingTheSession()
    {
        var rig = new GamingRig(prices: Price(-3600, 0.30m));
        var tracker = new GamingSessionTracker(rig.Store, new GameDetector(), rig.Time, flushInterval: TimeSpan.FromSeconds(5));
        var live = rig.Prices;
        tracker.Attach(1, rig.Settings, () => live);

        void Step(double t, params ProcessInfo[] p)
        {
            var sample = Synthetic.Sample(t, 100, 200);
            var step = rig.Integrator.Accept(sample);
            rig.Time.Now = sample.UtcTime;
            tracker.OnSample(sample, step);
            tracker.ObserveProcesses(sample.UtcTime, p);
        }
        for (var t = 0; t <= 30; t++) Step(t, Cod);
        live = Price(-3600, 0.30m, (45, 0.90m));                      // a price announced while playing
        for (var t = 31; t <= 90; t++) Step(t, Cod);

        var rows = rig.Store.Sessions.Single().Energy;
        Assert.Equal(2, rows.Count);                                  // energy after the next flush is priced at the new price
    }

    // ---------------------------------------------------------------- manual sessions

    [Fact]
    public void ManualSession_IsRecordedAsManual_AndEndsWithManualStop()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 9, 100, 200);

        rig.Time.Now = At(10);
        rig.Tracker.StartManual("Retro Game", "retro.exe");
        rig.Run(10, 60, 100, 200);                                   // no matching process is needed or consulted
        rig.Time.Now = At(60);
        rig.Tracker.StopManual();

        var session = Assert.Single(rig.Store.Sessions);
        Assert.True(session.IsManual);
        Assert.Equal(GameDetectionSource.Manual, session.Source);
        Assert.Equal(GamingEndReason.ManualStop, session.EndReason);
        Assert.Equal(At(10), session.StartedUtc);
        Assert.Equal(At(60), session.EndedUtc);
        Assert.Equal(50, rig.Summary(session.Id).MonitoredSeconds, 6);
    }

    [Fact]
    public void ManualSession_ObeysTheOneActiveSessionRule()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 40, 100, 200, Cod);                                // an automatic session is running

        Assert.Throws<InvalidOperationException>(() => rig.Tracker.StartManual("Other", null));
        Assert.Single(rig.Store.Sessions);

        rig.Tracker.Detach();
        rig.Attach();
        rig.Time.Now = At(50);
        rig.Tracker.StartManual("Manual", null);                      // after the first ended, a manual one may start
        Assert.Throws<InvalidOperationException>(() => rig.Tracker.StartManual("Second Manual", null));
    }

    [Fact]
    public void ManualSession_NeedsMonitoring()
    {
        var rig = new GamingRig();

        Assert.Throws<InvalidOperationException>(() => rig.Tracker.StartManual("Game", null));
    }

    [Fact]
    public void ManualSession_IsNotEndedByProcessesComingAndGoing()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Time.Now = At(0);
        rig.Tracker.StartManual("Handheld Emulator", "emu.exe");

        rig.Run(0, 200, 100, 200, Cod);                              // an unrelated game starts and runs
        rig.Run(201, 400, 100, 200);                                 // and nothing is running at all

        Assert.Equal(GamingSessionStatus.Active, rig.Store.Sessions.Single().Status);
    }

    // ---------------------------------------------------------------- ownership / lifecycle

    [Fact]
    public void SecondActiveSession_IsRefusedByTheStore_AndTheTrackerAbandonsTheContext()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Store.FailCreate = true;                                  // e.g. another gaming session is somehow still active

        rig.Run(0, 60, 100, 200, Cod);

        Assert.Empty(rig.Store.Sessions);
        Assert.Equal(DetectorPhase.Candidate, rig.Detector.Phase);    // it keeps trying, but never integrates into a session that does not exist
        Assert.Throws<GamingSessionExistsException>(() =>
        {
            var store = new InMemoryGamingStore();
            var game = new GameIdentity { Key = "a.exe", ExeName = "a.exe", DisplayName = "A", Source = GameDetectionSource.UserMapping, Tier = DetectionTier.Mapped };
            store.CreateGamingSession(new NewGamingSession { MonitoringSessionId = 1, Game = game, IsManual = false, StartedUtc = At(0) });
            store.CreateGamingSession(new NewGamingSession { MonitoringSessionId = 1, Game = game, IsManual = false, StartedUtc = At(1) });
        });
    }

    [Fact]
    public void StoppingMonitoring_EndsTheGamingSession_WithMonitoringStopped()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 60, 100, 200, Cod);

        rig.Time.Now = At(61);
        rig.Tracker.Detach();

        var session = Assert.Single(rig.Store.Sessions);
        Assert.Equal(GamingSessionStatus.Ended, session.Status);
        Assert.Equal(GamingEndReason.MonitoringStopped, session.EndReason);
        Assert.False(rig.Tracker.IsAttached);
    }

    [Fact]
    public void RestartOfTheSameMonitoringSession_KeepsTheGamingSession_WhenAsked()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 60, 100, 200, Cod);

        rig.Tracker.Detach(keepContext: true);                        // interval change: monitoring restarts in the same session
        rig.Attach();                                                 // same monitoring session id
        rig.Integrator = new EnergyIntegrator();
        rig.Run(65, 100, 100, 200, Cod);

        var session = Assert.Single(rig.Store.Sessions);
        Assert.Equal(GamingSessionStatus.Active, session.Status);
    }

    [Fact]
    public void AttachingToADifferentMonitoringSession_EndsTheOldGamingSession()
    {
        var rig = new GamingRig();
        rig.Attach(1);
        rig.Run(0, 60, 100, 200, Cod);

        rig.Tracker.Detach(keepContext: true);
        rig.Attach(2);                                                // a different monitoring session: the old gaming session cannot continue

        Assert.Equal(GamingEndReason.MonitoringStopped, rig.Store.Sessions.Single().EndReason);
    }

    [Fact]
    public void Observing_WhileNotAttached_IsIgnored()
    {
        var rig = new GamingRig();

        rig.Run(0, 100, 100, 200, Cod);

        Assert.Empty(rig.Store.Sessions);
        Assert.Equal(DetectorPhase.NoGame, rig.Detector.Phase);
    }

    [Fact]
    public void Progress_IsPersistedPeriodically_SoACrashLosesLittle()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 100, 100, 200, Cod);

        Assert.True(rig.Store.ProgressSaves >= 5, $"expected periodic saves, got {rig.Store.ProgressSaves}");
        var saved = rig.Store.Sessions.Single();
        Assert.True(GamingAnalytics.TotalsOf(saved.Energy).MonitoredSeconds > 60);   // the persisted state already carries most of the energy
    }

    [Fact]
    public void RenamingTheRunningGame_UpdatesTheStoredSession()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 40, 100, 200, Cod);

        rig.Tracker.ApplyMappings([new GameMapping { ExeName = "cod.exe", Kind = GameMappingKind.Game, DisplayName = "Call of Duty: Warzone" }]);

        Assert.Equal("Call of Duty: Warzone", rig.Store.Sessions.Single().DisplayName);
        Assert.Equal("Call of Duty: Warzone", rig.Tracker.Live()!.GameName);
    }

    [Fact]
    public void ExcludingTheRunningGame_EndsItImmediately()
    {
        var rig = new GamingRig();
        rig.Attach();
        rig.Run(0, 40, 100, 200, Cod);

        rig.Tracker.ApplyMappings([new GameMapping { ExeName = "cod.exe", Kind = GameMappingKind.Excluded }]);

        Assert.Equal(GamingEndReason.Excluded, rig.Store.Sessions.Single().EndReason);
        Assert.Null(rig.Tracker.Live());
    }

    [Fact]
    public void AnObserverThatThrows_NeverBreaksEnergyRecording()
    {
        var store = new InMemoryEnergyStore();
        var monitor = new EnergyMonitor(store, observer: new ThrowingObserver());
        var session = monitor.Start([], new EstimationSettings());

        foreach (var s in Synthetic.Constant(0, 20, 1, 100, 200))
            monitor.OnSample(s);
        monitor.Stop();

        Assert.Equal(20, store.TotalsFor(session.Id).MonitoredSeconds, 9);
    }

    private sealed class ThrowingObserver : IMonitorObserver
    {
        public void OnSample(PowerSample sample, IntegrationStep step) => throw new InvalidOperationException("boom");
        public void OnSuspended() => throw new InvalidOperationException("boom");
        public void OnResumed() => throw new InvalidOperationException("boom");
    }
}

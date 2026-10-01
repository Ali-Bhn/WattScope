using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Gaming;
using WattScope.Core.Pricing;
using static WattScope.Core.Tests.TestSupport.GamingRig;

namespace WattScope.Core.Tests.Gaming;

public class GamingAnalyticsTests
{
    private static readonly ElectricityPriceSchedule Eur30 = new([new ElectricityPrice { EffectiveFromUtc = At(-3600), PricePerKwh = 0.30m }]);

    private static long _id;

    /// <summary>A finished session with explicit totals: <paramref name="complete"/> seconds at completeWatts, <paramref name="partial"/> seconds at partialWatts.</summary>
    private static GamingSessionRecord Session(
        string key, double start, double durationSeconds, double completeSeconds = 0, double completeWatts = 0,
        double partialSeconds = 0, double partialWatts = 0, double? cpuWatts = null, double? gpuWatts = null,
        GamingSessionStatus status = GamingSessionStatus.Ended, string? name = null, bool unpriced = false, double? peakCpu = null, double? peakGpu = null)
    {
        var monitored = completeSeconds + partialSeconds;
        var totals = new EnergyTotals
        {
            MonitoredSeconds = monitored,
            CpuPackageJoules = cpuWatts is { } c ? c * monitored : 0,
            CpuPackageSeconds = cpuWatts is null ? 0 : monitored,
            GpuBoardJoules = gpuWatts is { } g ? g * monitored : 0,
            GpuBoardSeconds = gpuWatts is null ? 0 : monitored,
            EstimatedCompleteJoules = completeWatts * completeSeconds,
            EstimatedCompleteSeconds = completeSeconds,
            EstimatedPartialJoules = partialWatts * partialSeconds,
            EstimatedPartialSeconds = partialSeconds
        };
        return new GamingSessionRecord
        {
            Id = ++_id,
            MonitoringSessionId = 1,
            GameKey = key,
            ExeName = key,
            DisplayName = name ?? key,
            Source = GameDetectionSource.KnownCatalog,
            IsManual = false,
            StartedUtc = At(start),
            EndedUtc = status == GamingSessionStatus.Active ? null : At(start + durationSeconds),
            LastObservationUtc = At(start + durationSeconds),
            Status = status,
            EndReason = status == GamingSessionStatus.Active ? null : GamingEndReason.ProcessExited,
            Peaks = new GamingPeaks { CpuWatts = peakCpu, GpuWatts = peakGpu },
            Energy = [new GamingEnergyRow(unpriced ? null : Eur30.Entries[0].EffectiveFromUtc, totals, At(start), At(start + durationSeconds))]
        };
    }

    private static GamingSessionSummary Summ(GamingSessionRecord r, ElectricityPriceSchedule? prices = null) =>
        GamingAnalytics.Summarize(r, prices ?? Eur30, At(1_000_000));

    // ---------------------------------------------------------------- weighted aggregates

    [Fact]
    public void AverageSystemPower_IsEnergyWeighted_NotAnAverageOfAverages()
    {
        var a = Session("g.exe", 0, 3600, completeSeconds: 3600, completeWatts: 400);
        var b = Session("g.exe", 10_000, 600, completeSeconds: 600, completeWatts: 200);

        var game = Assert.Single(GamingAnalytics.AggregateByGame([Summ(a), Summ(b)]));

        Assert.Equal((400.0 * 3600 + 200.0 * 600) / 4200, game.Complete.AvgSystemWatts!.Value, 9);   // 371.43, not (400+200)/2 = 300
        Assert.NotEqual(300, game.Complete.AvgSystemWatts!.Value, 3);
    }

    [Fact]
    public void AverageCpuAndGpuPower_AreWeightedByCoveredSeconds_AndMissingCpuDoesNotDragItDown()
    {
        var withCpu = Session("g.exe", 0, 1000, completeSeconds: 1000, completeWatts: 300, cpuWatts: 100, gpuWatts: 200);
        var noCpu = Session("g.exe", 5000, 9000, partialSeconds: 9000, partialWatts: 150, cpuWatts: null, gpuWatts: 100);   // unelevated: no CPU data

        var game = GamingAnalytics.AggregateByGame([Summ(withCpu), Summ(noCpu)]).Single();

        Assert.Equal(100, game.AvgCpuWatts!.Value, 9);       // only the 1000 s that had CPU data count -- the 9000 s of "no data" are not zeros
        Assert.Equal((200.0 * 1000 + 100.0 * 9000) / 10_000, game.AvgGpuWatts!.Value, 9);
    }

    [Fact]
    public void CompleteAndPartialSessions_AreNeverMergedIntoOneEnergyOrCostTotal()
    {
        var complete = Session("g.exe", 0, 3600, completeSeconds: 3600, completeWatts: 400);
        var partial = Session("g.exe", 5000, 3600, partialSeconds: 3600, partialWatts: 250);

        var game = GamingAnalytics.AggregateByGame([Summ(complete), Summ(partial)]).Single();

        Assert.Equal(1, game.Complete.Sessions);
        Assert.Equal(1, game.Partial.Sessions);
        Assert.Equal(400.0 * 3600, game.Complete.SystemJoules, 6);        // the partial lower bound did not leak into the complete total
        Assert.Equal(250.0 * 3600, game.Partial.SystemJoules, 6);
        Assert.Equal(0.12m, game.Complete.Cost!.Value, 8);                  // 0.4 kWh x 0.30
        Assert.Null(game.Partial.Cost);                                    // a partial group has no cost, only a lower-bound energy
    }

    [Fact]
    public void Cost_IsSummedOnlyOverFullyPricedCompleteSessions()
    {
        var priced = Session("g.exe", 0, 3600, completeSeconds: 3600, completeWatts: 360);                         // 1.296 MJ = 0.36 kWh
        var unpriced = Session("g.exe", 5000, 3600, completeSeconds: 3600, completeWatts: 360, unpriced: true);   // consumed "before any price"
        var laterPrice = new ElectricityPriceSchedule([new ElectricityPrice { EffectiveFromUtc = At(900_000), PricePerKwh = 0.30m }]);

        var game = GamingAnalytics.AggregateByGame([Summ(priced), Summ(unpriced, laterPrice)], "EUR").Single();

        Assert.Equal(0.36m * 0.30m, game.Complete.Cost!.Value, 8);
        Assert.Equal(1, game.Complete.UnpricedSessions);
        Assert.Equal("EUR", game.Currency);
    }

    [Fact]
    public void PlayTime_MonitoredTime_AndAverageSessionLength_AreDistinct()
    {
        var a = Session("g.exe", 0, 3600, completeSeconds: 3000, completeWatts: 300);     // 600 s of the hour was not monitored
        var b = Session("g.exe", 10_000, 1800, completeSeconds: 1800, completeWatts: 300);

        var game = GamingAnalytics.AggregateByGame([Summ(a), Summ(b)]).Single();

        Assert.Equal(2, game.SessionCount);
        Assert.Equal(5400, game.TotalDurationSeconds, 6);
        Assert.Equal(4800, game.TotalMonitoredSeconds, 6);
        Assert.Equal(2700, game.AverageSessionDurationSeconds, 6);
    }

    [Fact]
    public void RunningSessions_AreNotAggregated_AndGamesAreKeptApart()
    {
        var done = Session("a.exe", 0, 100, completeSeconds: 100, completeWatts: 300);
        var live = Session("a.exe", 500, 100, completeSeconds: 100, completeWatts: 300, status: GamingSessionStatus.Active);
        var other = Session("b.exe", 0, 100, completeSeconds: 100, completeWatts: 300);

        var games = GamingAnalytics.AggregateByGame([Summ(done), Summ(live), Summ(other)]);

        Assert.Equal(2, games.Count);
        Assert.Equal(1, games.Single(g => g.GameKey == "a.exe").SessionCount);
    }

    [Fact]
    public void SessionsWithNoEstimate_AreCounted_ButContributeNoEnergy()
    {
        var none = Session("g.exe", 0, 600);                                                  // monitored nothing usable
        var good = Session("g.exe", 5000, 600, completeSeconds: 600, completeWatts: 300);

        var game = GamingAnalytics.AggregateByGame([Summ(none), Summ(good)]).Single();

        Assert.Equal(1, game.NoEstimateSessions);
        Assert.Equal(1, game.Complete.Sessions);
        Assert.Equal(0, game.Partial.Sessions);
    }

    [Fact]
    public void DisplayName_FollowsTheMostRecentSession()
    {
        var old = Session("cod.exe", 0, 100, completeSeconds: 100, completeWatts: 300, name: "Call of Duty");
        var renamed = Session("cod.exe", 1000, 100, completeSeconds: 100, completeWatts: 300, name: "Call of Duty: Warzone");

        Assert.Equal("Call of Duty: Warzone", GamingAnalytics.AggregateByGame([Summ(renamed), Summ(old)]).Single().DisplayName);
    }

    [Fact]
    public void PeakPower_IsTheMaximum_AcrossSessions()
    {
        var a = Session("g.exe", 0, 100, completeSeconds: 100, completeWatts: 300, peakCpu: 120, peakGpu: 250);
        var b = Session("g.exe", 500, 100, completeSeconds: 100, completeWatts: 300, peakCpu: 180, peakGpu: 210);

        var game = GamingAnalytics.AggregateByGame([Summ(a), Summ(b)]).Single();

        Assert.Equal(180, game.PeakCpuWatts);
        Assert.Equal(250, game.PeakGpuWatts);
    }

    // ---------------------------------------------------------------- per-session summary

    [Fact]
    public void Summary_OfAPartialSession_IsALowerBound_WithItsOwnLowerBoundCost()
    {
        var s = Summ(Session("g.exe", 0, 3600, partialSeconds: 3600, partialWatts: 250, gpuWatts: 200));

        Assert.Equal(EstimateCompleteness.Partial, s.Completeness);
        Assert.True(s.SystemIsLowerBound);
        Assert.Equal(250.0 * 3600, s.SystemJoules, 6);
        Assert.Equal(GamingCostStatus.WithheldPartial, s.CostStatus);
        Assert.Null(s.Cost);
        Assert.Equal(0.075m, s.LowerBoundCost!.Value, 8);                       // 0.25 kWh x 0.30
    }

    [Fact]
    public void ARunningSession_UsesNowForItsDuration_ARecoveredOneUsesItsLastObservation()
    {
        var running = Session("g.exe", 0, 100, completeSeconds: 100, completeWatts: 300, status: GamingSessionStatus.Active);
        var interrupted = running with { Status = GamingSessionStatus.Interrupted, EndedUtc = At(100) };

        Assert.Equal(1_000_000, GamingAnalytics.Summarize(running, Eur30, At(1_000_000)).DurationSeconds, 6);
        Assert.Equal(100, GamingAnalytics.Summarize(interrupted, Eur30, At(1_000_000)).DurationSeconds, 6);
    }
}

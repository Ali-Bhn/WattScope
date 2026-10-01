using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using static WattScope.Storage.Tests.TestDatabase;

namespace WattScope.Storage.Tests;

/// <summary>
/// End-to-end coverage of Milestone 003's sub-bucket price allocation
/// (docs/ENERGY_MODEL.md section 4) against a real SQLite database: a price
/// change that falls inside one 15-minute bucket is split from the actual
/// power curve, not the previous bucket-start-only rule.
///
/// The whole session is one bucket (BaseUtc is 15-minute aligned) with a
/// deliberate step in CPU power -- 100 W for the first 450 s, 300 W for the
/// second 450 s -- so an "assume uniform power, split by time" approximation
/// produces a visibly different (and wrong) number from the true split,
/// which is what distinguishes the three precision tiers below.
/// </summary>
public class PriceAllocationTests
{
    private static readonly EstimationSettings Settings = new() { OtherComponentsWatts = 0, PsuEfficiency = 1.0 };
    private static readonly DateTimeOffset PriceChange = BaseUtc.AddSeconds(450); // exactly mid-bucket
    private static readonly decimal PriceBefore = 0.30m;
    private static readonly decimal PriceAfter = 0.50m;

    // Hand-computed from the step profile: 449 s at 100 W (44 900 J) + one
    // blended second at (100+300)/2 W (200 J) = 45 100 J before the change;
    // 450 s at 300 W = 135 000 J after it. Total 180 100 J.
    private const double ExpectedJoulesBefore = 45_100;
    private const double ExpectedJoulesAfter = 135_000;

    private static IEnumerable<PowerSample> StepProfile()
    {
        for (var t = 0; t <= 449; t++) yield return Sample(t, 100, 0);
        for (var t = 450; t <= 900; t++) yield return Sample(t, 300, 0);
    }

    private static decimal ExpectedExactCost() =>
        CostCalculator.Cost(ExpectedJoulesBefore, PriceBefore) + CostCalculator.Cost(ExpectedJoulesAfter, PriceAfter);

    [Fact]
    public void PriceKnownDuringRecording_SplitsLiveFromTheRealPowerCurve_ExactPrecision()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(-1), PricePerKwh = PriceBefore }, BaseUtc);
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = PriceChange, PricePerKwh = PriceAfter }, BaseUtc);

        // Mirrors RecordCommand: the monitor is given the schedule that is already current at Start.
        var monitor = new EnergyMonitor(store, priceScheduleProvider: store.GetPriceSchedule);
        var id = monitor.Start(DevMachineBindings, Settings).Id;
        foreach (var s in StepProfile()) monitor.OnSample(s);
        monitor.Stop();

        var splits = store.GetPriceSplits(id);
        var bucket = Assert.Single(store.GetSessionBuckets(id));
        Assert.True(splits.ContainsKey((id, bucket.BucketStartUtc)), "a price change mid-bucket must produce live split rows");

        var report = BuildReport(store, id, knownSplits: splits, repriceBucket: null);
        Assert.Equal(PriceAllocationPrecision.Exact, report.Cost.Precision);
        Assert.Equal(ExpectedExactCost(), report.Cost.EstimatedCost);
    }

    [Fact]
    public void PriceAddedRetroactively_WithRawSamplesStillPresent_ReplaysExactly()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(-1), PricePerKwh = PriceBefore }, BaseUtc);

        // Only price1 is known during recording -- the whole bucket is
        // recorded under one price, so it gets exactly one split row.
        var monitor = new EnergyMonitor(store, priceScheduleProvider: store.GetPriceSchedule);
        var id = monitor.Start(DevMachineBindings, Settings).Id;
        foreach (var s in StepProfile()) monitor.OnSample(s);
        monitor.Stop();
        var recordedBucketKey = Assert.Single(store.GetSessionBuckets(id)).BucketStartUtc;
        var recordedSplit = Assert.Single(store.GetPriceSplits(id)[(id, recordedBucketKey)]);
        Assert.Equal(BaseUtc.AddDays(-1), recordedSplit.PriceEffectiveUtc);

        // The price change is added after the fact, into an already-persisted bucket --
        // the single recorded split above no longer matches the current schedule.
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = PriceChange, PricePerKwh = PriceAfter }, BaseUtc.AddHours(1));

        var estimation = store.GetSession(id)!.Estimation;
        var prices = store.GetPriceSchedule();
        var bucket = Assert.Single(store.GetSessionBuckets(id));
        IReadOnlyList<PricedEnergySegment>? Reprice(EnergyBucket b) => store.TryRepriceBucket(b, prices, estimation);

        var replayed = Reprice(bucket);
        Assert.NotNull(replayed);
        Assert.Equal(ExpectedJoulesBefore, replayed!.Single(s => s.PriceEffectiveUtc != PriceChange).Totals.EstimatedCompleteJoules, 6);
        Assert.Equal(ExpectedJoulesAfter, replayed.Single(s => s.PriceEffectiveUtc == PriceChange).Totals.EstimatedCompleteJoules, 6);

        // Pass the (now-stale) live-recorded splits too, exactly as
        // DatabaseCommands does in production -- AllocateBucket must detect
        // they no longer match the current schedule and fall through to
        // reprice, rather than trusting the stale single-price split.
        var report = BuildReport(store, id, knownSplits: store.GetPriceSplits(id), repriceBucket: Reprice);
        Assert.Equal(PriceAllocationPrecision.ExactReplayed, report.Cost.Precision);
        Assert.Equal(ExpectedExactCost(), report.Cost.EstimatedCost);
    }

    [Fact]
    public void PriceAddedRetroactively_AfterRawSamplesArePruned_FallsBackToApproximation_VisiblyDifferentFromExact()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(-1), PricePerKwh = PriceBefore }, BaseUtc);

        var monitor = new EnergyMonitor(store, priceScheduleProvider: store.GetPriceSchedule);
        var id = monitor.Start(DevMachineBindings, Settings).Id;
        foreach (var s in StepProfile()) monitor.OnSample(s);
        monitor.Stop();

        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = PriceChange, PricePerKwh = PriceAfter }, BaseUtc.AddHours(1));
        store.SetRawRetentionDays(0);
        store.PruneRawSamples(BaseUtc.AddYears(1)); // definitely past every raw sample
        Assert.Empty(store.GetSamples(id));

        var estimation = store.GetSession(id)!.Estimation;
        var prices = store.GetPriceSchedule();
        var bucket = Assert.Single(store.GetSessionBuckets(id));

        Assert.Null(store.TryRepriceBucket(bucket, prices, estimation)); // raw data is gone -- must refuse, not guess

        var report = BuildReport(store, id, knownSplits: store.GetPriceSplits(id), repriceBucket: b => store.TryRepriceBucket(b, prices, estimation));
        Assert.Equal(PriceAllocationPrecision.ApproximateProportional, report.Cost.Precision);

        // Uniform-power assumption: 180 100 J split 50/50 by time, not the true 45 100 / 135 000 split.
        var approximateExpected = CostCalculator.Cost(90_050, PriceBefore) + CostCalculator.Cost(90_050, PriceAfter);
        Assert.Equal(approximateExpected, report.Cost.EstimatedCost);
        Assert.NotEqual(ExpectedExactCost(), report.Cost.EstimatedCost);
    }

    private static EnergyReport BuildReport(
        SqliteEnergyStore store, long sessionId,
        IReadOnlyDictionary<(long SessionId, DateTimeOffset BucketStartUtc), IReadOnlyList<PricedEnergySegment>>? knownSplits,
        Func<EnergyBucket, IReadOnlyList<PricedEnergySegment>?>? repriceBucket)
    {
        var session = store.GetSession(sessionId)!;
        var end = session.EndedUtc ?? session.LastObservationUtc ?? session.StartedUtc;
        return EnergyReportBuilder.Build("s", session.StartedUtc, end,
            store.GetSessionBuckets(sessionId), store.GetSessionGaps(sessionId), store.GetPriceSchedule(), [session.Estimation],
            knownSplits, repriceBucket);
    }
}

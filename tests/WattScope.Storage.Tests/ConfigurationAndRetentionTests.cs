using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using Xunit.Abstractions;
using static WattScope.Storage.Tests.TestDatabase;

namespace WattScope.Storage.Tests;

public class ConfigurationAndRetentionTests(ITestOutputHelper output)
{
    [Fact]
    public void Prices_RoundTripExactly_AndPriceChangesAreKeptAsHistory()
    {
        using var db = new TestDatabase();
        using (var store = db.Open())
        {
            store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc, PricePerKwh = 0.3175m }, BaseUtc);
            store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(30), PricePerKwh = 0.2899m }, BaseUtc);
        }

        using (var store = db.Open())
        {
            var schedule = store.GetPriceSchedule();
            Assert.Equal(2, schedule.Entries.Count);
            Assert.Equal(0.3175m, schedule.PriceAt(BaseUtc.AddDays(1))!.PricePerKwh);
            Assert.Equal(0.2899m, schedule.PriceAt(BaseUtc.AddDays(31))!.PricePerKwh);
            Assert.Equal("EUR", schedule.Currency);
        }
    }

    [Fact]
    public void InvalidPrices_AreRejectedBeforeWriting()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc, PricePerKwh = 0.30m }, BaseUtc);

        Assert.Throws<ArgumentException>(() => store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(1), PricePerKwh = 0.30m, Currency = "USD" }, BaseUtc));
        Assert.Throws<ArgumentException>(() => store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc, PricePerKwh = 0.40m }, BaseUtc));
        Assert.Throws<ArgumentException>(() => store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(2), PricePerKwh = -1m }, BaseUtc));
        Assert.Single(store.GetPriceSchedule().Entries);
    }

    [Fact]
    public void EstimationSettings_DefaultUntilSaved_ThenLatestWins()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        Assert.Equal(new EstimationSettings(), store.GetEstimationSettings());

        store.SaveEstimationSettings(new EstimationSettings { OtherComponentsWatts = 35, PsuEfficiency = 0.92 }, BaseUtc);
        store.SaveEstimationSettings(new EstimationSettings { OtherComponentsWatts = 40, PsuEfficiency = 0.87, IncludeGpuBoard = false }, BaseUtc.AddMinutes(1));

        Assert.Equal(new EstimationSettings { OtherComponentsWatts = 40, PsuEfficiency = 0.87, IncludeGpuBoard = false }, store.GetEstimationSettings());
        Assert.ThrowsAny<ArgumentException>(() => store.SaveEstimationSettings(new EstimationSettings { PsuEfficiency = 0 }, BaseUtc));
    }

    [Fact]
    public void RetentionPruning_RemovesOldRawSamples_ButKeepsAggregates()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var monitor = new EnergyMonitor(store);
        var id = monitor.Start(DevMachineBindings, new EstimationSettings()).Id;
        foreach (var s in Constant(0, 600, 100, 100)) monitor.OnSample(s);
        monitor.Stop();
        var bucketsBefore = store.GetSessionBuckets(id);

        Assert.Equal(0, store.PruneRawSamples(BaseUtc.AddDays(6)));  // default retention 7 days
        store.SetRawRetentionDays(1);
        Assert.Equal(601, store.PruneRawSamples(BaseUtc.AddDays(6)));

        Assert.Empty(store.GetSamples(id));
        Assert.Equal(bucketsBefore, store.GetSessionBuckets(id));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetRawRetentionDays(-1));
    }

    [Fact]
    public void OneDayAtOneHertz_StaysSmall()
    {
        // The M001 full-sensor JSON export produced ~46 MB per 10 minutes. This
        // records 24 h of selected-channel samples at 1 Hz through the real monitor.
        using var db = new TestDatabase();
        long bytes;
        using (var store = db.Open())
        {
            var monitor = new EnergyMonitor(store);
            monitor.Start(DevMachineBindings, new EstimationSettings());
            var random = new Random(42);
            for (var t = 0; t <= 86_400; t++)
                monitor.OnSample(Sample(t, 30 + random.NextDouble() * 120, 40 + random.NextDouble() * 250));
            monitor.Stop();
            store.Checkpoint();
            var stats = store.GetStatistics();
            bytes = new FileInfo(db.FilePath).Length;

            Assert.Equal(86_401, stats.PowerSamples);
            Assert.Equal(96, stats.EnergyBuckets);
            output.WriteLine($"24 h at 1 Hz: {stats.PowerSamples} samples, {stats.EnergyBuckets} buckets, file {bytes / 1024.0 / 1024.0:F2} MB ({bytes / (double)stats.PowerSamples:F1} bytes/sample)");
        }

        Assert.True(bytes < 4 * 1024 * 1024, $"24 h of samples took {bytes} bytes");
    }
}

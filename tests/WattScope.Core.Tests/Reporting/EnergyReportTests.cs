using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;

namespace WattScope.Core.Tests.Reporting;

public class EnergyReportTests
{
    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02");

    private static EnergyBucket Bucket(DateTimeOffset start, double completeJoules, double monitored = 900, double completeSeconds = 900) => new()
    {
        SessionId = 1,
        BucketStartUtc = start,
        Totals = new EnergyTotals
        {
            MonitoredSeconds = monitored,
            EstimatedCompleteJoules = completeJoules,
            EstimatedCompleteSeconds = completeSeconds
        }
    };

    private static ElectricityPriceSchedule Price(decimal p) =>
        new([new ElectricityPrice { EffectiveFromUtc = DateTimeOffset.UnixEpoch, PricePerKwh = p }]);

    [Fact]
    public void LocalDay_UsesTheZonesMidnight()
    {
        var (start, end) = ReportPeriods.LocalDay(new DateOnly(2026, 3, 10), PlusTwo);

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 22, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 22, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void LocalDay_OnDstChange_Has23Hours()
    {
        TimeZoneInfo berlin;
        try { berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        catch (TimeZoneNotFoundException) { berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }

        var (start, end) = ReportPeriods.LocalDay(new DateOnly(2026, 3, 29), berlin);

        Assert.Equal(TimeSpan.FromHours(23), end - start);
    }

    [Fact]
    public void LocalMonth_CoversTheWholeCalendarMonth()
    {
        var (start, end) = ReportPeriods.LocalMonth(2026, 2, PlusTwo);

        Assert.Equal(new DateTimeOffset(2026, 1, 31, 22, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 22, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void DailyBreakdown_SplitsOnLocalMidnight()
    {
        var lastBucketOfDay = new DateTimeOffset(2026, 3, 9, 21, 45, 0, TimeSpan.Zero);  // 23:45 local
        var firstBucketOfNextDay = lastBucketOfDay.AddMinutes(15);                        // 00:00 local

        var days = EnergyReportBuilder.DailyBreakdown(
            [Bucket(lastBucketOfDay, 3_600_000), Bucket(firstBucketOfNextDay, 7_200_000)], PlusTwo, Price(0.30m));

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 3, 9), days[0].LocalDate);
        Assert.Equal(0.30m, days[0].Cost.EstimatedCost);
        Assert.Equal(new DateOnly(2026, 3, 10), days[1].LocalDate);
        Assert.Equal(0.60m, days[1].Cost.EstimatedCost);
    }

    [Fact]
    public void MonthlyTotal_IsRoundedOnce_FromUnroundedDailyAmounts()
    {
        // 3 days each costing 0.005 EUR: rounding each day gives 0.01 * 3 = 0.03, the true total is 0.015 -> 0.02.
        var joules = 0.005 / 0.30 * 3_600_000;
        var buckets = Enumerable.Range(0, 3)
            .Select(d => Bucket(new DateTimeOffset(2026, 3, 1 + d, 10, 0, 0, TimeSpan.Zero), joules))
            .ToList();
        var (start, end) = ReportPeriods.LocalMonth(2026, 3, PlusTwo);

        var month = EnergyReportBuilder.Build("March", start, end, buckets, [], Price(0.30m), []);
        var days = EnergyReportBuilder.DailyBreakdown(buckets, PlusTwo, Price(0.30m));

        Assert.Equal(0.02m, month.Cost.EstimatedCostRounded);
        Assert.All(days, d => Assert.Equal(0.01m, d.Cost.EstimatedCostRounded));
    }

    [Fact]
    public void Completeness_ReflectsEstimateCoverage()
    {
        var t = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var complete = EnergyReportBuilder.Build("c", t, t.AddDays(1), [Bucket(t, 1000)], [], Price(0.3m), []);
        var partial = EnergyReportBuilder.Build("p", t, t.AddDays(1), [Bucket(t, 1000, monitored: 900, completeSeconds: 450)], [], Price(0.3m), []);
        var none = EnergyReportBuilder.Build("n", t, t.AddDays(1), [Bucket(t, 0, monitored: 900, completeSeconds: 0)], [], Price(0.3m), []);

        Assert.Equal(EstimateCompleteness.Complete, complete.Completeness);
        Assert.Equal(EstimateCompleteness.Partial, partial.Completeness);
        Assert.Equal(EstimateCompleteness.None, none.Completeness);
        Assert.Null(none.Cost.EstimatedCost);
    }

    [Fact]
    public void Gaps_AreClippedToThePeriod()
    {
        var t = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var gaps = new[]
        {
            new MonitoringGap { StartUtc = t.AddHours(-1), EndUtc = t.AddHours(1), Reason = GapReason.Suspend },
            new MonitoringGap { StartUtc = t.AddDays(2), EndUtc = t.AddDays(3), Reason = GapReason.Suspend }
        };

        var report = EnergyReportBuilder.Build("d", t, t.AddDays(1), [], gaps, ElectricityPriceSchedule.Empty, []);

        Assert.Equal(1, report.GapCount);
        Assert.Equal(TimeSpan.FromHours(1), report.GapDuration);
    }

    [Fact]
    public void Assumptions_ListEachDistinctSettingsOnce()
    {
        var t = DateTimeOffset.UnixEpoch;
        var settings = new EstimationSettings();

        var report = EnergyReportBuilder.Build("x", t, t.AddDays(1), [], [], ElectricityPriceSchedule.Empty, [settings, settings]);

        Assert.Equal(settings.DescribeAssumptions().Count, report.Assumptions.Count);
    }
}

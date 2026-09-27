using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Pricing;

namespace WattScope.Core.Reporting;

/// <summary>Energy and cost for one period (a session, a local day, a local month).</summary>
public sealed record EnergyReport
{
    public required string Title { get; init; }
    public required DateTimeOffset PeriodStartUtc { get; init; }
    public required DateTimeOffset PeriodEndUtc { get; init; }

    /// <summary>Unrounded totals. Measured CPU/GPU energy and estimated wall energy are separate fields.</summary>
    public required EnergyTotals Totals { get; init; }

    public required int GapCount { get; init; }
    public required TimeSpan GapDuration { get; init; }

    /// <summary>Complete only if the complete wall estimate covers all monitored time.</summary>
    public required EstimateCompleteness Completeness { get; init; }

    public required CostBreakdown Cost { get; init; }

    /// <summary>Estimation assumptions in effect for the data in this report (one set per distinct settings).</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }
}

public sealed record DailyEnergy
{
    public required DateOnly LocalDate { get; init; }
    public required EnergyTotals Totals { get; init; }
    public required CostBreakdown Cost { get; init; }
}

public static class EnergyReportBuilder
{
    public static EnergyReport Build(
        string title,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        IReadOnlyList<EnergyBucket> buckets,
        IReadOnlyList<MonitoringGap> gaps,
        ElectricityPriceSchedule prices,
        IEnumerable<EstimationSettings> settingsUsed)
    {
        var totals = Sum(buckets);

        var gapCount = 0;
        var gapDuration = TimeSpan.Zero;
        foreach (var gap in gaps)
        {
            var start = gap.StartUtc > periodStartUtc ? gap.StartUtc : periodStartUtc;
            var end = gap.EndUtc < periodEndUtc ? gap.EndUtc : periodEndUtc;
            if (end <= start)
                continue;
            gapCount++;
            gapDuration += end - start;
        }

        var assumptions = settingsUsed.Distinct().SelectMany(s => s.DescribeAssumptions()).Distinct().ToList();

        return new EnergyReport
        {
            Title = title,
            PeriodStartUtc = periodStartUtc,
            PeriodEndUtc = periodEndUtc,
            Totals = totals,
            GapCount = gapCount,
            GapDuration = gapDuration,
            Completeness = CompletenessOf(totals),
            Cost = CostCalculator.ForBuckets(buckets, prices),
            Assumptions = assumptions
        };
    }

    /// <summary>Groups buckets by the local calendar date of their start in <paramref name="timeZone"/>.</summary>
    public static IReadOnlyList<DailyEnergy> DailyBreakdown(IReadOnlyList<EnergyBucket> buckets, TimeZoneInfo timeZone, ElectricityPriceSchedule prices) =>
        buckets
            .GroupBy(b => ReportPeriods.LocalDate(b.BucketStartUtc, timeZone))
            .OrderBy(g => g.Key)
            .Select(g => new DailyEnergy
            {
                LocalDate = g.Key,
                Totals = Sum(g),
                Cost = CostCalculator.ForBuckets(g, prices)
            })
            .ToList();

    public static EnergyTotals Sum(IEnumerable<EnergyBucket> buckets)
    {
        var totals = new EnergyTotals();
        foreach (var bucket in buckets)
            totals += bucket.Totals;
        return totals;
    }

    public static EstimateCompleteness CompletenessOf(EnergyTotals totals)
    {
        if (totals.EstimatedCompleteSeconds == 0 && totals.EstimatedPartialSeconds == 0)
            return EstimateCompleteness.None;
        // Tolerance only for floating-point summation order, not for missing data.
        return totals.EstimatedCompleteSeconds >= totals.MonitoredSeconds * (1 - 1e-9)
            ? EstimateCompleteness.Complete
            : EstimateCompleteness.Partial;
    }
}

public static class ReportPeriods
{
    public static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, timeZone).DateTime);

    /// <summary>[start, end) in UTC of a local calendar day. Handles DST-length days.</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) LocalDay(DateOnly date, TimeZoneInfo timeZone) =>
        (LocalMidnightUtc(date, timeZone), LocalMidnightUtc(date.AddDays(1), timeZone));

    /// <summary>[start, end) in UTC of a local calendar month.</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) LocalMonth(int year, int month, TimeZoneInfo timeZone)
    {
        var first = new DateOnly(year, month, 1);
        return (LocalMidnightUtc(first, timeZone), LocalMidnightUtc(first.AddMonths(1), timeZone));
    }

    private static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // In zones whose DST switch happens at midnight, local midnight may not
        // exist; the day then starts at the first valid local time.
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(15);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}

using WattScope.Core.Energy;

namespace WattScope.Core.Pricing;

public static class EnergyUnits
{
    public const decimal JoulesPerKwh = 3_600_000m;

    /// <summary>
    /// Joules (double, from integration) to kWh (decimal, for pricing). The
    /// double-to-decimal conversion keeps 15 significant digits; no other
    /// rounding is applied.
    /// </summary>
    public static decimal ToKwh(double joules) => (decimal)joules / JoulesPerKwh;

    public static double ToWh(double joules) => joules / 3600.0;
}

/// <summary>
/// The single rounding rule for money: 2 decimal places, midpoint rounded away
/// from zero (commercial rounding, e.g. 0.125 -> 0.13). Applied only to final,
/// displayed totals -- never to intermediate per-bucket or per-day amounts.
/// </summary>
public static class MoneyRounding
{
    public const int Decimals = 2;
    public const MidpointRounding Mode = MidpointRounding.AwayFromZero;

    public static decimal Round(decimal amount) => Math.Round(amount, Decimals, Mode);
}

public sealed record CostBreakdown
{
    public required string Currency { get; init; }

    /// <summary>Unrounded cost of the complete wall-energy estimate that had a price. Null when no complete estimated energy was priced.</summary>
    public decimal? EstimatedCost { get; init; }

    /// <summary><see cref="EstimatedCost"/> rounded with <see cref="MoneyRounding"/>.</summary>
    public decimal? EstimatedCostRounded => EstimatedCost is { } c ? MoneyRounding.Round(c) : null;

    public required double PricedJoules { get; init; }

    /// <summary>Complete estimated energy consumed before any price was configured. Not priced -- never priced at zero.</summary>
    public required double UnpricedJoules { get; init; }

    /// <summary>Unrounded cost of the partial (lower-bound) estimate, where priced. Reported separately; never added to <see cref="EstimatedCost"/>.</summary>
    public decimal? PartialEstimateCost { get; init; }

    public required IReadOnlyList<ElectricityPrice> PricesApplied { get; init; }
}

public static class CostCalculator
{
    public static decimal Cost(double joules, decimal pricePerKwh) => EnergyUnits.ToKwh(joules) * pricePerKwh;

    /// <summary>
    /// Prices each bucket with the entry in effect at the bucket's start and
    /// sums the unrounded amounts.
    /// </summary>
    public static CostBreakdown ForBuckets(IEnumerable<EnergyBucket> buckets, ElectricityPriceSchedule schedule)
    {
        decimal? estimated = null;
        decimal? partial = null;
        double pricedJoules = 0, unpricedJoules = 0;
        var applied = new HashSet<ElectricityPrice>();

        foreach (var bucket in buckets)
        {
            var totals = bucket.Totals;
            if (totals.EstimatedCompleteJoules == 0 && totals.EstimatedPartialJoules == 0)
                continue;

            var price = schedule.PriceAt(bucket.BucketStartUtc);
            if (price is null)
            {
                unpricedJoules += totals.EstimatedCompleteJoules;
                continue;
            }

            applied.Add(price);
            if (totals.EstimatedCompleteSeconds > 0)
            {
                estimated = (estimated ?? 0m) + Cost(totals.EstimatedCompleteJoules, price.PricePerKwh);
                pricedJoules += totals.EstimatedCompleteJoules;
            }
            if (totals.EstimatedPartialSeconds > 0)
                partial = (partial ?? 0m) + Cost(totals.EstimatedPartialJoules, price.PricePerKwh);
        }

        return new CostBreakdown
        {
            Currency = schedule.Currency,
            EstimatedCost = estimated,
            PricedJoules = pricedJoules,
            UnpricedJoules = unpricedJoules,
            PartialEstimateCost = partial,
            PricesApplied = applied.OrderBy(p => p.EffectiveFromUtc).ToList()
        };
    }
}

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

/// <summary>
/// How precisely a priced amount reflects the price in effect at the actual
/// time energy was consumed (see docs/ENERGY_MODEL.md section 4). Ordered
/// worst-last so a report can take the maximum across every bucket it sums.
/// </summary>
public enum PriceAllocationPrecision
{
    /// <summary>Either only one price applied throughout the bucket, or a price change inside it was split from the real power curve while recording (never reconstructed after the fact).</summary>
    Exact = 0,

    /// <summary>A price was added retroactively into an already-recorded bucket with no split tracking; the split was reconstructed by replaying raw samples that were still within retention.</summary>
    ExactReplayed = 1,

    /// <summary>A price was added retroactively into an already-recorded bucket whose raw samples are no longer available (past retention); the bucket's energy was split by wall-clock time proportion, assuming uniform average power -- an explicit approximation, not a measurement.</summary>
    ApproximateProportional = 2
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

    /// <summary>
    /// The least-exact allocation tier used by any bucket in this breakdown.
    /// <see cref="PriceAllocationPrecision.Exact"/> for the overwhelming
    /// majority of reports (no price ever changed mid-bucket, or it did and
    /// was split live). Only degrades when a price was added retroactively
    /// into an already-recorded bucket -- callers should surface this rather
    /// than presenting an approximation as exact.
    /// </summary>
    public required PriceAllocationPrecision Precision { get; init; }
}

/// <summary>One priced portion of a bucket's energy: some of the bucket's totals, charged at one price (or unpriced).</summary>
public sealed record PricedPiece
{
    public required EnergyTotals Totals { get; init; }
    public ElectricityPrice? Price { get; init; }
    public required PriceAllocationPrecision Precision { get; init; }
}

public static class CostCalculator
{
    public static decimal Cost(double joules, decimal pricePerKwh) => EnergyUnits.ToKwh(joules) * pricePerKwh;

    /// <summary>
    /// Splits one bucket's energy into priced pieces.
    ///
    /// <paramref name="knownSplits"/> -- recorded live, while the bucket was
    /// being sampled (<see cref="Energy.EnergyAccumulator.PendingPriceSplits"/>)
    /// -- always has at least one row per price that ever actually applied
    /// to the bucket while recording (one row for the ordinary single-price
    /// case). Those are used directly, with no reconstruction needed,
    /// <em>but only if they still agree with the current schedule</em>: if a
    /// price was since added <em>retroactively</em>, with an effective time
    /// inside this bucket, the schedule now shows a boundary the recorded
    /// splits never accounted for, so they are stale and must not be trusted.
    ///
    /// In that case (or when no known splits exist at all -- e.g. older data
    /// recorded before this feature existed), <paramref name="repriceBucket"/>
    /// is asked to attempt an exact replay from raw samples (only possible
    /// within raw retention); if it returns nothing, the bucket's energy is
    /// split proportionally by wall-clock time between the sub-periods the
    /// boundary defines -- an explicit, flagged approximation, never
    /// presented as exact (docs/ENERGY_MODEL.md section 4).
    /// </summary>
    public static IReadOnlyList<PricedPiece> AllocateBucket(
        EnergyBucket bucket,
        ElectricityPriceSchedule schedule,
        IReadOnlyList<PricedEnergySegment>? knownSplits = null,
        Func<EnergyBucket, IReadOnlyList<PricedEnergySegment>?>? repriceBucket = null)
    {
        var bucketEnd = bucket.BucketStartUtc + EnergyBuckets.DefaultBucketSize;
        var currentBoundaries = schedule.Entries.Select(p => p.EffectiveFromUtc)
            .Where(t => t > bucket.BucketStartUtc && t < bucketEnd).OrderBy(t => t).ToList();

        if (knownSplits is { Count: > 0 } && StillMatchesCurrentSchedule(knownSplits, schedule, bucket.BucketStartUtc, currentBoundaries))
            return knownSplits.Select(s => Piece(s, schedule, PriceAllocationPrecision.Exact)).ToList();

        if (currentBoundaries.Count == 0)
            return [new PricedPiece { Totals = bucket.Totals, Price = schedule.PriceAt(bucket.BucketStartUtc), Precision = PriceAllocationPrecision.Exact }];

        var replayed = repriceBucket?.Invoke(bucket);
        if (replayed is { Count: > 0 })
            return replayed.Select(s => Piece(s, schedule, PriceAllocationPrecision.ExactReplayed)).ToList();

        // Approximate fallback: split the bucket's stored energy proportionally
        // by wall-clock time across the sub-periods the price boundaries
        // define, assuming uniform average power across the bucket.
        var edges = new List<DateTimeOffset> { bucket.BucketStartUtc };
        edges.AddRange(currentBoundaries);
        edges.Add(bucketEnd);
        var bucketSeconds = (bucketEnd - bucket.BucketStartUtc).TotalSeconds;

        var pieces = new List<PricedPiece>(edges.Count - 1);
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var fraction = (edges[i + 1] - edges[i]).TotalSeconds / bucketSeconds;
            pieces.Add(new PricedPiece
            {
                Totals = Scale(bucket.Totals, fraction),
                Price = schedule.PriceAt(edges[i]),
                Precision = PriceAllocationPrecision.ApproximateProportional
            });
        }
        return pieces;
    }

    /// <summary>
    /// True if the price keys recorded live for this bucket are exactly the
    /// set the *current* schedule would still assign to it -- i.e. no price
    /// has been added or become visible since recording with an effective
    /// time that would have changed how this bucket was split. Prices are
    /// only ever appended (never edited or removed), so recorded splits can
    /// only fall behind the current schedule, never ahead of it.
    /// </summary>
    private static bool StillMatchesCurrentSchedule(
        IReadOnlyList<PricedEnergySegment> knownSplits, ElectricityPriceSchedule schedule, DateTimeOffset bucketStart, IReadOnlyList<DateTimeOffset> currentBoundaries)
    {
        var expected = new HashSet<DateTimeOffset?> { schedule.PriceAt(bucketStart)?.EffectiveFromUtc };
        foreach (var boundary in currentBoundaries)
            expected.Add(schedule.PriceAt(boundary)?.EffectiveFromUtc);
        var actual = knownSplits.Select(s => s.PriceEffectiveUtc).ToHashSet();
        return expected.SetEquals(actual);
    }

    private static PricedPiece Piece(PricedEnergySegment segment, ElectricityPriceSchedule schedule, PriceAllocationPrecision precision) => new()
    {
        Totals = segment.Totals,
        Price = segment.PriceEffectiveUtc is { } from ? schedule.Entries.FirstOrDefault(p => p.EffectiveFromUtc == from) : null,
        Precision = precision
    };

    private static EnergyTotals Scale(EnergyTotals t, double f) => new()
    {
        MonitoredSeconds = t.MonitoredSeconds * f,
        CpuPackageJoules = t.CpuPackageJoules * f,
        CpuPackageSeconds = t.CpuPackageSeconds * f,
        GpuBoardJoules = t.GpuBoardJoules * f,
        GpuBoardSeconds = t.GpuBoardSeconds * f,
        EstimatedCompleteJoules = t.EstimatedCompleteJoules * f,
        EstimatedCompleteSeconds = t.EstimatedCompleteSeconds * f,
        EstimatedPartialJoules = t.EstimatedPartialJoules * f,
        EstimatedPartialSeconds = t.EstimatedPartialSeconds * f
    };

    /// <summary>
    /// Prices every bucket (see <see cref="AllocateBucket"/> for the
    /// allocation rule) and sums the unrounded amounts.
    /// </summary>
    /// <param name="knownSplits">Live-recorded price splits, keyed by (session, bucket start). Omit if not available (e.g. older data).</param>
    /// <param name="repriceBucket">Attempts an exact raw-sample replay for a bucket with no known split whose current price schedule nonetheless shows a boundary inside it. Omit to always fall back to the proportional approximation for that (rare, retroactive-pricing) case.</param>
    public static CostBreakdown ForBuckets(
        IEnumerable<EnergyBucket> buckets,
        ElectricityPriceSchedule schedule,
        IReadOnlyDictionary<(long SessionId, DateTimeOffset BucketStartUtc), IReadOnlyList<PricedEnergySegment>>? knownSplits = null,
        Func<EnergyBucket, IReadOnlyList<PricedEnergySegment>?>? repriceBucket = null)
    {
        decimal? estimated = null;
        decimal? partial = null;
        double pricedJoules = 0, unpricedJoules = 0;
        var applied = new HashSet<ElectricityPrice>();
        var precision = PriceAllocationPrecision.Exact;

        foreach (var bucket in buckets)
        {
            var totals = bucket.Totals;
            if (totals.EstimatedCompleteJoules == 0 && totals.EstimatedPartialJoules == 0)
                continue;

            IReadOnlyList<PricedEnergySegment>? bucketSplits = null;
            knownSplits?.TryGetValue((bucket.SessionId, bucket.BucketStartUtc), out bucketSplits);
            foreach (var piece in AllocateBucket(bucket, schedule, bucketSplits, repriceBucket))
            {
                if (piece.Precision > precision)
                    precision = piece.Precision;

                if (piece.Price is not { } price)
                {
                    unpricedJoules += piece.Totals.EstimatedCompleteJoules;
                    continue;
                }

                applied.Add(price);
                if (piece.Totals.EstimatedCompleteSeconds > 0)
                {
                    estimated = (estimated ?? 0m) + Cost(piece.Totals.EstimatedCompleteJoules, price.PricePerKwh);
                    pricedJoules += piece.Totals.EstimatedCompleteJoules;
                }
                if (piece.Totals.EstimatedPartialSeconds > 0)
                    partial = (partial ?? 0m) + Cost(piece.Totals.EstimatedPartialJoules, price.PricePerKwh);
            }
        }

        return new CostBreakdown
        {
            Currency = schedule.Currency,
            EstimatedCost = estimated,
            PricedJoules = pricedJoules,
            UnpricedJoules = unpricedJoules,
            PartialEstimateCost = partial,
            PricesApplied = applied.OrderBy(p => p.EffectiveFromUtc).ToList(),
            Precision = precision
        };
    }
}

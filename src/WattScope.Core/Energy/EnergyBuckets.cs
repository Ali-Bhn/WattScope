using WattScope.Core.Estimation;
using WattScope.Core.Pricing;

namespace WattScope.Core.Energy;

public static class EnergyBuckets
{
    /// <summary>
    /// 15 minutes: every real-world UTC offset is a multiple of 15 minutes,
    /// so every bucket falls entirely within one local calendar day in any
    /// time zone, and daily/monthly totals can be summed from buckets exactly.
    /// </summary>
    public static readonly TimeSpan DefaultBucketSize = TimeSpan.FromMinutes(15);

    /// <summary>Start of the epoch-aligned bucket containing <paramref name="utc"/>.</summary>
    public static DateTimeOffset BucketStart(DateTimeOffset utc, TimeSpan bucketSize)
    {
        var sinceEpoch = utc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var floored = sinceEpoch - (((sinceEpoch % bucketSize.Ticks) + bucketSize.Ticks) % bucketSize.Ticks);
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + floored, TimeSpan.Zero);
    }
}

/// <summary>
/// Splits verified intervals into UTC buckets and accumulates their energy.
/// Holds only the buckets touched since the last <see cref="ClearPending"/>
/// (bounded memory: normally one or two open buckets).
/// </summary>
public sealed class EnergyAccumulator
{
    private readonly SortedDictionary<DateTimeOffset, EnergyTotals> _pending = new();

    /// <summary>
    /// Per-bucket, per-price-entry sub-totals. A bucket priced by one entry
    /// throughout its whole life ends up with exactly one entry here (all of
    /// it, however many flushes it took to accumulate); a bucket a price
    /// change fell inside gets more than one. The count can only be known
    /// once the bucket is fully accumulated, which a single flush's pending
    /// window cannot see -- so every piece is recorded here, and it is
    /// <see cref="PendingPriceSplits"/>'s caller (the storage layer, via an
    /// additive upsert keyed by (bucket, price)) that lets contributions from
    /// different flushes for the same bucket and price coalesce into one row.
    /// </summary>
    private readonly Dictionary<DateTimeOffset, Dictionary<DateTimeOffset, EnergyTotals>> _pendingPriceSplits = new();
    private readonly TimeSpan _bucketSize;

    /// <summary>Stands in for "unpriced" as a dictionary key (real price effective times are never this old); converted to/from null at the public boundary.</summary>
    private static readonly DateTimeOffset UnpricedKey = DateTimeOffset.MinValue;

    public EnergyAccumulator(TimeSpan? bucketSize = null)
    {
        _bucketSize = bucketSize ?? EnergyBuckets.DefaultBucketSize;
    }

    /// <summary>Totals for everything added since construction (not affected by <see cref="ClearPending"/>).</summary>
    public EnergyTotals RunTotals { get; private set; }

    /// <summary>
    /// Splits <paramref name="interval"/> at 15-minute bucket boundaries and,
    /// if <paramref name="prices"/> is given, additionally at any price-change
    /// instant it contains -- so a price change mid-bucket is allocated
    /// exactly, from the real power curve, while the data still exists (see
    /// docs/ENERGY_MODEL.md section 4). <paramref name="prices"/> may be a
    /// schedule that only reflects the price as of this call; the caller is
    /// expected to refresh it periodically, not on every sample, to avoid
    /// unnecessary polling.
    /// </summary>
    public void Add(PowerInterval interval, EstimationSettings settings, ElectricityPriceSchedule? prices = null)
    {
        var bucketBoundaries = BucketBoundariesWithin(interval.StartUtc, interval.EndUtc, _bucketSize);
        var priceBoundaries = prices is null
            ? []
            : prices.Entries.Select(p => p.EffectiveFromUtc).Where(t => t > interval.StartUtc && t < interval.EndUtc);

        foreach (var piece in interval.SplitAt(bucketBoundaries.Concat(priceBoundaries)))
        {
            var totals = EnergyTotals.FromInterval(piece, settings);
            var bucketKey = EnergyBuckets.BucketStart(piece.StartUtc, _bucketSize);
            _pending[bucketKey] = _pending.TryGetValue(bucketKey, out var existing) ? existing + totals : totals;
            RunTotals += totals;

            if (prices is null)
                continue;
            var priceKey = prices.PriceAt(piece.StartUtc)?.EffectiveFromUtc ?? UnpricedKey;
            if (!_pendingPriceSplits.TryGetValue(bucketKey, out var byPrice))
                _pendingPriceSplits[bucketKey] = byPrice = new Dictionary<DateTimeOffset, EnergyTotals>();
            byPrice[priceKey] = byPrice.TryGetValue(priceKey, out var existingSplit) ? existingSplit + totals : totals;
        }
    }

    private static IEnumerable<DateTimeOffset> BucketBoundariesWithin(DateTimeOffset start, DateTimeOffset end, TimeSpan bucketSize)
    {
        var boundary = EnergyBuckets.BucketStart(start, bucketSize) + bucketSize;
        while (boundary < end)
        {
            yield return boundary;
            boundary += bucketSize;
        }
    }

    /// <summary>Per-bucket deltas accumulated since the last successful flush.</summary>
    public IReadOnlyList<EnergyBucket> PendingDeltas(long sessionId) =>
        _pending.Select(kv => new EnergyBucket { SessionId = sessionId, BucketStartUtc = kv.Key, Totals = kv.Value }).ToList();

    /// <summary>
    /// Per-price-entry deltas accumulated since the last flush, one per
    /// (bucket, price) combination actually observed. The storage layer
    /// upserts these additively, so across a bucket's whole lifetime they
    /// coalesce into exactly one row per price that ever applied to it --
    /// one row for the common case of a single price throughout, more only
    /// when a price genuinely changed mid-bucket (see docs/ENERGY_MODEL.md
    /// section 4). Empty only when no price schedule was given to
    /// <see cref="Add"/> at all.
    /// </summary>
    public IReadOnlyList<PricedEnergySegment> PendingPriceSplits(long sessionId) =>
        _pendingPriceSplits
            .SelectMany(kv => kv.Value.Select(p => new PricedEnergySegment
            {
                SessionId = sessionId,
                BucketStartUtc = kv.Key,
                PriceEffectiveUtc = p.Key == UnpricedKey ? null : p.Key,
                Totals = p.Value
            }))
            .ToList();

    public void ClearPending()
    {
        _pending.Clear();
        _pendingPriceSplits.Clear();
    }
}

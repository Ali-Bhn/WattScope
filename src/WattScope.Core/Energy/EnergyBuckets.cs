using WattScope.Core.Estimation;

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
    private readonly TimeSpan _bucketSize;

    public EnergyAccumulator(TimeSpan? bucketSize = null)
    {
        _bucketSize = bucketSize ?? EnergyBuckets.DefaultBucketSize;
    }

    /// <summary>Totals for everything added since construction (not affected by <see cref="ClearPending"/>).</summary>
    public EnergyTotals RunTotals { get; private set; }

    public void Add(PowerInterval interval, EstimationSettings settings)
    {
        foreach (var piece in interval.SplitAtBoundaries(_bucketSize))
        {
            var totals = EnergyTotals.FromInterval(piece, settings);
            var key = EnergyBuckets.BucketStart(piece.StartUtc, _bucketSize);
            _pending[key] = _pending.TryGetValue(key, out var existing) ? existing + totals : totals;
            RunTotals += totals;
        }
    }

    /// <summary>Per-bucket deltas accumulated since the last successful flush.</summary>
    public IReadOnlyList<EnergyBucket> PendingDeltas(long sessionId) =>
        _pending.Select(kv => new EnergyBucket { SessionId = sessionId, BucketStartUtc = kv.Key, Totals = kv.Value }).ToList();

    public void ClearPending() => _pending.Clear();
}

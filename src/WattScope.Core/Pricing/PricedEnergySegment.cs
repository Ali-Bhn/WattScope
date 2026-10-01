using WattScope.Core.Energy;

namespace WattScope.Core.Pricing;

/// <summary>
/// Energy within one 15-minute bucket that was priced with one specific price
/// entry (see docs/ENERGY_MODEL.md section 4). Produced live, from the
/// actual per-sample power curve, by <see cref="EnergyAccumulator"/> --
/// never reconstructed after the fact from bucket aggregates alone (that data
/// no longer exists once samples are merged into a bucket total). A bucket
/// priced by a single entry throughout its whole 15 minutes has exactly one
/// of these (all of the bucket's energy, at that one price); a bucket a
/// price change fell inside has more than one -- that count is the signal
/// <see cref="Pricing.CostCalculator.AllocateBucket"/> uses to know whether a
/// bucket needs splitting at all.
/// </summary>
public sealed record PricedEnergySegment
{
    public required long SessionId { get; init; }
    public required DateTimeOffset BucketStartUtc { get; init; }

    /// <summary>The price entry in effect for this segment, or null if the segment's energy was consumed before any price existed (unpriced, never priced at zero).</summary>
    public DateTimeOffset? PriceEffectiveUtc { get; init; }

    /// <summary>
    /// Only the fields cost allocation actually needs
    /// (<see cref="EnergyTotals.EstimatedCompleteJoules"/>/<see cref="EnergyTotals.EstimatedCompleteSeconds"/>,
    /// <see cref="EnergyTotals.EstimatedPartialJoules"/>/<see cref="EnergyTotals.EstimatedPartialSeconds"/>)
    /// are populated when this segment came from storage; the rest are left at
    /// zero rather than duplicated -- <see cref="EnergyBucket"/> already carries
    /// the full per-channel totals for the bucket.
    /// </summary>
    public required EnergyTotals Totals { get; init; }
}

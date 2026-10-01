using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Pricing;

namespace WattScope.Core.Gaming;

/// <summary>
/// Energy and peaks attributed to one stretch of one gaming context. It is fed
/// verified <see cref="PowerInterval"/>s that the energy monitor already built
/// (so gaps, sleep and invalid readings are excluded by construction -- the
/// ledger can only ever hold energy that was actually integrated) and the raw
/// samples for peak tracking. No raw samples are copied or stored.
///
/// Energy is kept per electricity price (the price in effect when it was
/// consumed), so a price change inside a session is priced exactly, the same
/// rule the 15-minute buckets follow (docs/ENERGY_MODEL.md section 4).
/// </summary>
public sealed class GamingLedger
{
    /// <summary>Stands in for "unpriced" as a dictionary key; real price times are never this early.</summary>
    private const long Unpriced = long.MinValue;

    private sealed record Entry(EnergyTotals Totals, DateTimeOffset First, DateTimeOffset Last);

    private readonly Dictionary<long, Entry> _byPrice = new();

    public double? PeakCpuWatts { get; private set; }
    public double? PeakGpuWatts { get; private set; }
    public double? PeakSystemCompleteWatts { get; private set; }
    public double? PeakSystemPartialWatts { get; private set; }

    public EnergyTotals Totals
    {
        get
        {
            var total = new EnergyTotals();
            foreach (var entry in _byPrice.Values)
                total += entry.Totals;
            return total;
        }
    }

    public GamingPeaks Peaks => new()
    {
        CpuWatts = PeakCpuWatts,
        GpuWatts = PeakGpuWatts,
        SystemCompleteWatts = PeakSystemCompleteWatts,
        SystemPartialWatts = PeakSystemPartialWatts
    };

    public IReadOnlyList<GamingEnergyRow> Rows() =>
        _byPrice.OrderBy(kv => kv.Key)
            .Select(kv => new GamingEnergyRow(
                kv.Key == Unpriced ? null : DateTimeOffset.FromUnixTimeMilliseconds(kv.Key), kv.Value.Totals, kv.Value.First, kv.Value.Last))
            .ToList();

    /// <summary>Adds a verified interval (already clipped to this ledger's time range), split at any price change it contains.</summary>
    public void AddInterval(PowerInterval interval, EstimationSettings settings, ElectricityPriceSchedule? prices)
    {
        var priceBoundaries = prices is null
            ? []
            : prices.Entries.Select(p => p.EffectiveFromUtc).Where(t => t > interval.StartUtc && t < interval.EndUtc);

        foreach (var piece in interval.SplitAt(priceBoundaries))
        {
            var totals = EnergyTotals.FromInterval(piece, settings);
            var key = prices?.PriceAt(piece.StartUtc) is { } price ? price.EffectiveFromUtc.ToUnixTimeMilliseconds() : Unpriced;
            _byPrice[key] = _byPrice.TryGetValue(key, out var existing)
                ? new Entry(existing.Totals + totals, Min(existing.First, piece.StartUtc), Max(existing.Last, piece.EndUtc))
                : new Entry(totals, piece.StartUtc, piece.EndUtc);
        }
    }

    /// <summary>Tracks peak power. A missing channel never contributes (it is not zero).</summary>
    public void AddSample(PowerSample sample, EstimationSettings settings)
    {
        if (sample.CpuPackage.IsValid)
            PeakCpuWatts = Max(PeakCpuWatts, sample.CpuPackage.Watts!.Value);
        if (sample.GpuBoard.IsValid)
            PeakGpuWatts = Max(PeakGpuWatts, sample.GpuBoard.Watts!.Value);

        var wall = WallPowerEstimator.EstimateWatts(sample, settings);
        if (wall is { Value: { } watts, Completeness: EstimateCompleteness.Complete })
            PeakSystemCompleteWatts = Max(PeakSystemCompleteWatts, watts);
        else if (wall is { Value: { } partialWatts, Completeness: EstimateCompleteness.Partial })
            PeakSystemPartialWatts = Max(PeakSystemPartialWatts, partialWatts);
    }

    /// <summary>Folds another ledger (a grace-period tail that turned out to belong to the session) into this one.</summary>
    public void Merge(GamingLedger other)
    {
        foreach (var (key, entry) in other._byPrice)
            _byPrice[key] = _byPrice.TryGetValue(key, out var existing)
                ? new Entry(existing.Totals + entry.Totals, Min(existing.First, entry.First), Max(existing.Last, entry.Last))
                : entry;
        PeakCpuWatts = Max(PeakCpuWatts, other.PeakCpuWatts);
        PeakGpuWatts = Max(PeakGpuWatts, other.PeakGpuWatts);
        PeakSystemCompleteWatts = Max(PeakSystemCompleteWatts, other.PeakSystemCompleteWatts);
        PeakSystemPartialWatts = Max(PeakSystemPartialWatts, other.PeakSystemPartialWatts);
    }

    private static double? Max(double? current, double? candidate) =>
        candidate is null ? current : current is null ? candidate : Math.Max(current.Value, candidate.Value);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}

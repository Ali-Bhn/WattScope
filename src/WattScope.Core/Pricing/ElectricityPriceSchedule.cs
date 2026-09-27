namespace WattScope.Core.Pricing;

/// <summary>A flat per-kWh price that applies from <see cref="EffectiveFromUtc"/> until the next entry.</summary>
public sealed record ElectricityPrice
{
    public required DateTimeOffset EffectiveFromUtc { get; init; }

    /// <summary>Price per kWh in <see cref="Currency"/>. Stored and computed as decimal, never double.</summary>
    public required decimal PricePerKwh { get; init; }

    public string Currency { get; init; } = ElectricityPriceSchedule.DefaultCurrency;
}

/// <summary>
/// Time-ordered price history. A price change never rewrites the past: energy
/// is always priced with the entry in effect at the time it was consumed
/// (at bucket granularity -- see docs/ENERGY_MODEL.md, "Electricity cost").
/// </summary>
public sealed class ElectricityPriceSchedule
{
    public const string DefaultCurrency = "EUR";

    public ElectricityPriceSchedule(IEnumerable<ElectricityPrice> prices)
    {
        var ordered = prices.OrderBy(p => p.EffectiveFromUtc).ToList();

        foreach (var price in ordered)
        {
            if (price.PricePerKwh < 0)
                throw new ArgumentException($"Negative electricity price ({price.PricePerKwh}) is not supported.", nameof(prices));
            if (price.Currency is not { Length: 3 } || !price.Currency.All(char.IsAsciiLetterUpper))
                throw new ArgumentException($"Currency must be a 3-letter ISO 4217 code, got '{price.Currency}'.", nameof(prices));
        }

        var currencies = ordered.Select(p => p.Currency).Distinct().ToList();
        if (currencies.Count > 1)
            throw new ArgumentException($"A price schedule must use a single currency; found {string.Join(", ", currencies)}.", nameof(prices));

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].EffectiveFromUtc == ordered[i - 1].EffectiveFromUtc)
                throw new ArgumentException($"Two prices share the effective time {ordered[i].EffectiveFromUtc:O}.", nameof(prices));
        }

        Entries = ordered;
        Currency = currencies.Count == 1 ? currencies[0] : DefaultCurrency;
    }

    public static ElectricityPriceSchedule Empty { get; } = new([]);

    public IReadOnlyList<ElectricityPrice> Entries { get; }

    public string Currency { get; }

    /// <summary>The price in effect at <paramref name="utc"/>, or null if no price had been configured yet at that time.</summary>
    public ElectricityPrice? PriceAt(DateTimeOffset utc)
    {
        ElectricityPrice? result = null;
        foreach (var entry in Entries)
        {
            if (entry.EffectiveFromUtc > utc)
                break;
            result = entry;
        }
        return result;
    }
}

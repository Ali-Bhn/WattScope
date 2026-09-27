using WattScope.Core.Energy;
using WattScope.Core.Pricing;

namespace WattScope.Core.Tests.Pricing;

public class CostCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static EnergyBucket Bucket(DateTimeOffset start, double completeJoules, double partialJoules = 0) => new()
    {
        SessionId = 1,
        BucketStartUtc = start,
        Totals = new EnergyTotals
        {
            MonitoredSeconds = 900,
            EstimatedCompleteJoules = completeJoules,
            EstimatedCompleteSeconds = completeJoules > 0 ? 900 : 0,
            EstimatedPartialJoules = partialJoules,
            EstimatedPartialSeconds = partialJoules > 0 ? 900 : 0
        }
    };

    private static ElectricityPriceSchedule Schedule(params (DateTimeOffset From, decimal Price)[] prices) =>
        new(prices.Select(p => new ElectricityPrice { EffectiveFromUtc = p.From, PricePerKwh = p.Price }));

    [Fact]
    public void OneKilowattHour_AtThirtyCents_CostsExactlyThirtyCents()
    {
        Assert.Equal(0.30m, CostCalculator.Cost(3_600_000, 0.30m));
    }

    [Fact]
    public void CostSums_UseDecimal_WithoutBinaryFloatingPointError()
    {
        // 0.1 kWh + 0.2 kWh at 0.3 EUR/kWh: exactly 0.09 in decimal.
        var cost = CostCalculator.ForBuckets(
            [Bucket(T0, 360_000), Bucket(T0.AddMinutes(15), 720_000)],
            Schedule((T0, 0.3m)));

        Assert.Equal(0.09m, cost.EstimatedCost);
        Assert.Equal("EUR", cost.Currency);
    }

    [Theory]
    [InlineData("0.125", "0.13")]
    [InlineData("0.135", "0.14")]
    [InlineData("0.124999", "0.12")]
    [InlineData("2.675", "2.68")]
    [InlineData("-0.125", "-0.13")]
    public void MoneyRounding_IsTwoDecimals_MidpointAwayFromZero(string input, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            MoneyRounding.Round(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Rounding_IsAppliedOnceToTheTotal_NotPerBucket()
    {
        // 1000 buckets each costing 0.004 EUR: rounding each would give 0.00.
        var joulesFor0_004Eur = 0.004 / 0.4 * 3_600_000; // 36 000 J at 0.40 EUR/kWh
        var buckets = Enumerable.Range(0, 1000).Select(i => Bucket(T0.AddMinutes(15 * i), joulesFor0_004Eur)).ToList();

        var cost = CostCalculator.ForBuckets(buckets, Schedule((T0, 0.40m)));

        Assert.Equal(4.00m, cost.EstimatedCostRounded);
    }

    [Fact]
    public void PriceChange_AppliesFromItsEffectiveTime_AndDoesNotRewriteThePast()
    {
        var change = T0.AddHours(10).AddMinutes(30);
        var before = Bucket(T0.AddHours(10), 3_600_000);  // 1 kWh
        var after = Bucket(T0.AddHours(11), 3_600_000);   // 1 kWh
        var schedule = Schedule((T0, 0.30m), (change, 0.40m));

        Assert.Equal(0.30m, CostCalculator.ForBuckets([before], schedule).EstimatedCost);
        Assert.Equal(0.40m, CostCalculator.ForBuckets([after], schedule).EstimatedCost);
        var both = CostCalculator.ForBuckets([before, after], schedule);
        Assert.Equal(0.70m, both.EstimatedCost);
        Assert.Equal(2, both.PricesApplied.Count);
    }

    [Fact]
    public void PriceEffectiveExactlyAtBucketStart_AppliesToThatBucket()
    {
        var schedule = Schedule((T0, 0.30m), (T0.AddHours(1), 0.50m));

        Assert.Equal(0.50m, CostCalculator.ForBuckets([Bucket(T0.AddHours(1), 3_600_000)], schedule).EstimatedCost);
    }

    [Fact]
    public void EnergyBeforeAnyPrice_IsUnpriced_NotFree()
    {
        var schedule = Schedule((T0.AddDays(1), 0.30m));

        var cost = CostCalculator.ForBuckets([Bucket(T0, 3_600_000)], schedule);

        Assert.Null(cost.EstimatedCost);
        Assert.Equal(3_600_000, cost.UnpricedJoules);
        Assert.Equal(0, cost.PricedJoules);
    }

    [Fact]
    public void NoPriceConfigured_GivesNoCost()
    {
        var cost = CostCalculator.ForBuckets([Bucket(T0, 3_600_000)], ElectricityPriceSchedule.Empty);

        Assert.Null(cost.EstimatedCost);
        Assert.Null(cost.EstimatedCostRounded);
    }

    [Fact]
    public void PartialEstimateCost_IsReportedSeparately_AndNeverAddedToTheEstimate()
    {
        var cost = CostCalculator.ForBuckets(
            [Bucket(T0, 3_600_000), Bucket(T0.AddMinutes(15), 0, partialJoules: 3_600_000)],
            Schedule((T0, 0.30m)));

        Assert.Equal(0.30m, cost.EstimatedCost);
        Assert.Equal(0.30m, cost.PartialEstimateCost);
    }

    [Fact]
    public void Schedule_RejectsNegativePrices_MixedCurrencies_DuplicateTimes_AndBadCodes()
    {
        Assert.Throws<ArgumentException>(() => Schedule((T0, -0.1m)));
        Assert.Throws<ArgumentException>(() => new ElectricityPriceSchedule(
        [
            new ElectricityPrice { EffectiveFromUtc = T0, PricePerKwh = 0.3m, Currency = "EUR" },
            new ElectricityPrice { EffectiveFromUtc = T0.AddDays(1), PricePerKwh = 0.3m, Currency = "USD" }
        ]));
        Assert.Throws<ArgumentException>(() => Schedule((T0, 0.3m), (T0, 0.4m)));
        Assert.Throws<ArgumentException>(() => new ElectricityPriceSchedule(
            [new ElectricityPrice { EffectiveFromUtc = T0, PricePerKwh = 0.3m, Currency = "eur" }]));
    }

    [Fact]
    public void DefaultCurrency_IsEur()
    {
        Assert.Equal("EUR", ElectricityPriceSchedule.Empty.Currency);
        Assert.Equal("EUR", new ElectricityPrice { EffectiveFromUtc = T0, PricePerKwh = 1m }.Currency);
    }

    [Fact]
    public void ToKwh_DoesNotRoundIntermediateEnergy()
    {
        // 1 J is 1/3 600 000 kWh; the decimal keeps it rather than rounding to 0.
        Assert.Equal(1m / 3_600_000m, EnergyUnits.ToKwh(1));
    }
}

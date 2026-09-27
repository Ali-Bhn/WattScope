using System.Globalization;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;

namespace WattScope.Probe;

public static class EnergyConsoleReporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Wh(double joules) => EnergyUnits.ToWh(joules).ToString("0.000", Inv) + " Wh";

    public static string Watts(ChannelReading reading) =>
        reading.IsValid ? reading.Watts!.Value.ToString("0.0", Inv) + " W" : $"n/a ({reading.State})";

    public static string Money(decimal amount, string currency) => amount.ToString("0.00", Inv) + " " + currency;

    public static string Local(DateTimeOffset utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Inv);

    public static string Duration(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private static string Coverage(double seconds, double monitored) =>
        monitored > 0 ? (seconds / monitored * 100).ToString("0.0", Inv) + " %" : "n/a";

    public static void PrintReport(EnergyReport report)
    {
        var t = report.Totals;
        Console.WriteLine($"=== {report.Title} ===");
        Console.WriteLine($"  Period:                {Local(report.PeriodStartUtc)} -> {Local(report.PeriodEndUtc)} (local time, {TimeZoneInfo.Local.Id})");
        Console.WriteLine($"  Monitored time:        {Duration(TimeSpan.FromSeconds(t.MonitoredSeconds))}   gaps: {report.GapCount} ({Duration(report.GapDuration)}, no energy recorded)");
        Console.WriteLine();
        Console.WriteLine("  Measured (sensor readings, integrated):");
        Console.WriteLine($"    CPU package:         {Wh(t.CpuPackageJoules),14}   coverage {Coverage(t.CpuPackageSeconds, t.MonitoredSeconds)}");
        Console.WriteLine($"    GPU board:           {Wh(t.GpuBoardJoules),14}   coverage {Coverage(t.GpuBoardSeconds, t.MonitoredSeconds)}");
        Console.WriteLine();
        Console.WriteLine("  ESTIMATED wall energy (not measured -- see assumptions):");
        Console.WriteLine($"    Complete estimate:   {Wh(t.EstimatedCompleteJoules),14}   coverage {Coverage(t.EstimatedCompleteSeconds, t.MonitoredSeconds)}   completeness: {report.Completeness}");
        if (t.EstimatedPartialSeconds > 0)
            Console.WriteLine($"    Partial (lower bound, some inputs missing; excluded from cost): {Wh(t.EstimatedPartialJoules)} over {Duration(TimeSpan.FromSeconds(t.EstimatedPartialSeconds))}");
        if (report.Completeness == EstimateCompleteness.None && t.MonitoredSeconds > 0)
            Console.WriteLine("    No complete estimate exists for this period: a required input (usually CPU package power) was never valid.");
        Console.WriteLine();
        PrintCost(report.Cost);
        if (report.Assumptions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Assumptions:");
            foreach (var line in report.Assumptions)
                Console.WriteLine($"    - {line}");
        }
    }

    public static void PrintCost(CostBreakdown cost)
    {
        Console.WriteLine("  Estimated cost (of the complete wall-energy estimate):");
        if (cost.EstimatedCost is { } exact)
        {
            Console.WriteLine($"    Cost:                {Money(cost.EstimatedCostRounded!.Value, cost.Currency)}   (unrounded {exact.ToString("0.########", Inv)} {cost.Currency}; rounded half away from zero to 2 decimals)");
            foreach (var price in cost.PricesApplied)
                Console.WriteLine($"    Price applied:       {price.PricePerKwh.ToString(Inv)} {price.Currency}/kWh (effective {Local(price.EffectiveFromUtc)})");
        }
        else
        {
            Console.WriteLine("    Cost:                n/a" + (cost.UnpricedJoules > 0 ? "" : " (no priced complete estimate in this period)"));
        }
        if (cost.UnpricedJoules > 0)
            Console.WriteLine($"    Unpriced energy:     {Wh(cost.UnpricedJoules)} consumed before any price was configured (not priced at zero). Use --set-price with --effective.");
        if (cost.PartialEstimateCost is { } partial)
            Console.WriteLine($"    Partial estimate cost (lower bound, not included above): {partial.ToString("0.########", Inv)} {cost.Currency}");
    }

    public static void PrintDaily(IReadOnlyList<DailyEnergy> days, string currency)
    {
        Console.WriteLine("  Daily breakdown (local dates; each day rounded individually for display only -- totals are rounded once from unrounded sums):");
        Console.WriteLine($"    {"Date",-10}  {"Monitored",9}  {"CPU pkg",13}  {"GPU board",13}  {"Est. wall",13}  {"Est. cost",12}");
        foreach (var day in days)
        {
            var t = day.Totals;
            var cost = day.Cost.EstimatedCostRounded is { } c ? Money(c, currency) : "n/a";
            Console.WriteLine($"    {day.LocalDate:yyyy-MM-dd}  {Duration(TimeSpan.FromSeconds(t.MonitoredSeconds)),9}  {Wh(t.CpuPackageJoules),13}  {Wh(t.GpuBoardJoules),13}  {Wh(t.EstimatedCompleteJoules),13}  {cost,12}");
        }
    }

    public static void PrintSessionLine(MonitoringSession session, EnergyTotals totals)
    {
        var end = session.EndedUtc ?? session.LastObservationUtc;
        Console.WriteLine(
            $"  #{session.Id,-5} {session.Status,-11} {Local(session.StartedUtc)} -> {(end is { } e ? Local(e) : "(running)"),-19}  " +
            $"monitored {Duration(TimeSpan.FromSeconds(totals.MonitoredSeconds))}  est. wall {Wh(totals.EstimatedCompleteJoules)} ({EnergyReportBuilder.CompletenessOf(totals)})");
    }
}

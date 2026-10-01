using System.Globalization;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using WattScope.Storage;

namespace WattScope.Probe.Commands;

/// <summary>Commands that only read or configure the database; none of them touch hardware.</summary>
public static class DatabaseCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(CliOptions options, SqliteEnergyStore store)
    {
        RecoverIfNoRecorderIsRunning(store);

        return options.Command switch
        {
            CliCommand.Sessions => Sessions(store),
            CliCommand.Summary => Summary(options, store),
            CliCommand.SetPrice => SetPrice(options, store),
            CliCommand.Prices => Prices(store),
            CliCommand.Estimation => Estimation(options, store),
            CliCommand.Retention => Retention(options, store),
            CliCommand.DbInfo => DbInfo(store),
            CliCommand.Gaming => GamingCommands.List(store),
            CliCommand.GameMark or CliCommand.GameExclude or CliCommand.GameUnmark => GamingCommands.Mapping(options, store),
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }

    /// <summary>
    /// If no process holds the monitor lock, any Active session was orphaned by
    /// a crash: recover it so reports show it as Interrupted rather than running.
    /// </summary>
    public static void RecoverIfNoRecorderIsRunning(SqliteEnergyStore store)
    {
        using var monitorLock = MonitorLock.TryAcquire(store.DatabasePath);
        if (monitorLock is null)
            return;
        foreach (var session in store.RecoverInterruptedSessions())
            Console.WriteLine($"Recovered session #{session.Id}: the recording process ended unexpectedly; energy is kept up to its last saved observation ({EnergyConsoleReporter.Local(session.LastObservationUtc ?? session.StartedUtc)}).");
    }

    private static int Sessions(SqliteEnergyStore store)
    {
        var sessions = store.ListSessions(20);
        if (sessions.Count == 0)
        {
            Console.WriteLine("No sessions recorded yet. Start one with --record.");
            return 0;
        }
        Console.WriteLine($"Most recent sessions ({store.DatabasePath}):");
        foreach (var session in sessions)
            EnergyConsoleReporter.PrintSessionLine(session, EnergyReportBuilder.Sum(store.GetSessionBuckets(session.Id)));
        return 0;
    }

    private static int Summary(CliOptions options, SqliteEnergyStore store)
    {
        var prices = store.GetPriceSchedule();
        var tz = TimeZoneInfo.Local;

        if (options.Day is { } day)
        {
            var (start, end) = ReportPeriods.LocalDay(day, tz);
            PrintPeriod($"Day {day:yyyy-MM-dd}", start, end, store, prices, tz, daily: false);
            return 0;
        }

        if (options.Month is { } month)
        {
            var (start, end) = ReportPeriods.LocalMonth(month.Year, month.Month, tz);
            PrintPeriod($"Month {month.Year:0000}-{month.Month:00}", start, end, store, prices, tz, daily: true);
            return 0;
        }

        var session = options.SessionId is { } id ? store.GetSession(id) : store.GetLatestSession();
        if (session is null)
        {
            Console.Error.WriteLine(options.SessionId is { } missing ? $"Session {missing} does not exist." : "No sessions recorded yet.");
            return 1;
        }
        PrintSession(store, session, prices);
        return 0;
    }

    /// <summary>Builds the same <see cref="EnergyReport"/> both the text CLI and the JSON --serve protocol use for one session -- the only difference is what each does with it afterwards.</summary>
    public static EnergyReport BuildSessionReport(SqliteEnergyStore store, MonitoringSession session, ElectricityPriceSchedule prices)
    {
        var end = session.EndedUtc ?? session.LastObservationUtc ?? session.StartedUtc;
        return EnergyReportBuilder.Build(
            $"Session #{session.Id} ({session.Status}{(session.EndReason is { } r ? $": {r}" : "")})",
            session.StartedUtc, end,
            store.GetSessionBuckets(session.Id), store.GetSessionGaps(session.Id), prices, [session.Estimation],
            store.GetPriceSplits(session.Id),
            bucket => store.TryRepriceBucket(bucket, prices, session.Estimation));
    }

    public static void PrintSession(SqliteEnergyStore store, MonitoringSession session, ElectricityPriceSchedule prices)
    {
        EnergyConsoleReporter.PrintReport(BuildSessionReport(store, session, prices));
        var gaps = store.GetSessionGaps(session.Id);
        if (gaps.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Monitoring gaps (no energy is recorded for them):");
            foreach (var gap in gaps)
                Console.WriteLine($"    {gap.Reason,-18} {EnergyConsoleReporter.Local(gap.StartUtc)} -> {EnergyConsoleReporter.Local(gap.EndUtc)}  ({EnergyConsoleReporter.Duration(gap.Duration)})");
        }
        Console.WriteLine();
        Console.WriteLine("  Sensors used:");
        foreach (var binding in session.Sensors)
            Console.WriteLine($"    {binding.Channel}: {binding.HardwareName} / {binding.SensorName} [{binding.ProviderId}]");
        if (session.Sensors.Count == 0)
            Console.WriteLine("    (none -- no CPU package or GPU board sensor was selectable)");
    }

    /// <summary>Builds the same period report (and, for a month, its daily breakdown) both the text CLI and the JSON --serve protocol use.</summary>
    public static (EnergyReport Report, IReadOnlyList<DailyEnergy>? Daily) BuildPeriodReport(
        string title, DateTimeOffset start, DateTimeOffset end, SqliteEnergyStore store, ElectricityPriceSchedule prices, TimeZoneInfo tz, bool daily)
    {
        var buckets = store.GetBuckets(start, end);
        var sessions = store.GetSessions(buckets.Select(b => b.SessionId));
        var estimationBySession = sessions.ToDictionary(s => s.Id, s => s.Estimation);
        var knownSplits = store.GetPriceSplits(start, end);
        IReadOnlyList<Core.Pricing.PricedEnergySegment>? Reprice(EnergyBucket bucket) =>
            estimationBySession.TryGetValue(bucket.SessionId, out var estimation) ? store.TryRepriceBucket(bucket, prices, estimation) : null;

        var report = EnergyReportBuilder.Build(title, start, end, buckets, store.GetGaps(start, end), prices, sessions.Select(s => s.Estimation), knownSplits, Reprice);
        var dailyBreakdown = daily && buckets.Count > 0 ? EnergyReportBuilder.DailyBreakdown(buckets, tz, prices, knownSplits, Reprice) : null;
        return (report, dailyBreakdown);
    }

    private static void PrintPeriod(string title, DateTimeOffset start, DateTimeOffset end, SqliteEnergyStore store, ElectricityPriceSchedule prices, TimeZoneInfo tz, bool daily)
    {
        var (report, dailyBreakdown) = BuildPeriodReport(title, start, end, store, prices, tz, daily);
        EnergyConsoleReporter.PrintReport(report);
        if (dailyBreakdown is { Count: > 0 })
        {
            Console.WriteLine();
            EnergyConsoleReporter.PrintDaily(dailyBreakdown, prices.Currency);
        }
        if (report.Totals.MonitoredSeconds == 0)
            Console.WriteLine("  (no recorded energy in this period)");
    }

    private static int SetPrice(CliOptions options, SqliteEnergyStore store)
    {
        var now = DateTimeOffset.UtcNow;
        var price = new ElectricityPrice
        {
            EffectiveFromUtc = options.EffectiveFrom ?? now,
            PricePerKwh = options.Price!.Value,
            Currency = options.Currency
        };
        try
        {
            store.AddPrice(price, now);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Price not saved: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"Saved: {price.PricePerKwh.ToString(Inv)} {price.Currency}/kWh effective from {EnergyConsoleReporter.Local(price.EffectiveFromUtc)} (local).");
        Console.WriteLine("Energy is priced at the effective time of consumption: a price change mid-session is split exactly from the real power curve, not rounded to a 15-minute bucket boundary.");
        return 0;
    }

    private static int Prices(SqliteEnergyStore store)
    {
        var schedule = store.GetPriceSchedule();
        if (schedule.Entries.Count == 0)
        {
            Console.WriteLine("No electricity price configured. Costs are reported as unavailable (never as zero). Set one with --set-price <EUR/kWh>.");
            return 0;
        }
        Console.WriteLine("Electricity price history (each applies until the next):");
        foreach (var entry in schedule.Entries)
            Console.WriteLine($"  from {EnergyConsoleReporter.Local(entry.EffectiveFromUtc)}  {entry.PricePerKwh.ToString(Inv)} {entry.Currency}/kWh");
        return 0;
    }

    private static int Estimation(CliOptions options, SqliteEnergyStore store)
    {
        var current = store.GetEstimationSettings();
        var changed = options.OtherWatts is not null || options.PsuEfficiency is not null || options.IncludeCpu is not null || options.IncludeGpu is not null;
        if (changed)
        {
            var updated = current with
            {
                OtherComponentsWatts = options.OtherWatts ?? current.OtherComponentsWatts,
                PsuEfficiency = options.PsuEfficiency ?? current.PsuEfficiency,
                IncludeCpuPackage = options.IncludeCpu ?? current.IncludeCpuPackage,
                IncludeGpuBoard = options.IncludeGpu ?? current.IncludeGpuBoard
            };
            try
            {
                store.SaveEstimationSettings(updated, DateTimeOffset.UtcNow);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Settings not saved: {ex.Message}");
                return 1;
            }
            current = updated;
            Console.WriteLine("Saved. New settings apply to sessions started from now on; existing sessions keep the settings they were recorded with.");
        }

        Console.WriteLine("Wall-power estimation settings:");
        foreach (var line in current.DescribeAssumptions())
            Console.WriteLine($"  - {line}");
        Console.WriteLine($"  - Required measured inputs: CPU package={current.IncludeCpuPackage}, GPU board={current.IncludeGpuBoard}");
        return 0;
    }

    private static int Retention(CliOptions options, SqliteEnergyStore store)
    {
        if (options.RetentionDays is { } days)
        {
            try
            {
                store.SetRawRetentionDays(days);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            var pruned = store.PruneRawSamples(DateTimeOffset.UtcNow);
            Console.WriteLine($"Raw sample retention set to {days} day(s); {pruned} older sample(s) pruned.");
        }
        else
        {
            Console.WriteLine($"Raw samples are kept for {store.GetRawRetentionDays()} day(s). 15-minute energy aggregates, gaps and availability are kept indefinitely.");
        }
        return 0;
    }

    private static int DbInfo(SqliteEnergyStore store)
    {
        var stats = store.GetStatistics();
        Console.WriteLine($"Database:            {store.DatabasePath}");
        Console.WriteLine($"Schema version:      {stats.SchemaVersion}");
        Console.WriteLine($"Size:                {stats.DatabaseBytes / 1024.0:0.0} KiB (+ WAL {stats.WalBytes / 1024.0:0.0} KiB, free {stats.FreeBytes / 1024.0:0.0} KiB)");
        Console.WriteLine($"Sessions:            {stats.Sessions}");
        Console.WriteLine($"Raw samples:         {stats.PowerSamples}" + (stats.OldestSampleUtc is { } o ? $" ({EnergyConsoleReporter.Local(o)} -> {EnergyConsoleReporter.Local(stats.NewestSampleUtc!.Value)})" : ""));
        Console.WriteLine($"Energy buckets:      {stats.EnergyBuckets} (15 min each)");
        Console.WriteLine($"Price-split rows:    {stats.PriceSplitRows} (one per bucket per price actually applied; more than one only where a price changed mid-bucket)");
        Console.WriteLine($"Gaps:                {stats.Gaps}");
        Console.WriteLine($"Availability rows:   {stats.AvailabilityRows}");
        Console.WriteLine($"Prices:              {stats.Prices}");
        Console.WriteLine($"Raw retention:       {stats.RawRetentionDays} day(s)");
        if (stats.PowerSamples > 0)
            Console.WriteLine($"Bytes per sample:    {(stats.DatabaseBytes + stats.WalBytes) / (double)stats.PowerSamples:0.0} (whole file / raw samples, upper bound)");
        var integrity = store.CheckIntegrity();
        Console.WriteLine($"Integrity check:     {integrity}");
        return integrity == "ok" ? 0 : 1;
    }
}

using System.Globalization;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using WattScope.Storage;

namespace WattScope.Probe.Commands;

/// <summary>
/// Validates energy integration, pricing, persistence, recovery and summaries
/// end-to-end with synthetic data whose correct answers are known analytically.
/// Uses a temporary database; never touches hardware or the user's database.
/// </summary>
public static class SelfTestCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static int _passed, _failed;

    public static int Run()
    {
        _passed = _failed = 0;
        Console.WriteLine("WattScope self-test (synthetic data, temporary database)");

        Section("Energy integration");
        {
            var r = Integrate(Constant(0, 3600, 100, 200));
            CheckClose("Constant 100 W CPU for 1 h = 100 Wh", 100, EnergyUnits.ToWh(r.Totals.CpuPackageJoules));
            CheckClose("Constant 200 W GPU for 1 h = 200 Wh", 200, EnergyUnits.ToWh(r.Totals.GpuBoardJoules));
            CheckClose("Estimated wall (100+200+50)/0.90 W for 1 h = 388.889 Wh", 350 / 0.9, EnergyUnits.ToWh(r.Totals.EstimatedCompleteJoules));

            var ramp = new List<PowerSample>();
            for (double t = 0, step = 0.5; t < 600; t += step, step = step == 0.5 ? 1.5 : 0.5)
                ramp.Add(S(t, 50 + 0.1 * t, 10));
            ramp.Add(S(600, 110, 10));
            CheckClose("Linear ramp 50->110 W, irregular 0.5/1.5 s spacing = 48000 J (trapezoid exact)", 48000, Integrate(ramp).Totals.CpuPackageJoules);

            CheckClose("Actual elapsed time used: samples at 0,1,3 s at 100 W = 300 J", 300, Integrate([S(0, 100, 1), S(1, 100, 1), S(3, 100, 1)]).Totals.CpuPackageJoules);

            var missing = Integrate([S(0, 100, 1), S(1, 100, 1), S(2, null, 1), S(3, 100, 1), S(4, 100, 1)]);
            CheckClose("Missing CPU reading: neighbouring intervals get no CPU energy (200 J, not 400)", 200, missing.Totals.CpuPackageJoules);
            CheckClose("Missing CPU reading: CPU coverage 2 of 4 s", 2, missing.Totals.CpuPackageSeconds);

            var dup = Integrate([S(0, 100, 1), S(1, 100, 1), S(1, 5000, 1), S(2, 100, 1)]);
            Check("Duplicate timestamp discarded", dup.Duplicates == 1 && Math.Abs(dup.Totals.CpuPackageJoules - 200) < 1e-9);

            var gap = Integrate(Constant(0, 10, 100, 1).Concat(Constant(100, 110, 100, 1)));
            Check("90 s sampling gap: recorded as gap, no energy attributed", gap.Gaps.Count == 1 && gap.Gaps[0].Reason == GapReason.SamplingStall && Math.Abs(gap.Totals.CpuPackageJoules - 2000) < 1e-9);

            var sleep = Integrate([S(0, 100, 1, 0), S(1, 100, 1, 1), S(2, 100, 1, 3601), S(3, 100, 1, 3602)]);
            Check("Sleep (UTC +1 h, monotonic +1 s): clock-discontinuity gap, no energy", sleep.Gaps.Count == 1 && sleep.Gaps[0].Reason == GapReason.ClockDiscontinuity && Math.Abs(sleep.Totals.MonitoredSeconds - 2) < 1e-9);

            Check("0 W CPU package rejected as implausible (not counted as 0 W)", new PowerValidationRules().Validate(EnergyChannel.CpuPackage, 0).State == ReadingState.ImplausibleZero);
            Check("NaN power rejected", new PowerValidationRules().Validate(EnergyChannel.GpuBoard, double.NaN).State == ReadingState.NonFinite);
        }

        Section("Electricity pricing (decimal)");
        {
            Check("1 kWh x 0.30 EUR/kWh = 0.30 EUR exactly", CostCalculator.Cost(3_600_000, 0.30m) == 0.30m);
            var schedule = new ElectricityPriceSchedule(
            [
                new ElectricityPrice { EffectiveFromUtc = T0, PricePerKwh = 0.30m },
                new ElectricityPrice { EffectiveFromUtc = T0.AddHours(1), PricePerKwh = 0.40m }
            ]);
            var cost = CostCalculator.ForBuckets([Bucket(T0, 3_600_000), Bucket(T0.AddHours(1), 3_600_000)], schedule);
            Check("Price change 0.30 -> 0.40: 1 kWh before + 1 kWh after = 0.70 EUR", cost.EstimatedCost == 0.70m);
            Check("Rounding: 0.125 -> 0.13, 2.675 -> 2.68 (half away from zero)", MoneyRounding.Round(0.125m) == 0.13m && MoneyRounding.Round(2.675m) == 2.68m);
            var tiny = CostCalculator.ForBuckets(Enumerable.Range(0, 1000).Select(i => Bucket(T0.AddMinutes(15 * i), 36_000)).ToList(),
                new ElectricityPriceSchedule([new ElectricityPrice { EffectiveFromUtc = T0, PricePerKwh = 0.40m }]));
            Check("1000 x 0.004 EUR rounds once to 4.00 (not per bucket to 0.00)", tiny.EstimatedCostRounded == 4.00m);
            var unpriced = CostCalculator.ForBuckets([Bucket(T0, 3_600_000)], ElectricityPriceSchedule.Empty);
            Check("No price configured: cost unavailable, not zero", unpriced.EstimatedCost is null && unpriced.UnpricedJoules == 3_600_000);
            Check("'0,30' is rejected as a price (not read as 30)", !CliOptions.TryParseDecimal("0,30", out _) && CliOptions.TryParseDecimal("0.30", out var p) && p == 0.30m);
        }

        Section("Database persistence, recovery and summaries");
        var dir = Path.Combine(Path.GetTempPath(), "wattscope-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dbPath = Path.Combine(dir, "selftest.db");
            var settings = new EstimationSettings { OtherComponentsWatts = 0, PsuEfficiency = 1.0 };
            long id;
            using (var store = SqliteEnergyStore.Open(dbPath))
            {
                Check($"Fresh database migrated to schema v{store.SchemaVersion}", store.SchemaVersion >= 1);
                store.AddPrice(new ElectricityPrice { EffectiveFromUtc = T0.AddDays(-1), PricePerKwh = 0.30m }, T0);
                var monitor = new EnergyMonitor(store);
                id = monitor.Start([], settings).Id;
                foreach (var s in Constant(0, 3600, 500, 500))
                    monitor.OnSample(s);
                Check("Session stopped cleanly", monitor.Stop());
            }

            using (var store = SqliteEnergyStore.Open(dbPath))
            {
                var session = store.GetSession(id)!;
                var report = EnergyReportBuilder.Build("s", session.StartedUtc, session.EndedUtc!.Value,
                    store.GetSessionBuckets(id), store.GetSessionGaps(id), store.GetPriceSchedule(), [session.Estimation]);
                Check("Reopened DB: session status Stopped", session.Status == SessionStatus.Stopped);
                CheckClose("Reopened DB: session estimated energy = 1 kWh (500 W + 500 W, 1 h)", 1000, EnergyUnits.ToWh(report.Totals.EstimatedCompleteJoules));
                Check("Reopened DB: session cost = 0.30 EUR", report.Cost.EstimatedCostRounded == 0.30m);
                Check("Reopened DB: 3601 compact raw samples stored", store.GetSamples(id).Count == 3601);
                var (dayStart, dayEnd) = ReportPeriods.LocalDay(DateOnly.FromDateTime(T0.UtcDateTime), TimeZoneInfo.Utc);
                var daily = EnergyReportBuilder.DailyBreakdown(store.GetBuckets(dayStart, dayEnd), TimeZoneInfo.Utc, store.GetPriceSchedule());
                Check("Daily summary (UTC day) = 0.30 EUR", daily.Count == 1 && daily[0].Cost.EstimatedCostRounded == 0.30m);

                // Simulated crash: data flushed every 10 s, process "dies" at t=25 without stopping.
                var crashing = new EnergyMonitor(store, new EnergyMonitorOptions { FlushInterval = TimeSpan.FromSeconds(10) });
                var crashId = crashing.Start([], settings).Id;
                foreach (var s in Constant(0, 25, 100, 100, T0.AddDays(1)))
                    crashing.OnSample(s);

                // Next "process": the lock is free, so the Active session is orphaned.
                var recovered = store.RecoverInterruptedSessions();
                Check("Crash recovery: orphaned session marked Interrupted", recovered.Count == 1 && recovered[0].Id == crashId && recovered[0].Status == SessionStatus.Interrupted);
                var crashTotals = EnergyReportBuilder.Sum(store.GetSessionBuckets(crashId));
                Check("Crash recovery: ends at last flushed observation; unflushed 5 s not fabricated (20 s, 2000 J CPU)",
                    recovered[0].EndedUtc == T0.AddDays(1).AddSeconds(20) && Math.Abs(crashTotals.MonitoredSeconds - 20) < 1e-9 && Math.Abs(crashTotals.CpuPackageJoules - 2000) < 1e-9);

                var resumed = new EnergyMonitor(store);
                resumed.Resume(crashId, []);
                foreach (var s in Constant(0, 10, 100, 100, T0.AddDays(1).AddMinutes(5)))
                    resumed.OnSample(s);
                resumed.Stop();
                var gaps = store.GetSessionGaps(crashId);
                Check("Resume after crash: gap recorded with reason Interrupted, no energy for it",
                    gaps.Count == 1 && gaps[0].Reason == GapReason.Interrupted && Math.Abs(EnergyReportBuilder.Sum(store.GetSessionBuckets(crashId)).MonitoredSeconds - 30) < 1e-9);

                Check("SQLite integrity and foreign-key check", store.CheckIntegrity() == "ok");
            }
        }
        catch (Exception ex)
        {
            Check($"Database section threw {ex.GetType().Name}: {ex.Message}", false);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? $"ALL {_passed} CHECKS PASSED" : $"{_failed} CHECK(S) FAILED, {_passed} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
    }

    private static void Check(string description, bool ok)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {description}");
    }

    private static void CheckClose(string description, double expected, double actual) =>
        Check($"{description}  (got {actual.ToString("0.######", Inv)})", Math.Abs(expected - actual) <= Math.Abs(expected) * 1e-9 + 1e-9);

    private static PowerSample S(double seconds, double? cpu, double? gpu, double? utcSeconds = null, DateTimeOffset? baseUtc = null) => new()
    {
        UtcTime = (baseUtc ?? T0) + TimeSpan.FromSeconds(utcSeconds ?? seconds),
        MonotonicTime = TimeSpan.FromSeconds(seconds),
        CpuPackage = cpu is { } c ? ChannelReading.FromWatts(c) : ChannelReading.Rejected(ReadingState.Unavailable),
        GpuBoard = gpu is { } g ? ChannelReading.FromWatts(g) : ChannelReading.Rejected(ReadingState.Unavailable)
    };

    private static IEnumerable<PowerSample> Constant(int from, int to, double cpu, double gpu, DateTimeOffset? baseUtc = null)
    {
        for (var t = from; t <= to; t++)
            yield return S(t, cpu, gpu, baseUtc: baseUtc);
    }

    private static EnergyBucket Bucket(DateTimeOffset start, double completeJoules) => new()
    {
        SessionId = 1,
        BucketStartUtc = start,
        Totals = new EnergyTotals { MonitoredSeconds = 900, EstimatedCompleteJoules = completeJoules, EstimatedCompleteSeconds = 900 }
    };

    private sealed record IntegrationResult(EnergyTotals Totals, List<MonitoringGap> Gaps, int Duplicates);

    private static IntegrationResult Integrate(IEnumerable<PowerSample> samples)
    {
        var integrator = new EnergyIntegrator();
        var accumulator = new EnergyAccumulator();
        var settings = new EstimationSettings();
        var gaps = new List<MonitoringGap>();
        var duplicates = 0;
        foreach (var sample in samples)
        {
            var step = integrator.Accept(sample);
            if (step.Outcome == IntegrationOutcome.DuplicateDiscarded) duplicates++;
            if (step.Gap is { } g) gaps.Add(g);
            if (step.Interval is { } interval) accumulator.Add(interval, settings);
        }
        return new IntegrationResult(accumulator.RunTotals, gaps, duplicates);
    }
}

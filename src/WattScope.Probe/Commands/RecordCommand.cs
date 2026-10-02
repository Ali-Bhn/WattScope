using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WattScope.Core.Discovery;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;
using WattScope.Storage;

namespace WattScope.Probe.Commands;

/// <summary>
/// Persistent energy monitoring: selected power channels -> energy -> SQLite.
/// Unlike --monitor, never buffers or serializes the full sensor inventory.
/// </summary>
public static class RecordCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    public static async Task<int> RunAsync(
        DiscoverySnapshot snapshot,
        IReadOnlyList<ISensorProvider> providers,
        CliOptions options,
        ILoggerFactory loggerFactory,
        string appVersion,
        CancellationToken cancellationToken)
    {
        var dbPath = options.DatabasePath ?? SqliteEnergyStore.DefaultDatabasePath;
        using var monitorLock = MonitorLock.TryAcquire(dbPath);
        if (monitorLock is null)
        {
            Console.Error.WriteLine($"Another WattScope recording is already writing to {dbPath}. Stop it first.");
            return 3;
        }

        using var store = SqliteEnergyStore.Open(dbPath, loggerFactory.CreateLogger<SqliteEnergyStore>());
        Console.WriteLine($"Database: {store.DatabasePath} (schema v{store.SchemaVersion})");
        foreach (var recovered in store.RecoverInterruptedSessions())
            Console.WriteLine($"Recovered session #{recovered.Id}: previous recording ended unexpectedly; kept up to {EnergyConsoleReporter.Local(recovered.LastObservationUtc ?? recovered.StartedUtc)}.");
        var pruned = store.PruneRawSamples(DateTimeOffset.UtcNow);
        if (pruned > 0)
            Console.WriteLine($"Pruned {pruned} raw sample(s) older than {store.GetRawRetentionDays()} day(s).");

        var builder = new PowerSampleBuilder(snapshot.PowerClassification);
        PrintChannelSetup(builder, snapshot);
        // Discovery read the whole inventory; from here on only the bound sensors are refreshed each tick.
        builder.LimitSamplingToBindings(providers);

        var interval = TimeSpan.FromMilliseconds(options.IntervalMilliseconds);
        var monitor = new EnergyMonitor(store,
            new EnergyMonitorOptions { Integration = IntegrationOptions.ForSamplingInterval(interval) },
            logger: loggerFactory.CreateLogger<EnergyMonitor>(),
            priceScheduleProvider: store.GetPriceSchedule);

        MonitoringSession session;
        try
        {
            if (options.Resume)
            {
                var targetId = options.ResumeSessionId ?? store.GetLatestSession()?.Id
                    ?? throw new InvalidOperationException("There is no session to resume.");
                session = monitor.Resume(targetId, builder.Bindings);
                Console.WriteLine($"Resuming session #{session.Id}. The time since its last observation is recorded as a gap (no energy).");
            }
            else
            {
                session = monitor.Start(builder.Bindings, store.GetEstimationSettings(), snapshot.OperatingSystem.IsElevated, appVersion);
                Console.WriteLine($"Started session #{session.Id}.");
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var prices = store.GetPriceSchedule();
        var estimation = session.Estimation;
        Console.WriteLine($"Interval: {options.IntervalMilliseconds} ms." +
            (options.DurationSeconds is { } d ? $" Duration: {d} s." : " Press Ctrl+C to stop."));
        if (prices.Entries.Count == 0)
            Console.WriteLine("No electricity price configured: cost will be reported as unavailable. Set one with --set-price.");
        Console.WriteLine();

        store.Checkpoint();
        var dbBytesBefore = DatabaseBytes(dbPath);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var latency = new LatencyHistogram();
        var lastPrune = Stopwatch.StartNew();

        PowerModeChangedEventHandler powerHandler = (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend)
            {
                monitor.OnSuspend();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] System suspending: data flushed; no energy is recorded until resume.");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                monitor.OnResume();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] System resumed: the sleep period is recorded as a gap.");
            }
        };
        SystemEvents.PowerModeChanged += powerHandler;

        var engine = new SamplingEngine(providers, loggerFactory.CreateLogger<SamplingEngine>());
        var samplingOptions = new SamplingOptions
        {
            Interval = interval,
            Duration = options.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null
        };

        long ticks = 0;
        try
        {
            await foreach (var tick in engine.RunAsync(samplingOptions, cancellationToken))
            {
                ticks++;
                latency.Add(tick.ReadDuration);
                var sample = builder.Build(tick);
                monitor.OnSample(sample);

                if (!options.Quiet)
                    PrintTick(tick, sample, monitor.RunTotals, estimation, prices);

                if (lastPrune.Elapsed >= PruneInterval)
                {
                    PruneOnSeparateConnection(dbPath, loggerFactory);
                    lastPrune.Restart();
                }
            }
        }
        finally
        {
            SystemEvents.PowerModeChanged -= powerHandler;
            var stopped = monitor.Stop(cancellationToken.IsCancellationRequested ? "Stopped by user (Ctrl+C)" : "Duration elapsed");
            if (!stopped)
                Console.Error.WriteLine("Warning: the session could not be fully saved/closed; it will be recovered as interrupted on the next start.");
        }

        wall.Stop();
        process.Refresh();
        var cpuUsed = process.TotalProcessorTime - cpuBefore;

        Console.WriteLine();
        Console.WriteLine("Recording stopped. Summary read back from the database:");
        Console.WriteLine();
        DatabaseCommands.PrintSession(store, store.GetSession(session.Id)!, prices);

        var stats = monitor.Statistics;
        // Fold the WAL back into the main file so the growth figure reflects stored data, not WAL page preallocation.
        try { store.Checkpoint(); } catch (Exception ex) { loggerFactory.CreateLogger("WattScope.Probe.Record").LogWarning(ex, "WAL checkpoint failed"); }
        var dbBytesAfter = DatabaseBytes(dbPath);
        Console.WriteLine();
        Console.WriteLine("=== Performance (this run) ===");
        Console.WriteLine($"  Wall time:             {EnergyConsoleReporter.Duration(wall.Elapsed)}  ticks: {ticks}");
        Console.WriteLine($"  Process CPU time:      {cpuUsed.TotalSeconds:0.00} s = {cpuUsed / wall.Elapsed * 100:0.00} % of one core, {cpuUsed / wall.Elapsed * 100 / Environment.ProcessorCount:0.000} % of all {Environment.ProcessorCount} logical CPUs");
        Console.WriteLine($"  Memory:                working set {process.WorkingSet64 / 1048576.0:0.0} MiB, peak {process.PeakWorkingSet64 / 1048576.0:0.0} MiB, managed heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MiB");
        Console.WriteLine($"  Sampling latency:      avg {latency.AverageMs:0.00} ms, p95 {latency.PercentileMs(0.95):0.00} ms, max {latency.MaxMs:0.00} ms (time to read all providers per tick)");
        Console.WriteLine($"  Database flushes:      {stats.Flushes} ok, {stats.FlushFailures} failed, avg {(stats.Flushes > 0 ? stats.TotalFlushDuration.TotalMilliseconds / stats.Flushes : 0):0.00} ms, max {stats.MaxFlushDuration.TotalMilliseconds:0.00} ms");
        Console.WriteLine($"  Samples:               {stats.SamplesAccepted} accepted, {stats.DuplicatesDiscarded} duplicate(s), {stats.SamplesIgnoredWhileSuspended} ignored while suspended, {stats.GapsRecorded} gap(s), {stats.RawSamplesDropped} raw dropped");
        Console.WriteLine($"  Database growth:       {(dbBytesAfter - dbBytesBefore) / 1024.0:0.0} KiB ({(stats.SamplesAccepted > 0 ? (dbBytesAfter - dbBytesBefore) / (double)stats.SamplesAccepted : 0):0.0} bytes/sample incl. WAL and aggregates)");
        return 0;
    }

    private static void PrintChannelSetup(PowerSampleBuilder builder, DiscoverySnapshot snapshot)
    {
        Console.WriteLine("Energy channels (selected sensors only; overlapping sensors are never added):");
        foreach (var channel in Enum.GetValues<EnergyChannel>())
        {
            var bindings = builder.Bindings.Where(b => b.Channel == channel).ToList();
            if (bindings.Count > 0)
            {
                foreach (var b in bindings)
                    Console.WriteLine($"  {channel,-11} {b.HardwareName} / {b.SensorName} [{b.ProviderId}]");
            }
            else
            {
                Console.WriteLine($"  {channel,-11} unavailable ({builder.FixedState(channel)})");
            }
        }

        var cpuNow = builder.Build(snapshot.SensorInventory, DateTimeOffset.UtcNow, TimeSpan.Zero).CpuPackage;
        if (cpuNow.State == ReadingState.ImplausibleZero)
        {
            Console.WriteLine();
            Console.WriteLine("  Note: CPU package power currently reads 0 W, which is rejected as implausible." +
                (snapshot.OperatingSystem.IsElevated ? "" :
                 " This process is not elevated; on the development machine CPU power only read correctly when elevated."));
            Console.WriteLine("  The wall-power estimate will be partial (lower bound) and excluded from cost while this persists.");
        }
        Console.WriteLine();
    }

    private static void PrintTick(SamplingTick tick, PowerSample sample, EnergyTotals run, EstimationSettings estimation, ElectricityPriceSchedule prices)
    {
        var wallNow = WallPowerEstimator.EstimateWatts(sample, estimation);
        var wallText = wallNow.Value is { } w ? $"{w.ToString("0.0", Inv)} W ({wallNow.Completeness})" : "n/a";
        var price = prices.PriceAt(tick.Timestamp);
        var costText = price is not null
            ? $" ~{CostCalculator.Cost(run.EstimatedCompleteJoules, price.PricePerKwh).ToString("0.0000", Inv)} {price.Currency}"
            : "";
        var partialText = run.EstimatedPartialJoules > 0 ? $", partial >= {EnergyConsoleReporter.Wh(run.EstimatedPartialJoules)} (uncosted)" : "";
        var gap = tick.GapDetected ? "  [LATE TICK]" : "";
        var errors = tick.Errors.Count > 0 ? $"  [{tick.Errors.Count} provider error(s)]" : "";
        Console.WriteLine(
            $"[{tick.Sequence,5}] {tick.Timestamp.ToLocalTime():HH:mm:ss}  CPU {EnergyConsoleReporter.Watts(sample.CpuPackage),-9}  " +
            $"GPU {EnergyConsoleReporter.Watts(sample.GpuBoard),-9}  est. wall {wallText}  | run: est. {EnergyConsoleReporter.Wh(run.EstimatedCompleteJoules)}{costText}{partialText}{gap}{errors}");
    }

    /// <summary>Hourly retention pruning on its own short-lived connection, so the recording connection is never used from two threads.</summary>
    private static void PruneOnSeparateConnection(string dbPath, ILoggerFactory loggerFactory)
    {
        try
        {
            using var pruneStore = SqliteEnergyStore.Open(dbPath, loggerFactory.CreateLogger<SqliteEnergyStore>());
            pruneStore.PruneRawSamples(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("WattScope.Probe.Retention").LogWarning(ex, "Raw sample pruning failed; will retry in an hour");
        }
    }

    private static long DatabaseBytes(string dbPath)
    {
        var full = Path.GetFullPath(dbPath);
        return new[] { full, full + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
    }

    /// <summary>Fixed-size latency histogram (0.25 ms bins up to 2 s): bounded memory for arbitrarily long runs.</summary>
    private sealed class LatencyHistogram
    {
        private const double BinMs = 0.25;
        private readonly long[] _bins = new long[8000];
        private long _count;
        private double _sumMs;

        public double MaxMs { get; private set; }
        public double AverageMs => _count > 0 ? _sumMs / _count : 0;

        public void Add(TimeSpan duration)
        {
            var ms = duration.TotalMilliseconds;
            _count++;
            _sumMs += ms;
            MaxMs = Math.Max(MaxMs, ms);
            _bins[Math.Min(_bins.Length - 1, (int)(ms / BinMs))]++;
        }

        public double PercentileMs(double p)
        {
            if (_count == 0)
                return 0;
            var target = (long)Math.Ceiling(_count * p);
            long seen = 0;
            for (var i = 0; i < _bins.Length; i++)
            {
                seen += _bins[i];
                if (seen >= target)
                    return (i + 1) * BinMs;
            }
            return MaxMs;
        }
    }
}

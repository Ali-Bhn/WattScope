using WattScope.Core.Discovery;
using WattScope.Core.Export;
using WattScope.Core.Sampling;

namespace WattScope.Probe.Commands;

public static class MonitorCommand
{
    public static async Task<int> RunAsync(
        DiscoverySnapshot snapshot,
        SamplingEngine samplingEngine,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Monitoring started. Interval: {options.IntervalMilliseconds} ms." +
            (options.DurationSeconds is { } d ? $" Duration: {d} s." : " Press Ctrl+C to stop."));
        Console.WriteLine();

        var samplingOptions = new SamplingOptions
        {
            Interval = TimeSpan.FromMilliseconds(options.IntervalMilliseconds),
            Duration = options.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null
        };

        var buffer = options.ExportPath is not null ? new List<SamplingTick>() : null;

        await foreach (var tick in samplingEngine.RunAsync(samplingOptions, cancellationToken))
        {
            ConsoleReporter.PrintTick(tick, snapshot.PowerClassification);
            buffer?.Add(tick);
        }

        Console.WriteLine();
        Console.WriteLine("Monitoring stopped.");

        if (options.ExportPath is not null && buffer is not null)
        {
            var report = ExportReportBuilder.Build(
                snapshot,
                new SamplingConfigExport { IntervalMilliseconds = options.IntervalMilliseconds, DurationSeconds = options.DurationSeconds },
                buffer);

            var exportService = new JsonExportService();
            await exportService.WriteToFileAsync(report, options.ExportPath, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Report written to {Path.GetFullPath(options.ExportPath)}");
        }

        return 0;
    }
}

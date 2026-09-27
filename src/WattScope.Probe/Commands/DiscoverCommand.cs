using WattScope.Core.Discovery;
using WattScope.Core.Export;

namespace WattScope.Probe.Commands;

public static class DiscoverCommand
{
    public static async Task<int> RunAsync(DiscoverySnapshot snapshot, string? exportPath, CancellationToken cancellationToken)
    {
        ConsoleReporter.PrintDiscovery(snapshot);

        if (exportPath is not null)
        {
            var report = ExportReportBuilder.Build(snapshot, sampling: null, readings: []);
            var exportService = new JsonExportService();
            await exportService.WriteToFileAsync(report, exportPath, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Report written to {Path.GetFullPath(exportPath)}");
        }

        return 0;
    }
}

using System.Reflection;
using WattScope.Core.Discovery;
using WattScope.Core.Export;
using WattScope.Core.Sampling;

namespace WattScope.Probe;

public static class ExportReportBuilder
{
    public static ExportReport Build(DiscoverySnapshot snapshot, SamplingConfigExport? sampling, IReadOnlyList<SamplingTick> readings)
    {
        return new ExportReport
        {
            AppVersion = GetAppVersion(),
            GeneratedAt = DateTimeOffset.UtcNow,
            OperatingSystem = snapshot.OperatingSystem,
            Hardware = snapshot.Hardware,
            Providers = snapshot.Providers,
            SensorInventory = snapshot.SensorInventory,
            SelectedPowerSensors = snapshot.PowerClassification.Selections,
            Sampling = sampling,
            Readings = readings,
            Diagnostics = snapshot.Diagnostics,
            Permissions = snapshot.Permissions
        };
    }

    private static string GetAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}

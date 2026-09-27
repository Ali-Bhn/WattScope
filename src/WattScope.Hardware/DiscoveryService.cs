using Microsoft.Extensions.Logging;
using WattScope.Core.Classification;
using WattScope.Core.Diagnostics;
using WattScope.Core.Discovery;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;
using WattScope.Hardware.SystemInfo;

namespace WattScope.Hardware;

/// <summary>
/// Orchestrates one discovery pass: WMI hardware inventory (sensor-independent)
/// plus every configured <see cref="ISensorProvider"/>, fed into the
/// classifier. Provider initialization failures never abort discovery -- they
/// become a <see cref="ProviderAvailability"/> entry and a diagnostic.
/// </summary>
public sealed class DiscoveryService
{
    private readonly IReadOnlyList<ISensorProvider> _sensorProviders;
    private readonly WmiHardwareInventoryProvider _wmiProvider;
    private readonly PowerSensorClassifier _classifier;
    private readonly ILogger<DiscoveryService>? _logger;

    public DiscoveryService(
        IReadOnlyList<ISensorProvider> sensorProviders,
        WmiHardwareInventoryProvider? wmiProvider = null,
        PowerSensorClassifier? classifier = null,
        ILogger<DiscoveryService>? logger = null)
    {
        _sensorProviders = sensorProviders;
        _wmiProvider = wmiProvider ?? new WmiHardwareInventoryProvider();
        _classifier = classifier ?? new PowerSensorClassifier();
        _logger = logger;
    }

    public DiscoverySnapshot Discover()
    {
        var osInfo = OsInfoCollector.Collect();
        var inventory = _wmiProvider.Discover(osInfo);

        var providerAvailability = new List<ProviderAvailability>();
        var allDevices = new List<HardwareDevice>(inventory.Devices);
        var allReadings = new List<SensorReading>();
        var diagnostics = new List<DiagnosticMessage>();

        foreach (var provider in _sensorProviders)
        {
            var availability = provider.Initialize();
            providerAvailability.Add(availability);

            if (!availability.IsAvailable)
            {
                _logger?.LogInformation("Provider {ProviderId} unavailable: {Reason}", provider.ProviderId, availability.UnavailableReason);
                diagnostics.Add(new DiagnosticMessage
                {
                    Severity = DiagnosticSeverity.Warning,
                    Source = provider.ProviderId,
                    Message = $"Provider unavailable: {availability.UnavailableReason}",
                    Timestamp = DateTimeOffset.UtcNow
                });
                continue;
            }

            allDevices.AddRange(provider.GetHardware());
            allReadings.AddRange(provider.GetSensorReadings());
        }

        var classification = _classifier.Classify(allDevices, allReadings);

        var permissions = new Core.Diagnostics.PermissionInfo
        {
            IsElevated = osInfo.IsElevated,
            KnownLimitations = BuildKnownLimitations(osInfo.IsElevated, providerAvailability)
        };

        return new DiscoverySnapshot
        {
            OperatingSystem = osInfo,
            Hardware = allDevices,
            Providers = providerAvailability,
            SensorInventory = allReadings,
            PowerClassification = classification,
            Diagnostics = diagnostics,
            Permissions = permissions
        };
    }

    private static IReadOnlyList<string> BuildKnownLimitations(bool isElevated, IReadOnlyList<ProviderAvailability> providers)
    {
        var limitations = new List<string>();

        if (!isElevated)
        {
            limitations.Add(
                "Running without administrator privileges: LibreHardwareMonitor's CPU package/core power " +
                "sensors depend on kernel-level MSR access (via its WinRing0 driver) and are commonly " +
                "unavailable or absent without elevation. GPU power sensors (NVML, and most vendor-API-based " +
                "LibreHardwareMonitor GPU sensors) are typically unaffected by this.");
        }

        foreach (var provider in providers.Where(p => p.RequiresElevation))
        {
            limitations.Add($"Provider {provider.ProviderId} reported it requires elevation: {provider.UnavailableReason}");
        }

        return limitations;
    }
}

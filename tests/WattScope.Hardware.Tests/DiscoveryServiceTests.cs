using WattScope.Core.Diagnostics;
using WattScope.Core.Sensors;
using WattScope.Hardware.Tests.TestSupport;

namespace WattScope.Hardware.Tests;

public class DiscoveryServiceTests
{
    [Fact]
    public void UnavailableProvider_ProducesADiagnosticWarning_AndDiscoveryStillCompletes()
    {
        var unavailable = new FakeSensorProvider("Broken", isAvailable: false, unavailableReason: "simulated missing driver");
        var service = new DiscoveryService([unavailable]);

        var snapshot = service.Discover();

        var providerEntry = Assert.Single(snapshot.Providers);
        Assert.False(providerEntry.IsAvailable);
        Assert.Contains(snapshot.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("simulated missing driver"));

        // Real, sensor-independent WMI hardware inventory is still present.
        Assert.NotEmpty(snapshot.Hardware);
    }

    [Fact]
    public void AvailableProvider_ContributesHardwareAndReadings()
    {
        var reading = new SensorReading
        {
            ProviderId = "Fake",
            HardwareId = "hw0",
            HardwareName = "Fake Device",
            SensorId = "s0",
            SensorName = "Fake Power",
            SensorType = "Power",
            Unit = "W",
            Value = 42.0,
            Availability = SensorAvailability.Available,
            Timestamp = DateTimeOffset.UtcNow
        };
        var provider = new FakeSensorProvider("Fake", readingsFactory: () => [reading]);
        var service = new DiscoveryService([provider]);

        var snapshot = service.Discover();

        Assert.Contains(snapshot.SensorInventory, r => r.SensorId == "s0" && r.Value == 42.0);
    }
}

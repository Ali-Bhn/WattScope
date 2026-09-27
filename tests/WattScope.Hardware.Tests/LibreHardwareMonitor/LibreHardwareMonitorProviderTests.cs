using WattScope.Hardware.LibreHardwareMonitor;

namespace WattScope.Hardware.Tests.LibreHardwareMonitor;

/// <summary>Integration tests against the real LibreHardwareMonitorLib backend on this Windows machine.</summary>
public class LibreHardwareMonitorProviderTests
{
    [Fact]
    public void Initialize_Succeeds_AndGetSensorReadingsDoesNotThrow()
    {
        using var provider = new LibreHardwareMonitorProvider();
        var availability = provider.Initialize();

        Assert.True(availability.IsAvailable, availability.UnavailableReason);

        var readings = provider.GetSensorReadings();
        Assert.NotEmpty(readings);
        Assert.All(readings, r => Assert.Equal(WattScope.Hardware.LibreHardwareMonitor.LibreHardwareMonitorProvider.Id, r.ProviderId));
    }

    [Fact]
    public void ReInitialize_DoesNotDuplicateHardwareDevices()
    {
        using var provider = new LibreHardwareMonitorProvider();
        provider.Initialize();
        var firstCount = provider.GetHardware().Count;

        provider.Initialize();
        var secondCount = provider.GetHardware().Count;

        Assert.Equal(firstCount, secondCount);
    }

    [Fact]
    public void GetSensorReadings_NeverReturnsNaNOrInfiniteAsAnAvailableValue()
    {
        using var provider = new LibreHardwareMonitorProvider();
        provider.Initialize();
        var readings = provider.GetSensorReadings();

        foreach (var reading in readings.Where(r => r.IsAvailable))
        {
            Assert.NotNull(reading.Value);
            Assert.False(double.IsNaN(reading.Value!.Value));
            Assert.False(double.IsInfinity(reading.Value!.Value));
        }
    }
}

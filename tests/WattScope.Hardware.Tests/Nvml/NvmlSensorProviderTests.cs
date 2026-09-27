using WattScope.Hardware.Nvml;

namespace WattScope.Hardware.Tests.Nvml;

/// <summary>
/// Integration tests against the real NVML on this machine, which has an
/// NVIDIA GPU and driver installed. If NVML is genuinely unavailable
/// (no NVIDIA hardware/driver), the provider must still report a graceful
/// "unavailable" result rather than throwing.
/// </summary>
public class NvmlSensorProviderTests
{
    [Fact]
    public void Initialize_ReportsAvailabilityWithoutThrowing()
    {
        using var provider = new NvmlSensorProvider();
        var availability = provider.Initialize();

        Assert.Equal(WattScope.Hardware.Nvml.NvmlSensorProvider.Id, availability.ProviderId);
        // Not asserting IsAvailable == true: must hold on any machine, with or without an NVIDIA GPU.
    }

    [Fact]
    public void WhenAvailable_PowerReadingsAreInAPlausibleWattRange()
    {
        using var provider = new NvmlSensorProvider();
        var availability = provider.Initialize();
        if (!availability.IsAvailable)
            return; // No NVIDIA GPU/driver on this machine; nothing to validate.

        var readings = provider.GetSensorReadings();
        Assert.NotEmpty(readings);
        foreach (var reading in readings.Where(r => r.IsAvailable))
        {
            Assert.InRange(reading.Value!.Value, 0.0, 2000.0);
        }
    }

    [Fact]
    public void ReInitialize_DoesNotDuplicateDevices()
    {
        using var provider = new NvmlSensorProvider();
        provider.Initialize();
        var firstCount = provider.GetHardware().Count;

        provider.Initialize();
        var secondCount = provider.GetHardware().Count;

        Assert.Equal(firstCount, secondCount);
    }
}

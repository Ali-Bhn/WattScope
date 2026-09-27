using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Hardware.Tests.TestSupport;

public sealed class FakeSensorProvider(
    string providerId,
    bool isAvailable = true,
    string? unavailableReason = null,
    IReadOnlyList<HardwareDevice>? hardware = null,
    Func<IReadOnlyList<SensorReading>>? readingsFactory = null) : ISensorProvider
{
    public string ProviderId => providerId;

    public ProviderAvailability Initialize() => new()
    {
        ProviderId = providerId,
        IsAvailable = isAvailable,
        UnavailableReason = unavailableReason
    };

    public IReadOnlyList<HardwareDevice> GetHardware() => hardware ?? [];

    public IReadOnlyList<SensorReading> GetSensorReadings() => readingsFactory?.Invoke() ?? [];

    public void Dispose() { }
}

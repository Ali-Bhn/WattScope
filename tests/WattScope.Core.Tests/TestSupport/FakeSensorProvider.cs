using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Core.Tests.TestSupport;

/// <summary>Configurable test double so classifier/sampling-engine tests never depend on real hardware.</summary>
public sealed class FakeSensorProvider : ISensorProvider
{
    private readonly Func<IReadOnlyList<SensorReading>>? _readingsFactory;
    private readonly IReadOnlyList<HardwareDevice> _hardware;
    private int _callCount;

    public FakeSensorProvider(
        string providerId,
        IReadOnlyList<HardwareDevice>? hardware = null,
        Func<IReadOnlyList<SensorReading>>? readingsFactory = null,
        bool isAvailable = true,
        string? unavailableReason = null)
    {
        ProviderId = providerId;
        _hardware = hardware ?? [];
        _readingsFactory = readingsFactory;
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
    }

    public string ProviderId { get; }
    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }
    public int CallCount => _callCount;
    public bool Disposed { get; private set; }

    public ProviderAvailability Initialize() => new()
    {
        ProviderId = ProviderId,
        IsAvailable = IsAvailable,
        UnavailableReason = UnavailableReason
    };

    public IReadOnlyList<HardwareDevice> GetHardware() => _hardware;

    public IReadOnlyList<SensorReading> GetSensorReadings()
    {
        _callCount++;
        return _readingsFactory?.Invoke() ?? [];
    }

    public void Dispose() => Disposed = true;
}

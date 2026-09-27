using WattScope.Core.Hardware;

namespace WattScope.Core.Sensors;

/// <summary>
/// A source of hardware and sensor data. Implementations wrap a specific
/// monitoring backend (LibreHardwareMonitor, NVML, ...). The sampling engine
/// and classifier depend only on this abstraction, never on a concrete
/// backend, so both can be exercised in tests with a fake implementation.
/// </summary>
public interface ISensorProvider : IDisposable
{
    /// <summary>Stable identifier for this provider, used as <see cref="SensorReading.ProviderId"/>.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Attempts to initialize the provider (open handles, load native
    /// libraries, enumerate the backend). Must not throw; failures are
    /// reported through the returned <see cref="ProviderAvailability"/> so a
    /// missing provider never crashes the application.
    /// </summary>
    ProviderAvailability Initialize();

    /// <summary>Hardware devices this provider can see. Empty if not initialized or unavailable.</summary>
    IReadOnlyList<HardwareDevice> GetHardware();

    /// <summary>
    /// Refreshes and returns the current sensor readings. Reuses the
    /// provider's already-initialized objects rather than re-enumerating
    /// hardware; safe to call once per sampling tick.
    /// </summary>
    IReadOnlyList<SensorReading> GetSensorReadings();
}

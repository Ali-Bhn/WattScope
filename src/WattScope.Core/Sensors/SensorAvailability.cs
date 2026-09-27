namespace WattScope.Core.Sensors;

/// <summary>
/// Why a sensor reading does or does not carry a usable value. Distinguishing
/// these is required so a missing/failed reading is never silently treated as
/// zero.
/// </summary>
public enum SensorAvailability
{
    Available,
    NotSupported,
    NullValue,
    NotANumber,
    Infinite,
    UpdateFailed,
    DeviceDisconnected
}

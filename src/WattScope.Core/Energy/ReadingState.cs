namespace WattScope.Core.Energy;

/// <summary>
/// Whether a channel's value in a <see cref="PowerSample"/> is usable for
/// energy integration, and if not, why. Persisted as a small integer, so
/// values must never be renumbered.
/// </summary>
public enum ReadingState : byte
{
    Valid = 0,

    /// <summary>The selected sensor was absent from this tick (e.g. the provider's refresh threw).</summary>
    Missing = 1,

    /// <summary>The provider reported the sensor without a value (<see cref="Sensors.SensorAvailability"/> other than Available).</summary>
    Unavailable = 2,

    /// <summary>NaN or infinite.</summary>
    NonFinite = 3,

    Negative = 4,

    /// <summary>Above <see cref="PowerValidationRules.MaxPlausibleWatts"/>.</summary>
    AboveMaximum = 5,

    /// <summary>
    /// Exactly 0 W on a channel where that is physically implausible while
    /// the machine is running (CPU package). LibreHardwareMonitor reports
    /// this as a normal value when it lacks driver access -- see
    /// docs/SENSOR_CONTRACT.md section 4.
    /// </summary>
    ImplausibleZero = 6,

    /// <summary>The classifier could not pick a sensor for the role (e.g. multiple GPUs across providers).</summary>
    Ambiguous = 7,

    /// <summary>No sensor for this role was discovered at all.</summary>
    NotPresent = 8
}

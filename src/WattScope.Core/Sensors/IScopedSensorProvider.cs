namespace WattScope.Core.Sensors;

/// <summary>
/// Optional capability of a provider whose backend exposes far more sensors
/// than monitoring uses. Once the energy channels are bound (after discovery),
/// continuous sampling tells the provider which of its sensors it needs, so a
/// tick refreshes and returns only those instead of the whole inventory.
/// Discovery and <c>--monitor</c> never call this and keep reading everything.
/// </summary>
public interface IScopedSensorProvider : ISensorProvider
{
    /// <summary>
    /// Limits <see cref="ISensorProvider.GetSensorReadings"/> to these sensor
    /// IDs (this provider's own <see cref="SensorReading.SensorId"/> values)
    /// until the next <see cref="ISensorProvider.Initialize"/>. A required
    /// sensor that cannot be read is simply absent from the result -- never a
    /// substitute value. An empty set means nothing of this provider is used.
    /// </summary>
    void LimitSamplingTo(IReadOnlyCollection<string> sensorIds);
}

using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Hardware.LibreHardwareMonitor;

/// <summary>
/// Wraps LibreHardwareMonitorLib's <see cref="Computer"/> as an
/// <see cref="ISensorProvider"/>. The <see cref="Computer"/> instance and its
/// hardware tree are created once in <see cref="Initialize"/> and reused for
/// every subsequent call to <see cref="GetSensorReadings"/>.
/// </summary>
public sealed class LibreHardwareMonitorProvider : ISensorProvider
{
    public const string Id = "LibreHardwareMonitor";

    private readonly ILogger<LibreHardwareMonitorProvider>? _logger;
    private Computer? _computer;
    private readonly HashSet<string> _failedHardwareIds = new();

    public LibreHardwareMonitorProvider(ILogger<LibreHardwareMonitorProvider>? logger = null)
    {
        _logger = logger;
    }

    public string ProviderId => Id;

    public ProviderAvailability Initialize()
    {
        try
        {
            _computer?.Close();
            _failedHardwareIds.Clear();
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false
            };
            _computer.Open();

            return new ProviderAvailability
            {
                ProviderId = Id,
                IsAvailable = true
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LibreHardwareMonitor failed to initialize");
            return new ProviderAvailability
            {
                ProviderId = Id,
                IsAvailable = false,
                UnavailableReason = ex.Message
            };
        }
    }

    public IReadOnlyList<HardwareDevice> GetHardware()
    {
        if (_computer is null)
            return [];

        var devices = new List<HardwareDevice>();
        foreach (var hardware in _computer.Hardware)
            CollectHardware(hardware, null, devices);
        return devices;
    }

    private static void CollectHardware(IHardware hardware, string? parentId, List<HardwareDevice> devices)
    {
        var id = hardware.Identifier.ToString();
        devices.Add(new HardwareDevice
        {
            Id = id,
            Category = LibreHardwareMonitorTypeMapper.ToCategory(hardware.HardwareType),
            Name = hardware.Name,
            ParentId = parentId,
            Source = Id
        });

        foreach (var sub in hardware.SubHardware)
            CollectHardware(sub, id, devices);
    }

    public IReadOnlyList<SensorReading> GetSensorReadings()
    {
        if (_computer is null)
            return [];

        var timestamp = DateTimeOffset.UtcNow;
        var readings = new List<SensorReading>();
        foreach (var hardware in _computer.Hardware)
            UpdateAndCollect(hardware, timestamp, readings);
        return readings;
    }

    private void UpdateAndCollect(IHardware hardware, DateTimeOffset timestamp, List<SensorReading> readings)
    {
        var hardwareId = hardware.Identifier.ToString();
        var updateFailed = false;
        try
        {
            hardware.Update();
            _failedHardwareIds.Remove(hardwareId);
        }
        catch (Exception ex)
        {
            updateFailed = true;
            if (_failedHardwareIds.Add(hardwareId))
                _logger?.LogWarning(ex, "Failed to update LibreHardwareMonitor hardware {Name} ({Id})", hardware.Name, hardwareId);
        }

        foreach (var sensor in hardware.Sensors)
            readings.Add(BuildReading(hardware, sensor, hardwareId, updateFailed, timestamp));

        foreach (var sub in hardware.SubHardware)
            UpdateAndCollect(sub, timestamp, readings);
    }

    private static SensorReading BuildReading(IHardware hardware, ISensor sensor, string hardwareId, bool updateFailed, DateTimeOffset timestamp)
    {
        var availability = ClassifyAvailability(sensor.Value, updateFailed);
        double? value = availability == SensorAvailability.Available ? sensor.Value!.Value : null;

        return new SensorReading
        {
            ProviderId = Id,
            HardwareId = hardwareId,
            HardwareName = hardware.Name,
            SensorId = sensor.Identifier.ToString(),
            SensorName = sensor.Name,
            SensorType = sensor.SensorType.ToString(),
            Unit = SensorUnitMapper.ToUnit(sensor.SensorType),
            Value = value,
            Availability = availability,
            Timestamp = timestamp
        };
    }

    private static SensorAvailability ClassifyAvailability(float? value, bool updateFailed)
    {
        if (updateFailed)
            return SensorAvailability.UpdateFailed;
        if (value is null)
            return SensorAvailability.NullValue;
        if (float.IsNaN(value.Value))
            return SensorAvailability.NotANumber;
        if (float.IsInfinity(value.Value))
            return SensorAvailability.Infinite;
        return SensorAvailability.Available;
    }

    public void Dispose()
    {
        try
        {
            _computer?.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error closing LibreHardwareMonitor computer handle");
        }
    }
}

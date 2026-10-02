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
///
/// By default every hardware node is refreshed and every sensor returned
/// (discovery, <c>--monitor</c>). After <see cref="LimitSamplingTo"/> a tick
/// refreshes only the nodes that own a required sensor (and their parents) and
/// returns only those sensors: on a machine whose GPU power comes from NVML the
/// LibreHardwareMonitor GPU node is then not queried every second for readings
/// nobody uses, while a GPU that <em>is</em> bound to LibreHardwareMonitor
/// keeps being read exactly as before.
/// </summary>
public sealed class LibreHardwareMonitorProvider : IScopedSensorProvider
{
    public const string Id = "LibreHardwareMonitor";

    /// <summary>A hardware node refreshed during scoped sampling, with the required sensors it owns (none for a mere parent).</summary>
    private sealed record ScopedHardware(IHardware Hardware, string HardwareId, IReadOnlyList<(ISensor Sensor, string SensorId)> Sensors);

    private readonly ILogger<LibreHardwareMonitorProvider>? _logger;
    private readonly IReadOnlyList<IHardware>? _fixedHardware;
    private Computer? _computer;
    private List<ScopedHardware>? _scope;
    private readonly HashSet<string> _failedHardwareIds = new();

    public LibreHardwareMonitorProvider(ILogger<LibreHardwareMonitorProvider>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Test seam: reads this hardware tree instead of opening a <see cref="Computer"/>.</summary>
    internal LibreHardwareMonitorProvider(IReadOnlyList<IHardware> hardware)
    {
        _fixedHardware = hardware;
    }

    public string ProviderId => Id;

    private IEnumerable<IHardware>? Roots => _fixedHardware ?? (IEnumerable<IHardware>?)_computer?.Hardware;

    public ProviderAvailability Initialize()
    {
        try
        {
            _computer?.Close();
            _failedHardwareIds.Clear();
            _scope = null;
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
        if (Roots is not { } roots)
            return [];

        var devices = new List<HardwareDevice>();
        foreach (var hardware in roots)
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

    public void LimitSamplingTo(IReadOnlyCollection<string> sensorIds)
    {
        var scope = new List<ScopedHardware>();
        foreach (var hardware in Roots ?? [])
            AddToScope(hardware, sensorIds, scope);
        _scope = scope;
    }

    /// <summary>
    /// Pre-order, like the unscoped walk. A node is kept when it owns a required
    /// sensor or leads to one, so the parents of a required node are still
    /// refreshed; everything else is left out. Returns whether the node was kept.
    /// </summary>
    private static bool AddToScope(IHardware hardware, IReadOnlyCollection<string> sensorIds, List<ScopedHardware> scope)
    {
        var sensors = hardware.Sensors
            .Select(s => (Sensor: s, SensorId: s.Identifier.ToString()))
            .Where(s => sensorIds.Contains(s.SensorId))
            .ToList();
        var index = scope.Count;
        scope.Add(new ScopedHardware(hardware, hardware.Identifier.ToString(), sensors));

        var needed = sensors.Count > 0;
        foreach (var sub in hardware.SubHardware)
            needed |= AddToScope(sub, sensorIds, scope);
        if (!needed)
            scope.RemoveAt(index); // nothing below it was kept either, so it is still the last entry
        return needed;
    }

    public IReadOnlyList<SensorReading> GetSensorReadings()
    {
        if (Roots is not { } roots)
            return [];

        var timestamp = DateTimeOffset.UtcNow;
        var readings = new List<SensorReading>();
        if (_scope is { } scope)
        {
            CollectScope(scope, timestamp, readings);
            return readings;
        }

        foreach (var hardware in roots)
            UpdateAndCollect(hardware, timestamp, readings);
        return readings;
    }

    private void CollectScope(List<ScopedHardware> scope, DateTimeOffset timestamp, List<SensorReading> readings)
    {
        foreach (var node in scope)
        {
            var updateFailed = !TryUpdate(node.Hardware, node.HardwareId);
            if (node.Sensors.Count == 0)
                continue;

            // Same rule as the unscoped walk: only a sensor the hardware lists right now is reported. One that
            // has gone is absent from the tick (the channel then reads Missing), never a stale or made-up value.
            var active = node.Hardware.Sensors;
            foreach (var (sensor, sensorId) in node.Sensors)
            {
                var current = Array.IndexOf(active, sensor) >= 0 ? sensor : Array.Find(active, s => s.Identifier.ToString() == sensorId);
                if (current is not null)
                    readings.Add(BuildReading(node.Hardware, current, node.HardwareId, sensorId, updateFailed, timestamp));
            }
        }
    }

    private void UpdateAndCollect(IHardware hardware, DateTimeOffset timestamp, List<SensorReading> readings)
    {
        var hardwareId = hardware.Identifier.ToString();
        var updateFailed = !TryUpdate(hardware, hardwareId);

        foreach (var sensor in hardware.Sensors)
            readings.Add(BuildReading(hardware, sensor, hardwareId, sensor.Identifier.ToString(), updateFailed, timestamp));

        foreach (var sub in hardware.SubHardware)
            UpdateAndCollect(sub, timestamp, readings);
    }

    private bool TryUpdate(IHardware hardware, string hardwareId)
    {
        try
        {
            hardware.Update();
            _failedHardwareIds.Remove(hardwareId);
            return true;
        }
        catch (Exception ex)
        {
            if (_failedHardwareIds.Add(hardwareId))
                _logger?.LogWarning(ex, "Failed to update LibreHardwareMonitor hardware {Name} ({Id})", hardware.Name, hardwareId);
            return false;
        }
    }

    private static SensorReading BuildReading(IHardware hardware, ISensor sensor, string hardwareId, string sensorId, bool updateFailed, DateTimeOffset timestamp)
    {
        var availability = ClassifyAvailability(sensor.Value, updateFailed);
        double? value = availability == SensorAvailability.Available ? sensor.Value!.Value : null;

        return new SensorReading
        {
            ProviderId = Id,
            HardwareId = hardwareId,
            HardwareName = hardware.Name,
            SensorId = sensorId,
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

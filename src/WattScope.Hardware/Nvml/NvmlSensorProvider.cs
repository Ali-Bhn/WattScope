using Microsoft.Extensions.Logging;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Hardware.Nvml;

/// <summary>
/// Optional NVIDIA power provider using the driver's own NVML library.
/// Reports only GPU board power -- NVML on consumer GPUs does not expose a
/// separate "chip-only" power figure the way some LibreHardwareMonitor
/// builds attempt to derive. If nvml.dll is missing or initialization fails
/// for any reason, this provider reports itself unavailable and the
/// application continues without it.
/// </summary>
public sealed class NvmlSensorProvider : ISensorProvider
{
    public const string Id = "Nvml";

    private readonly ILogger<NvmlSensorProvider>? _logger;
    private readonly List<(IntPtr Handle, HardwareDevice Device)> _devices = new();
    private bool _initialized;

    public NvmlSensorProvider(ILogger<NvmlSensorProvider>? logger = null)
    {
        _logger = logger;
    }

    public string ProviderId => Id;

    public ProviderAvailability Initialize()
    {
        _devices.Clear();

        try
        {
            var initResult = NvmlNativeMethods.nvmlInit_v2();
            if (initResult != NvmlReturn.Success)
                throw new NvmlException(initResult, nameof(NvmlNativeMethods.nvmlInit_v2));
            _initialized = true;

            var count = NvmlNativeMethods.DeviceGetCount();
            for (uint i = 0; i < count; i++)
            {
                var handle = NvmlNativeMethods.DeviceGetHandleByIndex(i);
                var name = NvmlNativeMethods.DeviceGetName(handle);
                var uuid = NvmlNativeMethods.DeviceGetUuid(handle);
                var device = new HardwareDevice
                {
                    Id = $"nvml:{uuid}",
                    Category = HardwareCategory.GpuNvidia,
                    Name = name,
                    Source = Id
                };
                _devices.Add((handle, device));
            }

            return new ProviderAvailability
            {
                ProviderId = Id,
                IsAvailable = true
            };
        }
        catch (DllNotFoundException ex)
        {
            _logger?.LogInformation("NVML unavailable: nvml.dll not found ({Message})", ex.Message);
            return new ProviderAvailability
            {
                ProviderId = Id,
                IsAvailable = false,
                UnavailableReason = "nvml.dll not found (no NVIDIA driver installed, or GPU not present)"
            };
        }
        catch (NvmlException ex)
        {
            _logger?.LogWarning(ex, "NVML initialization failed");
            return new ProviderAvailability
            {
                ProviderId = Id,
                IsAvailable = false,
                UnavailableReason = ex.Message,
                RequiresElevation = ex.ReturnCode == NvmlReturn.NoPermission
            };
        }
    }

    public IReadOnlyList<HardwareDevice> GetHardware() => _devices.Select(d => d.Device).ToList();

    public IReadOnlyList<SensorReading> GetSensorReadings()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var readings = new List<SensorReading>(_devices.Count);

        foreach (var (handle, device) in _devices)
        {
            SensorAvailability availability;
            double? value = null;
            try
            {
                var watts = NvmlNativeMethods.DeviceGetPowerUsageWatts(handle);
                if (watts is null)
                {
                    availability = SensorAvailability.NotSupported;
                }
                else
                {
                    value = watts.Value;
                    availability = SensorAvailability.Available;
                }
            }
            catch (NvmlException ex) when (ex.ReturnCode == NvmlReturn.GpuIsLost)
            {
                availability = SensorAvailability.DeviceDisconnected;
            }
            catch (NvmlException ex)
            {
                _logger?.LogWarning(ex, "NVML power query failed for {Device}", device.Name);
                availability = SensorAvailability.UpdateFailed;
            }

            readings.Add(new SensorReading
            {
                ProviderId = Id,
                HardwareId = device.Id,
                HardwareName = device.Name,
                SensorId = $"{device.Id}:power",
                SensorName = "GPU Board Power",
                SensorType = "Power",
                Unit = "W",
                Value = value,
                Availability = availability,
                Timestamp = timestamp
            });
        }

        return readings;
    }

    public void Dispose()
    {
        if (!_initialized)
            return;

        try
        {
            NvmlNativeMethods.nvmlShutdown();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error shutting down NVML");
        }
    }
}

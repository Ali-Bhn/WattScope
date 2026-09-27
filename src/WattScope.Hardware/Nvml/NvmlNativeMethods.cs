using System.Runtime.InteropServices;
using System.Text;

namespace WattScope.Hardware.Nvml;

/// <summary>
/// Direct P/Invoke bindings against the NVIDIA driver's own nvml.dll (found
/// under C:\Windows\System32 when a driver is installed). Never bundled by
/// this application -- if the DLL is missing, every call below throws
/// <see cref="DllNotFoundException"/>, which callers must handle.
/// </summary>
internal static class NvmlNativeMethods
{
    private const string DllName = "nvml.dll";
    private const int DeviceNameBufferSize = 96;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlInit_v2();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NvmlReturn nvmlShutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern NvmlReturn nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern NvmlReturn nvmlDeviceGetUUID(IntPtr device, StringBuilder uuid, uint length);

    internal static uint DeviceGetCount()
    {
        var result = nvmlDeviceGetCount_v2(out var count);
        ThrowIfError(result, nameof(nvmlDeviceGetCount_v2));
        return count;
    }

    internal static IntPtr DeviceGetHandleByIndex(uint index)
    {
        var result = nvmlDeviceGetHandleByIndex_v2(index, out var handle);
        ThrowIfError(result, nameof(nvmlDeviceGetHandleByIndex_v2));
        return handle;
    }

    internal static string DeviceGetName(IntPtr device)
    {
        var sb = new StringBuilder(DeviceNameBufferSize);
        var result = nvmlDeviceGetName(device, sb, (uint)sb.Capacity);
        ThrowIfError(result, nameof(nvmlDeviceGetName));
        return sb.ToString();
    }

    internal static string DeviceGetUuid(IntPtr device)
    {
        var sb = new StringBuilder(DeviceNameBufferSize);
        var result = nvmlDeviceGetUUID(device, sb, (uint)sb.Capacity);
        ThrowIfError(result, nameof(nvmlDeviceGetUUID));
        return sb.ToString();
    }

    /// <summary>Returns board power in watts, or null when the query is not supported on this device/driver.</summary>
    internal static double? DeviceGetPowerUsageWatts(IntPtr device)
    {
        var result = nvmlDeviceGetPowerUsage(device, out var milliwatts);
        if (result == NvmlReturn.NotSupported)
            return null;
        ThrowIfError(result, nameof(nvmlDeviceGetPowerUsage));
        return milliwatts / 1000.0;
    }

    private static void ThrowIfError(NvmlReturn result, string function)
    {
        if (result != NvmlReturn.Success)
            throw new NvmlException(result, function);
    }
}

internal sealed class NvmlException(NvmlReturn returnCode, string function)
    : Exception($"NVML call {function} failed with {returnCode}")
{
    public NvmlReturn ReturnCode { get; } = returnCode;
}

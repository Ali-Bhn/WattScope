using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using WattScope.Core.Gaming;

namespace WattScope.Hardware.Processes;

/// <summary>
/// Passive, cheap process enumeration for game detection. It uses the OS
/// toolhelp snapshot (one kernel call that returns PID, parent PID and
/// executable name for every process -- no handle is opened for that), and only
/// for a process it has not seen before does it open a handle with the single
/// least-privileged right <c>PROCESS_QUERY_LIMITED_INFORMATION</c> to read the
/// image path and creation time. That right does not allow reading or writing
/// the process's memory, injecting anything into it, or controlling it in any
/// way, and it is granted even for most protected processes. The result for
/// each process is cached, so a steady-state poll opens no handles at all.
///
/// Everyday system software (see <see cref="GameCatalog.NeverGame"/>) is not
/// even opened. Nothing here touches a game process beyond that one query.
/// </summary>
public sealed class WindowsProcessSource(ILogger? logger = null) : IProcessSource
{
    private Dictionary<int, ProcessInfo> _cache = new();

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        var entries = ReadToolhelp();
        var next = new Dictionary<int, ProcessInfo>(entries.Count);
        var result = new List<ProcessInfo>(entries.Count);

        foreach (var (pid, parent, name) in entries)
        {
            if (!_cache.TryGetValue(pid, out var info) || info.ParentPid != parent || !string.Equals(info.ExeName, name, StringComparison.OrdinalIgnoreCase))
                info = Resolve(pid, parent, name);
            next[pid] = info;
            result.Add(info);
        }

        _cache = next; // only live processes are kept: bounded memory, and a reused PID is re-resolved
        return result;
    }

    private ProcessInfo Resolve(int pid, int parent, string name)
    {
        var skipOpen = GameCatalog.NeverGame.Contains(name) && !GameCatalog.Launchers.Contains(name);
        if (skipOpen || pid <= 4)
            return new ProcessInfo(pid, parent, name, null, null);

        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return new ProcessInfo(pid, parent, name, null, null); // access denied (elevated/protected) or already gone: name only
        try
        {
            string? path = null;
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                path = buffer.ToString(0, (int)size);

            DateTimeOffset? start = null;
            if (NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0)
                start = new DateTimeOffset(DateTime.FromFileTimeUtc(creation), TimeSpan.Zero);

            return new ProcessInfo(pid, parent, name, path, start);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not resolve process {Pid}.", pid);
            return new ProcessInfo(pid, parent, name, null, null);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private List<(int Pid, int Parent, string Name)> ReadToolhelp()
    {
        var list = new List<(int, int, string)>(256);
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandle)
        {
            logger?.LogWarning("Process snapshot failed (error {Error}); no game detection this poll.", Marshal.GetLastWin32Error());
            return list;
        }
        try
        {
            var entry = new NativeMethods.ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
            for (var ok = NativeMethods.Process32FirstW(snapshot, ref entry); ok; ok = NativeMethods.Process32NextW(snapshot, ref entry))
                list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
        return list;
    }

    private static class NativeMethods
    {
        public const uint Th32csSnapProcess = 0x00000002;
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public UIntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>Reads the product name from an executable's version resource. Cached per path; failures yield null.</summary>
public sealed class FileVersionExecutableMetadata : IExecutableMetadata
{
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public string? GetProductName(string exePath)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(exePath, out var cached))
                return cached;
            string? product = null;
            try
            {
                product = FileVersionInfo.GetVersionInfo(exePath).ProductName;
            }
            catch (Exception)
            {
                // Missing/locked file or no version resource: just no product name.
            }
            return _cache[exePath] = string.IsNullOrWhiteSpace(product) ? null : product.Trim();
        }
    }
}

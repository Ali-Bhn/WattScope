using System.Runtime.InteropServices;
using WattScope.Core.Hardware;
using WattScope.Hardware.Elevation;

namespace WattScope.Hardware.SystemInfo;

public static class OsInfoCollector
{
    public static OsInfo Collect()
    {
        return new OsInfo
        {
            Caption = RuntimeInformation.OSDescription,
            Version = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            IsElevated = PrivilegeChecker.IsElevated()
        };
    }
}

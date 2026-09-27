namespace WattScope.Core.Diagnostics;

/// <summary>Documents the effect of the current process's privilege level on sensor access.</summary>
public sealed record PermissionInfo
{
    public required bool IsElevated { get; init; }
    public required IReadOnlyList<string> KnownLimitations { get; init; }
}

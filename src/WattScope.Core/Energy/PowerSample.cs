namespace WattScope.Core.Energy;

/// <summary>One channel's value at one sample. <see cref="Watts"/> is null unless <see cref="State"/> is Valid -- never zero-filled.</summary>
public readonly record struct ChannelReading(double? Watts, ReadingState State)
{
    public bool IsValid => State == ReadingState.Valid && Watts is not null;

    public static ChannelReading FromWatts(double watts) => new(watts, ReadingState.Valid);

    public static ChannelReading Rejected(ReadingState state) => new(null, state);
}

/// <summary>
/// The energy-relevant subset of one sampling tick: only the selected
/// channels, not the full sensor inventory.
/// </summary>
public sealed record PowerSample
{
    /// <summary>Wall-clock time, used for persistence and for placing energy into time buckets.</summary>
    public required DateTimeOffset UtcTime { get; init; }

    /// <summary>
    /// Process-local monotonic time, used for interval durations. Only
    /// differences between samples produced by the same process are meaningful.
    /// </summary>
    public required TimeSpan MonotonicTime { get; init; }

    public required ChannelReading CpuPackage { get; init; }
    public required ChannelReading GpuBoard { get; init; }

    public ChannelReading Get(EnergyChannel channel) => channel switch
    {
        EnergyChannel.CpuPackage => CpuPackage,
        EnergyChannel.GpuBoard => GpuBoard,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
}

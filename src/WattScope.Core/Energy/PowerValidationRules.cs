namespace WattScope.Core.Energy;

/// <summary>
/// Plausibility checks applied to each selected sensor's value before it may
/// contribute to energy. A rejected value becomes a <see cref="ReadingState"/>
/// with a null value, never a substituted number.
/// </summary>
public sealed record PowerValidationRules
{
    /// <summary>Upper bound for a single sensor's reading. Default is well above any current consumer CPU or GPU.</summary>
    public double MaxPlausibleWatts { get; init; } = 2000.0;

    public ChannelReading Validate(EnergyChannel channel, double watts)
    {
        if (double.IsNaN(watts) || double.IsInfinity(watts))
            return ChannelReading.Rejected(ReadingState.NonFinite);
        if (watts < 0)
            return ChannelReading.Rejected(ReadingState.Negative);
        if (watts > MaxPlausibleWatts)
            return ChannelReading.Rejected(ReadingState.AboveMaximum);
        // A running CPU package never draws exactly 0 W; LibreHardwareMonitor
        // reports 0.0 when it cannot reach RAPL. A GPU can legitimately
        // report 0 W (e.g. a powered-down hybrid-graphics dGPU), so zero is
        // only rejected for the CPU package channel.
        if (watts == 0 && channel == EnergyChannel.CpuPackage)
            return ChannelReading.Rejected(ReadingState.ImplausibleZero);
        return ChannelReading.FromWatts(watts);
    }
}

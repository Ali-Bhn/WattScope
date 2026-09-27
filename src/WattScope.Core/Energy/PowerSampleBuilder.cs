using WattScope.Core.Classification;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;

namespace WattScope.Core.Energy;

/// <summary>A sensor bound to an energy channel for a monitoring session (persisted as session metadata).</summary>
public sealed record EnergySensorBinding
{
    public required EnergyChannel Channel { get; init; }
    public required string ProviderId { get; init; }
    public required string HardwareId { get; init; }
    public required string HardwareName { get; init; }
    public required string SensorId { get; init; }
    public required string SensorName { get; init; }
}

/// <summary>
/// Turns a sampling tick into a <see cref="PowerSample"/> using only the
/// classifier's <em>selected</em> sensors. Bindings are fixed once, at
/// construction, from a discovery-time classification; per tick this is a
/// lookup of a handful of sensor IDs, not a re-classification.
/// </summary>
/// <remarks>
/// Duplicate prevention (docs/ENERGY_MODEL.md, "Channel sources"):
/// only <see cref="PowerSensorRole.CpuPackage"/> and
/// <see cref="PowerSensorRole.GpuBoard"/> selections are used -- never
/// CpuCores/CpuGraphics/GpuChip, which are subsets of those. Several selections
/// for one role are summed only when they are distinct physical devices from
/// a single provider (multi-socket CPU, multiple GPUs seen by one backend);
/// selections for one role from more than one provider cannot be proven to be
/// different devices and make the channel <see cref="ReadingState.Ambiguous"/>.
/// </remarks>
public sealed class PowerSampleBuilder
{
    private readonly PowerValidationRules _rules;
    private readonly ChannelSource _cpu;
    private readonly ChannelSource _gpu;

    public PowerSampleBuilder(PowerClassificationResult classification, PowerValidationRules? rules = null)
    {
        _rules = rules ?? new PowerValidationRules();
        _cpu = BuildSource(EnergyChannel.CpuPackage, PowerSensorRole.CpuPackage, classification);
        _gpu = BuildSource(EnergyChannel.GpuBoard, PowerSensorRole.GpuBoard, classification);
        Bindings = [.. _cpu.Bindings, .. _gpu.Bindings];
    }

    public IReadOnlyList<EnergySensorBinding> Bindings { get; }

    /// <summary>Set when a channel can never be valid in this session (no sensor, or ambiguous selection).</summary>
    public ReadingState? FixedState(EnergyChannel channel) =>
        channel == EnergyChannel.CpuPackage ? _cpu.FixedState : _gpu.FixedState;

    public PowerSample Build(SamplingTick tick) => Build(tick.Readings, tick.Timestamp, tick.MonotonicTime);

    public PowerSample Build(IReadOnlyList<SensorReading> readings, DateTimeOffset utcTime, TimeSpan monotonicTime) => new()
    {
        UtcTime = utcTime,
        MonotonicTime = monotonicTime,
        CpuPackage = Read(_cpu, readings),
        GpuBoard = Read(_gpu, readings)
    };

    private ChannelReading Read(ChannelSource source, IReadOnlyList<SensorReading> readings)
    {
        if (source.FixedState is { } fixedState)
            return ChannelReading.Rejected(fixedState);

        var total = 0.0;
        foreach (var binding in source.Bindings)
        {
            SensorReading? match = null;
            foreach (var reading in readings)
            {
                if (reading.SensorId == binding.SensorId && reading.ProviderId == binding.ProviderId)
                {
                    match = reading;
                    break;
                }
            }

            if (match is null)
                return ChannelReading.Rejected(ReadingState.Missing);
            if (!match.IsAvailable)
                return ChannelReading.Rejected(ReadingState.Unavailable);

            var validated = _rules.Validate(source.Channel, match.Value!.Value);
            if (!validated.IsValid)
                return validated;
            total += validated.Watts!.Value;
        }

        return ChannelReading.FromWatts(total);
    }

    private static ChannelSource BuildSource(EnergyChannel channel, PowerSensorRole role, PowerClassificationResult classification)
    {
        var selections = classification.Selections.Where(s => s.Role == role).ToList();
        if (selections.Count == 0)
            return new ChannelSource(channel, [], ReadingState.NotPresent);

        if (selections.Any(s => s.Selected is null))
            return new ChannelSource(channel, [], ReadingState.Ambiguous);

        var selected = selections.Select(s => s.Selected!.Reading).ToList();
        if (selected.Select(r => r.ProviderId).Distinct().Count() > 1)
            return new ChannelSource(channel, [], ReadingState.Ambiguous);

        var bindings = selected
            .DistinctBy(r => (r.ProviderId, r.HardwareId))
            .Select(r => new EnergySensorBinding
            {
                Channel = channel,
                ProviderId = r.ProviderId,
                HardwareId = r.HardwareId,
                HardwareName = r.HardwareName,
                SensorId = r.SensorId,
                SensorName = r.SensorName
            })
            .ToList();

        return new ChannelSource(channel, bindings, null);
    }

    private sealed record ChannelSource(EnergyChannel Channel, IReadOnlyList<EnergySensorBinding> Bindings, ReadingState? FixedState);
}

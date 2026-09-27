using System.Globalization;

namespace WattScope.Core.Estimation;

/// <summary>
/// User-configurable assumptions for estimating wall (AC) power from measured
/// component power. Every value here is an assumption, not a measurement, and
/// is reported alongside every estimate. See docs/ENERGY_MODEL.md, "Estimation model".
/// </summary>
public sealed record EstimationSettings
{
    public const double DefaultOtherComponentsWatts = 50.0;
    public const double DefaultPsuEfficiency = 0.90;

    /// <summary>Assumed constant draw of everything not measured (motherboard, RAM, storage, fans, peripherals powered by the PSU).</summary>
    public double OtherComponentsWatts { get; init; } = DefaultOtherComponentsWatts;

    /// <summary>Assumed PSU efficiency (DC out / AC in), in (0, 1].</summary>
    public double PsuEfficiency { get; init; } = DefaultPsuEfficiency;

    /// <summary>Whether measured CPU package power is a required input. Disable only on machines where it is known to be unreadable.</summary>
    public bool IncludeCpuPackage { get; init; } = true;

    /// <summary>Whether measured GPU board power is a required input. Disable on machines without a discrete GPU.</summary>
    public bool IncludeGpuBoard { get; init; } = true;

    public void Validate()
    {
        if (!double.IsFinite(OtherComponentsWatts) || OtherComponentsWatts < 0 || OtherComponentsWatts > 2000)
            throw new ArgumentOutOfRangeException(nameof(OtherComponentsWatts), OtherComponentsWatts, "Other component power must be between 0 and 2000 W.");
        if (!double.IsFinite(PsuEfficiency) || PsuEfficiency <= 0 || PsuEfficiency > 1)
            throw new ArgumentOutOfRangeException(nameof(PsuEfficiency), PsuEfficiency, "PSU efficiency must be in (0, 1].");
        if (!IncludeCpuPackage && !IncludeGpuBoard)
            throw new ArgumentException("At least one measured component (CPU package or GPU board) must be included.");
    }

    public IReadOnlyList<string> DescribeAssumptions()
    {
        var inputs = new List<string>();
        if (IncludeCpuPackage) inputs.Add("measured CPU package power");
        if (IncludeGpuBoard) inputs.Add("measured GPU board power");
        return
        [
            $"Estimated wall power = ({string.Join(" + ", inputs)} + other components) / PSU efficiency.",
            $"Other components: {OtherComponentsWatts.ToString("0.##", CultureInfo.InvariantCulture)} W (assumed constant, not measured).",
            $"PSU efficiency: {(PsuEfficiency * 100).ToString("0.#", CultureInfo.InvariantCulture)} % (assumed constant, not measured).",
            "This is an estimate, not a measurement of wall power."
        ];
    }
}

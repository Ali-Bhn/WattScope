using WattScope.Core.Energy;

namespace WattScope.Core.Estimation;

/// <summary>How many of the required measured inputs an estimate was based on.</summary>
public enum EstimateCompleteness
{
    /// <summary>Every included measured input was valid.</summary>
    Complete,

    /// <summary>
    /// At least one included input was missing. The value covers only the
    /// inputs that were available (plus the assumed other-component power), so
    /// it is a lower bound, never a complete estimate. Missing inputs are
    /// omitted, not treated as zero.
    /// </summary>
    Partial,

    /// <summary>No included input was valid; no estimate exists.</summary>
    None
}

public readonly record struct WallEstimate(double? Value, EstimateCompleteness Completeness);

/// <summary>
/// Estimated wall energy/power = (sum of included measured inputs + other
/// components) / PSU efficiency. Works identically on energy (J over an
/// interval) and on instantaneous power (W), because the model is linear.
/// </summary>
public static class WallPowerEstimator
{
    /// <param name="cpuJoules">Measured CPU package energy for the interval, or null if not measured.</param>
    /// <param name="gpuJoules">Measured GPU board energy for the interval, or null if not measured.</param>
    /// <param name="seconds">Interval length, used for the constant other-component term.</param>
    public static WallEstimate EstimateEnergy(double? cpuJoules, double? gpuJoules, double seconds, EstimationSettings settings)
    {
        var required = 0;
        var present = 0;
        var measured = 0.0;

        if (settings.IncludeCpuPackage)
        {
            required++;
            if (cpuJoules is { } c) { present++; measured += c; }
        }

        if (settings.IncludeGpuBoard)
        {
            required++;
            if (gpuJoules is { } g) { present++; measured += g; }
        }

        if (present == 0)
            return new WallEstimate(null, EstimateCompleteness.None);

        var value = (measured + settings.OtherComponentsWatts * seconds) / settings.PsuEfficiency;
        return new WallEstimate(value, present == required ? EstimateCompleteness.Complete : EstimateCompleteness.Partial);
    }

    /// <summary>Instantaneous estimated wall power for one sample (for live display).</summary>
    public static WallEstimate EstimateWatts(PowerSample sample, EstimationSettings settings) =>
        EstimateEnergy(sample.CpuPackage.IsValid ? sample.CpuPackage.Watts : null,
                       sample.GpuBoard.IsValid ? sample.GpuBoard.Watts : null,
                       1.0, settings);
}

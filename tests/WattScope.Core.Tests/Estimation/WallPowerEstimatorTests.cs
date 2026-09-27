using WattScope.Core.Estimation;

namespace WattScope.Core.Tests.Estimation;

public class WallPowerEstimatorTests
{
    private static readonly EstimationSettings Settings = new() { OtherComponentsWatts = 50, PsuEfficiency = 0.9 };

    [Fact]
    public void AllInputsMeasured_IsComplete()
    {
        var estimate = WallPowerEstimator.EstimateEnergy(100, 200, 1, Settings);

        Assert.Equal(EstimateCompleteness.Complete, estimate.Completeness);
        Assert.Equal(350 / 0.9, estimate.Value!.Value, 12);
    }

    [Fact]
    public void MissingCpu_IsPartial_AndMissingInputIsOmittedNotZeroed()
    {
        var estimate = WallPowerEstimator.EstimateEnergy(null, 200, 1, Settings);

        Assert.Equal(EstimateCompleteness.Partial, estimate.Completeness);
        Assert.Equal(250 / 0.9, estimate.Value!.Value, 12);
    }

    [Fact]
    public void NoInputsMeasured_HasNoEstimate()
    {
        var estimate = WallPowerEstimator.EstimateEnergy(null, null, 1, Settings);

        Assert.Equal(EstimateCompleteness.None, estimate.Completeness);
        Assert.Null(estimate.Value);
    }

    [Fact]
    public void ExcludedInput_DoesNotMakeTheEstimatePartial()
    {
        var gpuOnly = Settings with { IncludeCpuPackage = false };

        var estimate = WallPowerEstimator.EstimateEnergy(null, 200, 1, gpuOnly);

        Assert.Equal(EstimateCompleteness.Complete, estimate.Completeness);
        Assert.Equal(250 / 0.9, estimate.Value!.Value, 12);
    }

    [Fact]
    public void ExcludedInput_IsIgnoredEvenWhenMeasured()
    {
        var gpuOnly = Settings with { IncludeCpuPackage = false };

        var estimate = WallPowerEstimator.EstimateEnergy(100, 200, 1, gpuOnly);

        Assert.Equal(250 / 0.9, estimate.Value!.Value, 12);
    }

    [Fact]
    public void OtherComponentPower_ScalesWithIntervalLength()
    {
        var estimate = WallPowerEstimator.EstimateEnergy(100 * 60, 200 * 60, 60, Settings);

        Assert.Equal(350 * 60 / 0.9, estimate.Value!.Value, 9);
    }

    [Theory]
    [InlineData(50, 0.0)]
    [InlineData(50, 1.01)]
    [InlineData(50, double.NaN)]
    [InlineData(-1, 0.9)]
    [InlineData(double.PositiveInfinity, 0.9)]
    public void InvalidSettings_AreRejected(double other, double efficiency)
    {
        var settings = new EstimationSettings { OtherComponentsWatts = other, PsuEfficiency = efficiency };

        Assert.ThrowsAny<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void ExcludingEveryInput_IsRejected()
    {
        var settings = new EstimationSettings { IncludeCpuPackage = false, IncludeGpuBoard = false };

        Assert.ThrowsAny<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Assumptions_StateThatTheValueIsAnEstimate()
    {
        var text = string.Join("\n", Settings.DescribeAssumptions());

        Assert.Contains("50 W", text);
        Assert.Contains("90 %", text);
        Assert.Contains("not a measurement", text);
    }
}

using WattScope.Hardware.Elevation;

namespace WattScope.Hardware.Tests.Elevation;

public class PrivilegeCheckerTests
{
    [Fact]
    public void IsElevated_DoesNotThrow_AndReturnsABoolean()
    {
        var result = PrivilegeChecker.IsElevated();
        Assert.IsType<bool>(result);
    }
}

using System.Globalization;
using WattScope.Core.Classification;
using WattScope.Core.Diagnostics;
using WattScope.Core.Export;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Core.Tests.Export;

public class JsonExportServiceTests
{
    private static ExportReport SampleReport() => new()
    {
        AppVersion = "0.1.0",
        GeneratedAt = DateTimeOffset.UtcNow,
        OperatingSystem = new OsInfo { Caption = "Windows", Version = "10.0", Architecture = "X64", IsElevated = false },
        Hardware = [],
        Providers = [],
        SensorInventory =
        [
            new SensorReading
            {
                ProviderId = "Test",
                HardwareId = "hw0",
                HardwareName = "Test Hardware",
                SensorId = "s0",
                SensorName = "Test Power",
                SensorType = "Power",
                Unit = "W",
                Value = 1234.5678,
                Availability = SensorAvailability.Available,
                Timestamp = DateTimeOffset.UtcNow
            }
        ],
        SelectedPowerSensors = [],
        Readings = [],
        Diagnostics = [],
        Permissions = new PermissionInfo { IsElevated = false, KnownLimitations = [] }
    };

    [Fact]
    public void Serialize_UsesInvariantNumericFormatting_RegardlessOfCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // German culture formats decimals with a comma, e.g. "1234,5678".
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var json = new JsonExportService().Serialize(SampleReport());

            Assert.Contains("1234.5678", json);
            Assert.DoesNotContain("1234,5678", json);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task WriteToFileAsync_WritesReadableFile_AndLeavesNoTempFileBehind()
    {
        var tempDir = Directory.CreateTempSubdirectory("wattscope-export-test-");
        try
        {
            var path = Path.Combine(tempDir.FullName, "report.json");
            await new JsonExportService().WriteToFileAsync(SampleReport(), path);

            Assert.True(File.Exists(path));
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("\"appVersion\"", content);

            var leftoverTempFiles = Directory.GetFiles(tempDir.FullName, "*.tmp-*");
            Assert.Empty(leftoverTempFiles);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WriteToFileAsync_OverwritesAnExistingFile()
    {
        var tempDir = Directory.CreateTempSubdirectory("wattscope-export-test-");
        try
        {
            var path = Path.Combine(tempDir.FullName, "report.json");
            await File.WriteAllTextAsync(path, "old content");

            await new JsonExportService().WriteToFileAsync(SampleReport(), path);

            var content = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("old content", content);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}

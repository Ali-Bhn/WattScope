using System.Text.Json;
using System.Text.Json.Serialization;

namespace WattScope.Core.Export;

public sealed class JsonExportService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Numbers are always written invariant by System.Text.Json regardless of thread culture.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string Serialize(ExportReport report) => JsonSerializer.Serialize(report, Options);

    /// <summary>Writes to a temp file in the destination directory and renames into place, so a crash never leaves a partial report.</summary>
    public async Task WriteToFileAsync(ExportReport report, string destinationPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = Serialize(report);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

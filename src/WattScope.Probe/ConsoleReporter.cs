using System.Globalization;
using WattScope.Core.Classification;
using WattScope.Core.Discovery;
using WattScope.Core.Sampling;

namespace WattScope.Probe;

public static class ConsoleReporter
{
    public static void PrintDiscovery(DiscoverySnapshot snapshot)
    {
        Console.WriteLine("=== Operating System ===");
        Console.WriteLine($"  {snapshot.OperatingSystem.Caption} ({snapshot.OperatingSystem.Architecture})");
        Console.WriteLine($"  Elevated: {snapshot.OperatingSystem.IsElevated}");
        Console.WriteLine();

        Console.WriteLine("=== Hardware ===");
        foreach (var device in snapshot.Hardware.Where(d => d.ParentId is null))
        {
            Console.WriteLine($"  [{device.Category}] {device.Name}" + (device.Vendor is null ? "" : $" ({device.Vendor})") + $"  <source: {device.Source}>");
        }
        Console.WriteLine();

        Console.WriteLine("=== Providers ===");
        foreach (var provider in snapshot.Providers)
        {
            var status = provider.IsAvailable ? "available" : $"unavailable ({provider.UnavailableReason})";
            Console.WriteLine($"  {provider.ProviderId}: {status}");
        }
        Console.WriteLine();

        Console.WriteLine($"=== Sensors discovered: {snapshot.SensorInventory.Count} ===");
        var powerSensors = snapshot.SensorInventory.Where(s => s.SensorType == "Power").ToList();
        Console.WriteLine($"  Power sensors: {powerSensors.Count}");
        foreach (var sensor in powerSensors)
        {
            var valueText = sensor.IsAvailable
                ? sensor.Value!.Value.ToString("F2", CultureInfo.InvariantCulture) + " " + sensor.Unit
                : $"n/a ({sensor.Availability})";
            Console.WriteLine($"    [{sensor.ProviderId}] {sensor.HardwareName} / {sensor.SensorName}: {valueText}");
        }
        Console.WriteLine();

        Console.WriteLine("=== Selected power sensors (by role) ===");
        foreach (var selection in snapshot.PowerClassification.Selections)
        {
            if (selection.Selected is { } selected)
            {
                var v = selected.Reading.IsAvailable
                    ? selected.Reading.Value!.Value.ToString("F2", CultureInfo.InvariantCulture) + " " + selected.Reading.Unit
                    : $"n/a ({selected.Reading.Availability})";
                Console.WriteLine($"  {selection.Role}: {selected.Reading.SensorName} [{selected.Reading.ProviderId}] = {v}");
            }
            else
            {
                Console.WriteLine($"  {selection.Role}: (not selected -- {selection.Candidates.Count} ambiguous candidate(s))");
            }
            Console.WriteLine($"    reason: {selection.PrecedenceExplanation}");
        }
        Console.WriteLine();

        if (snapshot.Permissions.KnownLimitations.Count > 0)
        {
            Console.WriteLine("=== Known limitations ===");
            foreach (var limitation in snapshot.Permissions.KnownLimitations)
                Console.WriteLine($"  - {limitation}");
            Console.WriteLine();
        }

        if (snapshot.Diagnostics.Count > 0)
        {
            Console.WriteLine("=== Diagnostics ===");
            foreach (var diag in snapshot.Diagnostics)
                Console.WriteLine($"  [{diag.Severity}] {diag.Source}: {diag.Message}");
            Console.WriteLine();
        }
    }

    public static void PrintTick(SamplingTick tick, PowerClassificationResult classification)
    {
        var selectedIds = classification.Selections
            .Where(s => s.Selected is not null)
            .ToDictionary(s => (s.Selected!.Reading.ProviderId, s.Selected!.Reading.SensorId), s => s.Role);

        var parts = new List<string>();
        foreach (var reading in tick.Readings)
        {
            if (!selectedIds.TryGetValue((reading.ProviderId, reading.SensorId), out var role))
                continue;

            var v = reading.IsAvailable
                ? reading.Value!.Value.ToString("F1", CultureInfo.InvariantCulture) + reading.Unit
                : "n/a";
            parts.Add($"{role}={v}");
        }

        var gapNote = tick.GapDetected ? $"  [GAP {tick.GapDuration!.Value.TotalMilliseconds:F0}ms]" : "";
        var errorNote = tick.Errors.Count > 0 ? $"  [{tick.Errors.Count} error(s)]" : "";

        Console.WriteLine($"[{tick.Sequence,5}] {tick.Timestamp:HH:mm:ss.fff}  {string.Join("  ", parts)}{gapNote}{errorNote}");
    }
}

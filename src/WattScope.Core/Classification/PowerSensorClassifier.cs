using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Core.Classification;

/// <summary>
/// Classifies discovered power sensors into roles and selects, per role, at
/// most one sensor to represent that measurement -- never a sum of
/// overlapping sensors. See docs/SENSOR_CONTRACT.md for the full precedence
/// policy this implements.
/// </summary>
public sealed class PowerSensorClassifier
{
    private const string LibreHardwareMonitorProviderId = "LibreHardwareMonitor";
    private const string NvmlProviderId = "Nvml";

    public PowerClassificationResult Classify(
        IReadOnlyList<HardwareDevice> devices,
        IReadOnlyList<SensorReading> readings)
    {
        var deviceLookup = devices.ToDictionary(d => (d.Source, d.Id));

        var candidates = new List<PowerSensorCandidate>();
        foreach (var reading in readings)
        {
            if (!string.Equals(reading.SensorType, "Power", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!deviceLookup.TryGetValue((reading.ProviderId, reading.HardwareId), out var device))
                continue;

            var (role, source) = reading.ProviderId switch
            {
                LibreHardwareMonitorProviderId => PowerSensorNameRules.ClassifyLibreHardwareMonitor(device.Category, reading.SensorName),
                NvmlProviderId => PowerSensorNameRules.ClassifyNvml(reading.SensorName),
                _ => (PowerSensorRole.Other, ClassificationSource.Unclassified)
            };

            candidates.Add(new PowerSensorCandidate
            {
                Reading = reading,
                Role = role,
                Source = source
            });
        }

        var selections = BuildSelections(candidates);

        // Mark overlap on every candidate that belongs to a role group with more than one candidate.
        var overlapCounts = candidates
            .Where(c => c.Role != PowerSensorRole.Other)
            .GroupBy(c => c.Role)
            .ToDictionary(g => g.Key, g => g.Count());

        var finalCandidates = candidates
            .Select(c => c with { HasOverlap = c.Role != PowerSensorRole.Other && overlapCounts[c.Role] > 1 })
            .ToList();

        return new PowerClassificationResult
        {
            AllCandidates = finalCandidates,
            Selections = selections
        };
    }

    private static List<PowerSensorSelection> BuildSelections(List<PowerSensorCandidate> candidates)
    {
        var selections = new List<PowerSensorSelection>();

        foreach (var role in Enum.GetValues<PowerSensorRole>())
        {
            if (role == PowerSensorRole.Other)
                continue;

            var roleCandidates = candidates.Where(c => c.Role == role).ToList();
            if (roleCandidates.Count == 0)
                continue;

            if (role is PowerSensorRole.CpuPackage or PowerSensorRole.CpuCores or PowerSensorRole.CpuGraphics)
            {
                // CPU roles are only ever reported by LibreHardwareMonitor in this
                // application; no cross-provider precedence decision is needed.
                selections.AddRange(SelectPerDevice(role, roleCandidates,
                    "Single-provider role (CPU); no cross-provider candidate exists, so the discovered sensor is selected directly."));
                continue;
            }

            // GPU roles (GpuBoard, GpuChip) can be reported by both
            // LibreHardwareMonitor and NVML for the same physical NVIDIA GPU.
            // Only devices that actually produced a candidate for this role
            // are considered -- the sensor-independent WMI inventory device
            // (which never yields a Power reading) must not count here, or a
            // single physical GPU would look like multiple GPUs.
            var nvmlCandidates = roleCandidates.Where(c => c.Reading.ProviderId == NvmlProviderId).ToList();
            var lhmCandidates = roleCandidates.Where(c => c.Reading.ProviderId == LibreHardwareMonitorProviderId).ToList();
            var nvmlDeviceCount = nvmlCandidates.Select(c => c.Reading.HardwareId).Distinct().Count();
            var lhmDeviceCount = lhmCandidates.Select(c => c.Reading.HardwareId).Distinct().Count();

            if (nvmlCandidates.Count > 0 && lhmCandidates.Count > 0 && nvmlDeviceCount == 1 && lhmDeviceCount == 1)
            {
                // Exactly one NVIDIA GPU on each side: the single NVML
                // candidate and the single LibreHardwareMonitor candidate
                // necessarily describe the same physical device. NVML wins:
                // it queries the driver directly rather than through an
                // intermediate abstraction.
                var selected = nvmlCandidates[0];
                selections.Add(new PowerSensorSelection
                {
                    Role = role,
                    Selected = selected,
                    Candidates = roleCandidates,
                    PrecedenceExplanation =
                        "Single NVIDIA GPU present; NVML and LibreHardwareMonitor both reported this role for it. " +
                        "NVML selected: direct vendor driver query preferred over a derived value."
                });
            }
            else if (nvmlCandidates.Count > 0 && lhmCandidates.Count > 0)
            {
                // Multiple devices reported by at least one provider:
                // correlating which NVML device index matches which
                // LibreHardwareMonitor hardware entry cannot be done reliably
                // without further OS-level device-path correlation. Rather
                // than guess, keep every candidate reported and select none.
                selections.Add(new PowerSensorSelection
                {
                    Role = role,
                    Selected = null,
                    Candidates = roleCandidates,
                    PrecedenceExplanation =
                        $"NVML reports {nvmlDeviceCount} device(s) and LibreHardwareMonitor reports {lhmDeviceCount} " +
                        "device(s) for this role; cross-provider device correlation is ambiguous by ordinal position " +
                        "alone and was not attempted. No sensor was selected for this role; all discovered candidates are listed."
                });
            }
            else
            {
                // Only one provider reported this role: select per physical device.
                selections.AddRange(SelectPerDevice(role, roleCandidates,
                    "Only one provider reported this role; the discovered sensor for each device is selected directly."));
            }
        }

        return selections;
    }

    private static IEnumerable<PowerSensorSelection> SelectPerDevice(
        PowerSensorRole role, List<PowerSensorCandidate> roleCandidates, string explanation)
    {
        foreach (var group in roleCandidates.GroupBy(c => (c.Reading.ProviderId, c.Reading.HardwareId)))
        {
            var ordered = group.OrderBy(c => c.Reading.SensorId, StringComparer.Ordinal).ToList();
            yield return new PowerSensorSelection
            {
                Role = role,
                Selected = ordered[0],
                Candidates = ordered,
                PrecedenceExplanation = ordered.Count > 1
                    ? explanation + " Multiple sensors from the same device matched this role; the first by sensor ID was selected and the rest excluded to avoid double counting."
                    : explanation
            };
        }
    }
}

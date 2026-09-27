using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WattScope.Core.Diagnostics;
using WattScope.Core.Sensors;

namespace WattScope.Core.Sampling;

/// <summary>
/// Drives periodic sampling across a fixed set of already-initialized
/// providers. Never re-enumerates hardware; each tick only calls
/// <see cref="ISensorProvider.GetSensorReadings"/>. Ticks are produced
/// strictly sequentially -- the next tick's work never starts before the
/// previous one finished -- so sampling operations cannot overlap.
/// </summary>
public sealed class SamplingEngine
{
    private readonly IReadOnlyList<ISensorProvider> _providers;
    private readonly ILogger<SamplingEngine>? _logger;

    public SamplingEngine(IReadOnlyList<ISensorProvider> providers, ILogger<SamplingEngine>? logger = null)
    {
        _providers = providers;
        _logger = logger;
    }

    public async IAsyncEnumerable<SamplingTick> RunAsync(
        SamplingOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var sequence = 0;
        var nextDue = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (options.Duration is { } duration && stopwatch.Elapsed >= duration)
                yield break;

            var tickStart = stopwatch.Elapsed;
            var gapDetected = false;
            TimeSpan? gapDuration = null;
            var lateness = TimeSpan.Zero;

            if (sequence > 0)
            {
                lateness = tickStart - nextDue;
                if (lateness > options.Interval * (options.GapDetectionFactor - 1.0))
                {
                    gapDetected = true;
                    gapDuration = lateness;
                    _logger?.LogWarning("Sampling gap detected: tick {Sequence} started {Lateness}ms later than scheduled",
                        sequence, lateness.TotalMilliseconds);
                }
            }

            var readings = new List<SensorReading>();
            var errors = new List<DiagnosticMessage>();
            var timestamp = DateTimeOffset.UtcNow;
            var monotonicTimestamp = Stopwatch.GetTimestamp();

            foreach (var provider in _providers)
            {
                try
                {
                    readings.AddRange(provider.GetSensorReadings());
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Provider {ProviderId} failed to refresh sensor readings", provider.ProviderId);
                    errors.Add(new DiagnosticMessage
                    {
                        Severity = DiagnosticSeverity.Error,
                        Source = provider.ProviderId,
                        Message = $"Sensor refresh failed: {ex.Message}",
                        Timestamp = timestamp
                    });
                }
            }

            yield return new SamplingTick
            {
                Sequence = sequence,
                Timestamp = timestamp,
                MonotonicTime = Stopwatch.GetElapsedTime(0, monotonicTimestamp),
                ReadDuration = Stopwatch.GetElapsedTime(monotonicTimestamp),
                Readings = readings,
                Errors = errors,
                GapDetected = gapDetected,
                GapDuration = gapDuration
            };

            sequence++;
            // Stay on the fixed schedule (no drift), except when this tick was
            // already late by more than a whole interval (stall, sleep/resume):
            // then re-anchor on this tick instead of firing a burst of
            // back-to-back catch-up ticks for every missed slot.
            nextDue = lateness > options.Interval ? tickStart + options.Interval : nextDue + options.Interval;
            var remaining = nextDue - stopwatch.Elapsed;

            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
    }
}

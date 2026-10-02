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
    private readonly TimeProvider _time;

    /// <param name="timeProvider">
    /// Clock and timer source; defaults to the system one (the same
    /// <see cref="Stopwatch"/> counter and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// timers as before). Tests pass a virtual clock so scheduling is deterministic.
    /// </param>
    public SamplingEngine(IReadOnlyList<ISensorProvider> providers, ILogger<SamplingEngine>? logger = null, TimeProvider? timeProvider = null)
    {
        _providers = providers;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<SamplingTick> RunAsync(
        SamplingOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runStart = _time.GetTimestamp();
        var sequence = 0;
        var nextDue = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (options.Duration is { } duration && _time.GetElapsedTime(runStart) >= duration)
                yield break;

            var tickStart = _time.GetElapsedTime(runStart);
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
            var timestamp = _time.GetUtcNow();
            var monotonicTimestamp = _time.GetTimestamp();

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
                MonotonicTime = _time.GetElapsedTime(0, monotonicTimestamp),
                ReadDuration = _time.GetElapsedTime(monotonicTimestamp),
                Readings = readings,
                Errors = errors,
                GapDetected = gapDetected,
                GapDuration = gapDuration
            };

            sequence++;
            // Stay on the fixed schedule (no drift), except when this tick was
            // already late by more than a whole interval (stall, sleep/resume):
            // then re-anchor on this tick instead of firing a burst of
            // back-to-back catch-up ticks for every missed slot. A tick late by
            // at most one interval keeps the schedule, so the tick after it can
            // follow sooner than one interval -- once, never as a burst.
            nextDue = lateness > options.Interval ? tickStart + options.Interval : nextDue + options.Interval;
            var remaining = nextDue - _time.GetElapsedTime(runStart);

            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
    }
}

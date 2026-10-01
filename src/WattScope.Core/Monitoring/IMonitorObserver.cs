using WattScope.Core.Energy;

namespace WattScope.Core.Monitoring;

/// <summary>
/// Optional listener on an <see cref="EnergyMonitor"/> (Milestone 005: gaming
/// sessions). It is told about every accepted sample together with what the
/// integrator made of it, so it can derive its own view (for example the
/// energy of a gaming session) from the <em>same</em> verified intervals the
/// monitor persists -- there is no second integration and no raw-sample copy.
///
/// Called on the monitor's lock; implementations must be fast and must never
/// throw into the monitor (the monitor also guards against it). They must not
/// call back into the monitor.
/// </summary>
public interface IMonitorObserver
{
    /// <summary>A sample was accepted (not a duplicate). <paramref name="step"/> holds the verified interval and/or gap it produced.</summary>
    void OnSample(PowerSample sample, IntegrationStep step);

    /// <summary>The OS is suspending; samples are ignored until <see cref="OnResumed"/>.</summary>
    void OnSuspended();

    /// <summary>The OS resumed after a suspend.</summary>
    void OnResumed();
}

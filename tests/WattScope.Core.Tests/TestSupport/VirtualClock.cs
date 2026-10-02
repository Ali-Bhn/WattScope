namespace WattScope.Core.Tests.TestSupport;

/// <summary>
/// A virtual clock for scheduling tests: time moves only when the test advances it (a simulated slow
/// provider read) or when the code under test waits on a timer (the wait completes at once and the
/// clock jumps by exactly the requested delay). Nothing depends on the real scheduler, so the tick
/// times a test observes are exact and identical on an idle and on a loaded machine.
/// </summary>
public sealed class VirtualClock : TimeProvider
{
    // Deliberately not zero: nothing may assume the monotonic counter starts at the run's start.
    private long _ticks = TimeSpan.FromSeconds(1000).Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => Synthetic.BaseUtc + TimeSpan.FromTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
            callback(state);
        }
        return new CompletedTimer();
    }

    private sealed class CompletedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

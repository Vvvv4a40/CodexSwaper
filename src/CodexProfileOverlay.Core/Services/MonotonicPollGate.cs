namespace CodexProfileOverlay.Core.Services;

// Rate-limit expensive scheduling work without depending on wall-clock adjustments.
public sealed class MonotonicPollGate
{
    private readonly TimeProvider clock;
    private readonly TimeSpan interval;
    private long? lastEntry;

    public MonotonicPollGate(TimeSpan interval, TimeProvider? clock = null)
    {
        if (interval <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(interval)); }
        this.interval = interval;
        this.clock = clock ?? TimeProvider.System;
    }

    public bool TryEnter()
    {
        long now = clock.GetTimestamp();
        if (lastEntry is long previous && clock.GetElapsedTime(previous, now) < interval) { return false; }
        lastEntry = now;
        return true;
    }

    public void Reset() => lastEntry = null;
}

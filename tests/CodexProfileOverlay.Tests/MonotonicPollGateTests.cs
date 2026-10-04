using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class MonotonicPollGateTests
{
    [Fact]
    public void WindowTicks_PerformFourSchedulingChecksPerMinuteInsteadOfEighty()
    {
        var clock = new TestClock();
        var gate = new MonotonicPollGate(TimeSpan.FromSeconds(15), clock);
        int checks = 0;
        for (int tick = 0; tick < 80; tick++)
        {
            if (gate.TryEnter()) { checks++; }
            clock.Advance(TimeSpan.FromMilliseconds(750));
        }
        Assert.Equal(4, checks);
    }

    [Fact]
    public void Reset_AllowsImmediateCheckAfterSettingsOrProfilesChange()
    {
        var gate = new MonotonicPollGate(TimeSpan.FromSeconds(15), new TestClock());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        gate.Reset();
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void WallClockChange_DoesNotDelayTheMonotonicSchedule()
    {
        var clock = new TestClock();
        var gate = new MonotonicPollGate(TimeSpan.FromSeconds(15), clock);
        Assert.True(gate.TryEnter());
        clock.WallClock = DateTimeOffset.UtcNow.AddYears(-1);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(gate.TryEnter());
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public DateTimeOffset WallClock { get; set; } = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => WallClock;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}

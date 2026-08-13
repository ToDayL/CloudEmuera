using CloudEmuera.Api.Realtime;
using Xunit;

namespace CloudEmuera.Realtime.Tests;

[Trait("Category", "Snapshot")]
[Trait("Category", "Backpressure")]
public sealed class RealtimeResyncFailureTrackerTests
{
    [Fact]
    public void ThreeConsecutiveFailuresWithinTheWindowExceedTheLimit()
    {
        var clock = new TestTimeProvider();
        var tracker = new RealtimeResyncFailureTracker(clock.GetUtcNow(), TimeSpan.FromSeconds(30), 3);

        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.RegisterFailure(clock.GetUtcNow()));
    }

    [Fact]
    public void TheWindowRestartsAfterTheConfiguredInterval()
    {
        var clock = new TestTimeProvider();
        var tracker = new RealtimeResyncFailureTracker(clock.GetUtcNow(), TimeSpan.FromSeconds(30), 3);

        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.RegisterFailure(clock.GetUtcNow()));
    }

    [Fact]
    public void ResetClearsTheFailureCount()
    {
        var clock = new TestTimeProvider();
        var tracker = new RealtimeResyncFailureTracker(clock.GetUtcNow(), TimeSpan.FromSeconds(30), 3);

        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        tracker.Reset();
        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        Assert.True(tracker.RegisterFailure(clock.GetUtcNow()));
        Assert.False(tracker.RegisterFailure(clock.GetUtcNow()));
    }

    [Fact]
    public void InvalidConfigurationIsRejected()
    {
        var clock = new TestTimeProvider();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RealtimeResyncFailureTracker(clock.GetUtcNow(), TimeSpan.FromSeconds(30), 0));
    }
}

using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Time;

[Trait("Category", "RuntimePaths")]
public sealed class RuntimeClockTests
{
    [Fact]
    public async Task ManualClockDoesNotCompleteBeforeAdvance()
    {
        var clock = new ManualRuntimeClock();
        ValueTask wait = clock.DelayAsync(TimeSpan.FromSeconds(1));
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(wait.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await wait;
    }

    [Fact]
    public async Task EqualDeadlinesCompleteByRegistrationOrder()
    {
        var clock = new ManualRuntimeClock();
        ValueTask first = clock.DelayAsync(TimeSpan.FromSeconds(1));
        ValueTask second = clock.DelayAsync(TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(first.AsTask(), second.AsTask());
        Assert.Equal(2, clock.LastCompletedOrders.Count);
        Assert.Equal(0, clock.LastCompletedOrders[0]);
        Assert.Equal(1, clock.LastCompletedOrders[1]);
    }

    [Fact]
    public async Task CancellationRemovesWaiter()
    {
        var clock = new ManualRuntimeClock();
        using var cancellation = new CancellationTokenSource();
        ValueTask wait = clock.DelayAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(wait.IsCompleted);
    }

    [Fact]
    public void WallClockAdjustmentDoesNotChangeMonotonicElapsedTime()
    {
        var clock = new ManualRuntimeClock();
        long start = clock.GetTimestamp();
        clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddDays(4));
        Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(start, clock.GetTimestamp()));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(start, clock.GetTimestamp()));
    }

    [Fact]
    public void ManualDeadlineUsesCumulativeElapsedTime()
    {
        var clock = new ManualRuntimeClock();
        RuntimeDeadline deadline = RuntimeDeadline.After(clock, TimeSpan.FromSeconds(1));

        Assert.False(deadline.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(1), deadline.Remaining);

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(deadline.IsExpired);
        Assert.Equal(TimeSpan.FromMilliseconds(1), deadline.Remaining);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Fact]
    public void ProductionClockCanCreateDeadlineWithoutTimestampUnitInference()
    {
        var clock = new TimeProviderRuntimeClock();

        RuntimeDeadline deadline = RuntimeDeadline.After(clock, TimeSpan.FromMilliseconds(1));

        Assert.Equal(TimeSpan.FromMilliseconds(1), deadline.Timeout);
        Assert.NotEqual(0, deadline.StartingTimestamp);
    }

    [Fact]
    public async Task ReplayClockAdvancesWallAndMonotonicTimeTogether()
    {
        DateTimeOffset start = DateTimeOffset.UnixEpoch.AddDays(1);
        var clock = new ReplayRuntimeClock(start);
        ValueTask wait = clock.DelayAsync(TimeSpan.FromSeconds(3));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(wait.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        await wait;
        Assert.Equal(start.AddSeconds(3), clock.UtcNow);
        Assert.Equal(TimeSpan.FromSeconds(3), clock.GetElapsedTime(0, clock.GetTimestamp()));
    }
}

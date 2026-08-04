namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Production runtime clock backed by an injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class TimeProviderRuntimeClock : IRuntimeClock
{
    public TimeProviderRuntimeClock(TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeProvider TimeProvider { get; }

    public DateTimeOffset UtcNow => TimeProvider.GetUtcNow();

    public long GetTimestamp() => TimeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Runtime delays cannot be negative except Timeout.InfiniteTimeSpan.");
        }

        return new ValueTask(Task.Delay(delay, TimeProvider, cancellationToken));
    }
}

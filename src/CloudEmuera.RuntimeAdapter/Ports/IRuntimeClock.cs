namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Runtime time boundary. <see cref="UtcNow"/> is wall-clock time; timestamps
/// and elapsed calculations are monotonic and must drive timeout decisions.
/// </summary>
public interface IRuntimeClock
{
    DateTimeOffset UtcNow { get; }

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

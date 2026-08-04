namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Small monotonic deadline helper used by runtime callers that need a timeout
/// without coupling the interpreter to wall-clock arithmetic.
/// </summary>
public readonly struct RuntimeDeadline
{
    private readonly IRuntimeClock clock;
    private readonly long startingTimestamp;
    private readonly TimeSpan timeout;

    private RuntimeDeadline(IRuntimeClock clock, long startingTimestamp, TimeSpan timeout)
    {
        this.clock = clock;
        this.startingTimestamp = startingTimestamp;
        this.timeout = timeout;
    }

    public long StartingTimestamp => startingTimestamp;

    public TimeSpan Timeout => timeout;

    public bool IsExpired => clock is null || Remaining == TimeSpan.Zero;

    public static RuntimeDeadline After(IRuntimeClock clock, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        // Keep the timestamp and timeout in their native representations. A
        // single clock tick is not guaranteed to survive conversion to a
        // TimeSpan (notably on Linux), so inferring a timestamp unit from
        // GetElapsedTime(start, start + 1) is not valid.
        return new RuntimeDeadline(clock, clock.GetTimestamp(), delay);
    }

    public TimeSpan Remaining
    {
        get
        {
            if (clock is null)
            {
                return TimeSpan.Zero;
            }

            TimeSpan elapsed = clock.GetElapsedTime(startingTimestamp, clock.GetTimestamp());
            if (elapsed <= TimeSpan.Zero)
            {
                return timeout;
            }

            return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
        }
    }

    public ValueTask DelayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan remaining = Remaining;
        return remaining <= TimeSpan.Zero
            ? ValueTask.CompletedTask
            : clock.DelayAsync(remaining, cancellationToken);
    }
}

/// <summary>
/// Convenience timeout operation. The clock controls the wait, making the
/// operation deterministic when a test clock is injected.
/// </summary>
public static class RuntimeTimeout
{
    public static ValueTask WaitAsync(
        IRuntimeClock clock,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return clock.DelayAsync(timeout, cancellationToken);
    }
}

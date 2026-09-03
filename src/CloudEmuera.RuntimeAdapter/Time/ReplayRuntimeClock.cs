namespace CloudEmuera.RuntimeAdapter;

/// <summary>Debugger-controlled clock used only by a replay Worker.</summary>
public sealed class ReplayRuntimeClock(DateTimeOffset initialUtcNow) : IRuntimeClock
{
    private readonly object sync = new();
    private readonly List<Waiter> waiters = [];
    private long timestamp;
    private DateTimeOffset utcNow = initialUtcNow;
    private long order;

    public DateTimeOffset UtcNow { get { lock (sync) return utcNow; } }

    public long GetTimestamp() { lock (sync) return timestamp; }

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(delay));
        cancellationToken.ThrowIfCancellationRequested();
        if (delay == TimeSpan.Zero) return ValueTask.CompletedTask;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Waiter waiter;
        lock (sync)
        {
            long? deadline = delay == Timeout.InfiniteTimeSpan ? null : checked(timestamp + delay.Ticks);
            waiter = new Waiter(deadline, order++, completion);
            waiters.Add(waiter);
        }
        waiter.Registration = cancellationToken.Register(() => Cancel(waiter));
        return new ValueTask(completion.Task);
    }

    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
        List<Waiter> completed;
        lock (sync)
        {
            timestamp = checked(timestamp + amount.Ticks);
            utcNow = utcNow.Add(amount);
            completed = waiters.Where(value => value.Deadline is not null && value.Deadline <= timestamp)
                .OrderBy(value => value.Deadline).ThenBy(value => value.Order).ToList();
            foreach (Waiter waiter in completed) waiters.Remove(waiter);
        }
        foreach (Waiter waiter in completed)
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetResult(true);
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (sync) waiters.Remove(waiter);
        waiter.Completion.TrySetCanceled();
    }

    private sealed class Waiter(long? deadline, long order, TaskCompletionSource<bool> completion)
    {
        public long? Deadline { get; } = deadline;
        public long Order { get; } = order;
        public TaskCompletionSource<bool> Completion { get; } = completion;
        public CancellationTokenRegistration Registration { get; set; }
    }
}

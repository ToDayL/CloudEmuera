using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.RuntimeAdapter.Tests.Time;

internal sealed class ManualRuntimeClock : IRuntimeClock
{
    private readonly object sync = new();
    private readonly List<Waiter> waiters = [];
    private long timestamp;
    private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;
    private long registrationOrder;

    public IReadOnlyList<long> LastCompletedOrders { get; private set; } = Array.Empty<long>();

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (sync)
            {
                return utcNow;
            }
        }
    }

    public long GetTimestamp()
    {
        lock (sync)
        {
            return timestamp;
        }
    }

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (delay == TimeSpan.Zero)
        {
            return ValueTask.CompletedTask;
        }

        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new Waiter(
            delay == Timeout.InfiniteTimeSpan ? null : checked(timestamp + delay.Ticks),
            registrationOrder++,
            source);
        CancellationTokenRegistration registration = default;
        registration = cancellationToken.Register(() => Cancel(waiter, registration));
        waiter.Registration = registration;
        lock (sync)
        {
            if (!source.Task.IsCompleted)
            {
                waiters.Add(waiter);
            }
        }

        return new ValueTask(source.Task);
    }

    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

        List<Waiter> completed;
        lock (sync)
        {
            timestamp = checked(timestamp + amount.Ticks);
            utcNow = utcNow.Add(amount);
            completed = waiters
                .Where(waiter => waiter.Deadline is not null && waiter.Deadline <= timestamp)
                .OrderBy(waiter => waiter.Deadline)
                .ThenBy(waiter => waiter.Order)
                .ToList();
            foreach (Waiter waiter in completed)
            {
                waiters.Remove(waiter);
            }

            LastCompletedOrders = completed.Select(waiter => waiter.Order).ToArray();
        }

        foreach (Waiter waiter in completed)
        {
            waiter.Registration.Dispose();
            waiter.Source.TrySetResult(true);
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (sync)
        {
            utcNow = value;
        }
    }

    private void Cancel(Waiter waiter, CancellationTokenRegistration registration)
    {
        lock (sync)
        {
            waiters.Remove(waiter);
        }

        registration.Dispose();
        waiter.Source.TrySetCanceled();
    }

    private sealed class Waiter(long? deadline, long order, TaskCompletionSource<bool> source)
    {
        public long? Deadline { get; } = deadline;
        public long Order { get; } = order;
        public TaskCompletionSource<bool> Source { get; } = source;
        public CancellationTokenRegistration Registration { get; set; }
    }
}

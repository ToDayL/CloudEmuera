namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleResumeResultKind
{
    UpToDate,
    DeltaBatch,
    SnapshotWithDeltas
}

public abstract class ConsoleResumeResult
{
    private protected ConsoleResumeResult(ConsoleResumeResultKind kind, long currentSequence)
    {
        Kind = kind;
        CurrentSequence = currentSequence;
    }

    public ConsoleResumeResultKind Kind { get; }

    public ConsoleResumeResultKind ResultKind => Kind;

    public long CurrentSequence { get; }
}

public sealed class ConsoleUpToDateResult : ConsoleResumeResult
{
    public ConsoleUpToDateResult(long currentSequence)
        : base(ConsoleResumeResultKind.UpToDate, currentSequence)
    {
    }
}

public sealed class ConsoleDeltaBatchResult : ConsoleResumeResult
{
    public ConsoleDeltaBatchResult(
        long fromExclusive,
        long toInclusive,
        IEnumerable<SequencedConsoleEvent> events)
        : base(ConsoleResumeResultKind.DeltaBatch, toInclusive)
    {
        if (fromExclusive < 0 || toInclusive < fromExclusive)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "The delta cursor range is invalid.");
        }

        ArgumentNullException.ThrowIfNull(events);
        SequencedConsoleEvent[] copy = events.ToArray();
        if (copy.Any(item => item is null))
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.NullValue, "A delta event is required.");
        }

        FromExclusive = fromExclusive;
        ToInclusive = toInclusive;
        Events = Array.AsReadOnly(copy);
    }

    public long FromExclusive { get; }

    public long ToInclusive { get; }

    public IReadOnlyList<SequencedConsoleEvent> Events { get; }
}

public sealed class ConsoleSnapshotWithDeltasResult : ConsoleResumeResult
{
    public ConsoleSnapshotWithDeltasResult(
        ConsoleSnapshot snapshot,
        IEnumerable<SequencedConsoleEvent> eventsAfterSnapshot,
        long currentSequence)
        : base(ConsoleResumeResultKind.SnapshotWithDeltas, currentSequence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(eventsAfterSnapshot);
        SequencedConsoleEvent[] copy = eventsAfterSnapshot.ToArray();
        if (copy.Any(item => item is null || item.Sequence <= snapshot.SnapshotSequence))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidCursor,
                "Snapshot delta events must be strictly after the snapshot sequence.");
        }

        Snapshot = snapshot;
        EventsAfterSnapshot = Array.AsReadOnly(copy);
    }

    public ConsoleSnapshot Snapshot { get; }

    public IReadOnlyList<SequencedConsoleEvent> EventsAfterSnapshot { get; }

    public IReadOnlyList<SequencedConsoleEvent> Events => EventsAfterSnapshot;
}

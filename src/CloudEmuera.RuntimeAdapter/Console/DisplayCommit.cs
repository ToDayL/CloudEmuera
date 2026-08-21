namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// The only runtime-owned boundaries at which a console state may become
/// visible to a browser. Transport and history limits must never create a
/// display commit implicitly.
/// </summary>
public enum DisplayCommitReason
{
    WaitingForInput,
    RuntimeCompleted,
    RuntimeFailed,
    ExplicitRefresh
}

/// <summary>
/// Immutable description of the most recent complete display frame.
/// </summary>
public sealed record DisplayCommit
{
    public DisplayCommit(
        long frameId,
        long commitSequence,
        DisplayCommitReason reason,
        bool requiresSnapshot,
        ConsoleSnapshot snapshot,
        IEnumerable<SequencedConsoleTransaction>? transactions = null)
    {
        if (frameId <= 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "A display frame id must be positive.");
        if (commitSequence < 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "A display commit sequence cannot be negative.");
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SnapshotSequence != commitSequence)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "The display snapshot and commit sequence must match.");
        if (reason == DisplayCommitReason.WaitingForInput && snapshot.CurrentPrompt is null)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "A waiting-for-input display frame requires an active prompt.");
        if (reason is DisplayCommitReason.RuntimeCompleted or DisplayCommitReason.RuntimeFailed && snapshot.CurrentPrompt is not null)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "A terminal display frame cannot contain an active prompt.");

        SequencedConsoleTransaction[] copy = (transactions ?? Array.Empty<SequencedConsoleTransaction>()).ToArray();
        if (copy.Any(item => item is null))
            throw new ConsoleContractException(ConsoleContractViolationReason.NullValue, "A display frame transaction is required.");
        if (requiresSnapshot && copy.Length != 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "A snapshot display frame cannot also carry a delta representation.");
        if (!requiresSnapshot && copy.Length == 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "A delta display frame requires at least one transaction.");
        for (int index = 1; index < copy.Length; index++)
        {
            if (copy[index].Sequence != copy[index - 1].Sequence + 1)
                throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "Display frame transactions must be continuous.");
        }
        if (copy.Length != 0 && copy[^1].Sequence != commitSequence)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "The display frame transaction tail must equal its commit sequence.");
        if (reason == DisplayCommitReason.WaitingForInput && !requiresSnapshot &&
            (copy.Length == 0 || copy[^1].Transaction.Operations.Count == 0 ||
             copy[^1].Transaction.Operations[^1] is not OpenPromptOperation))
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "A waiting-for-input delta must end with OpenPrompt.");
        }

        FrameId = frameId;
        CommitSequence = commitSequence;
        Reason = reason;
        RequiresSnapshot = requiresSnapshot;
        Snapshot = snapshot;
        Transactions = Array.AsReadOnly(copy);
    }

    public long FrameId { get; }

    public long CommitSequence { get; }

    public DisplayCommitReason Reason { get; }

    public bool RequiresSnapshot { get; }

    /// <summary>
    /// Complete state at the commit boundary. It is retained by the Worker
    /// even when the wire representation is a delta frame.
    /// </summary>
    public ConsoleSnapshot Snapshot { get; }

    /// <summary>Transactions from the preceding committed frame, when safe.</summary>
    public IReadOnlyList<SequencedConsoleTransaction> Transactions { get; }
}

public enum DisplayCommitReadKind
{
    UpToDate,
    DeltaFrame,
    Snapshot
}

public sealed record DisplayCommitReadResult(
    DisplayCommitReadKind Kind,
    long CurrentFrameId,
    long CurrentSequence,
    DisplayCommit? Commit = null);

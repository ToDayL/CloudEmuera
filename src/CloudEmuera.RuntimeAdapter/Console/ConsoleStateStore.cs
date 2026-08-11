namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Synchronously reduced, bounded console state. State mutation, sequence
/// allocation and replay-history publication share one lock.
/// </summary>
public sealed class ConsoleStateStore
{
    private readonly object sync = new();
    private readonly ConsoleHistoryOptions options;
    private readonly ConsoleContractLimits limits;
    private readonly List<ConsoleNode> visibleNodes = [];
    private readonly List<SequencedConsoleEvent> history = [];
    private ConsolePrompt? currentPrompt;
    private long currentSequence;
    private long historyEstimatedBytes;
    private bool wasTruncated;
    private long droppedNodeCount;
    private ConsoleSnapshot baselineSnapshot = ConsoleSnapshot.Empty;
    private bool sequenceInitialized;

    public ConsoleStateStore()
        : this(ConsoleHistoryOptions.Default)
    {
    }

    public ConsoleStateStore(ConsoleHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        limits = options.ContractLimits;
    }

    public ConsoleStateStore(ConsoleContractLimits limits, ConsoleHistoryOptions? options = null)
        : this((options ?? ConsoleHistoryOptions.Default) with { ContractLimits = limits ?? throw new ArgumentNullException(nameof(limits)) })
    {
    }

    public ConsoleHistoryOptions Options => options;

    public long CurrentSequence
    {
        get
        {
            lock (sync)
            {
                return currentSequence;
            }
        }
    }

    public long SnapshotSequence
    {
        get
        {
            lock (sync)
            {
                return baselineSnapshot.SnapshotSequence;
            }
        }
    }

    public long HistoryEstimatedBytes
    {
        get
        {
            lock (sync)
            {
                return historyEstimatedBytes;
            }
        }
    }

    public ConsolePrompt? CurrentPrompt
    {
        get
        {
            lock (sync)
            {
                return currentPrompt;
            }
        }
    }

    public ConsoleSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return CreateSnapshot(currentSequence, visibleNodes, currentPrompt);
            }
        }
    }

    public ConsoleSnapshot CurrentSnapshot => Snapshot;

    public ConsoleSnapshot GetSnapshot() => Snapshot;

    public ConsoleSnapshot BaselineSnapshot
    {
        get
        {
            lock (sync)
            {
                return baselineSnapshot;
            }
        }
    }

    public IReadOnlyList<SequencedConsoleEvent> History
    {
        get
        {
            lock (sync)
            {
                return Array.AsReadOnly(history.ToArray());
            }
        }
    }

    /// <summary>
    /// Starts a fresh runtime's console sequence after a persisted Session
    /// sequence. This must be called before the first operation; it preserves
    /// Session-level monotonicity across a cold reopen without replaying the
    /// previous runtime's history.
    /// </summary>
    public void InitializeSequence(long initialSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialSequence);
        ArgumentOutOfRangeException.ThrowIfEqual(initialSequence, long.MaxValue);

        lock (sync)
        {
            if (sequenceInitialized || currentSequence != 0 || history.Count != 0 || visibleNodes.Count != 0 || currentPrompt is not null)
                throw new InvalidOperationException("The console sequence must be initialized before the first operation.");

            currentSequence = initialSequence;
            baselineSnapshot = CreateSnapshot(initialSequence, visibleNodes, currentPrompt);
            sequenceInitialized = true;
        }
    }

    public SequencedConsoleEvent Apply(ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (sync)
        {
            long operationEstimatedBytes = ConsoleSizeEstimator.MeasureOperation(operation);
            if (operationEstimatedBytes > options.MaxEstimatedBytes)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.NodeExceedsHistoryBudget,
                    "A single console operation exceeds the estimated history budget.");
            }

            PreparedMutation mutation = PrepareMutation(operation);
            long nextSequence = NextSequence();
            var sequenced = new SequencedConsoleEvent(nextSequence, operation);

            visibleNodes.Clear();
            visibleNodes.AddRange(mutation.VisibleNodes);
            currentPrompt = mutation.CurrentPrompt;
            wasTruncated = mutation.WasTruncated;
            droppedNodeCount = mutation.DroppedNodeCount;
            currentSequence = nextSequence;

            history.Add(sequenced);
            historyEstimatedBytes = checked(historyEstimatedBytes + operationEstimatedBytes);

            if (operation is ClearConsoleOperation ||
                mutation.DroppedThisOperation ||
                history.Count > options.MaxDeltaCount ||
                historyEstimatedBytes > options.MaxEstimatedBytes)
            {
                baselineSnapshot = CreateSnapshot(currentSequence, visibleNodes, currentPrompt);
                history.Clear();
                historyEstimatedBytes = 0;
            }

            return sequenced;
        }
    }

    public ConsoleResumeResult ReadSince(long lastSequence)
    {
        lock (sync)
        {
            if (lastSequence < 0 || lastSequence > currentSequence)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidCursor,
                    "The console cursor is outside the current sequence range.",
                    nameof(lastSequence));
            }

            if (lastSequence == currentSequence)
            {
                return new ConsoleUpToDateResult(currentSequence);
            }

            bool mustUseSnapshot = lastSequence == 0 || lastSequence < baselineSnapshot.SnapshotSequence;
            if (!mustUseSnapshot && history.Count != 0)
            {
                long firstSequence = history[0].Sequence;
                long lastHistorySequence = history[^1].Sequence;
                bool isContinuous = lastHistorySequence == currentSequence &&
                    lastSequence >= firstSequence - 1;
                if (isContinuous)
                {
                    SequencedConsoleEvent[] events = history
                        .Where(item => item.Sequence > lastSequence)
                        .ToArray();
                    return new ConsoleDeltaBatchResult(lastSequence, currentSequence, events);
                }
            }

            return new ConsoleSnapshotWithDeltasResult(
                baselineSnapshot,
                history,
                currentSequence);
        }
    }

    public SequencedConsoleEvent ApplyOperation(ConsoleOperation operation) => Apply(operation);

    private PreparedMutation PrepareMutation(ConsoleOperation operation)
    {
        switch (operation)
        {
            case AppendNodesOperation append:
            {
                ConsoleNodeValidation.ValidateBatch(append.Nodes, limits);
                var candidate = new List<ConsoleNode>(visibleNodes.Count + append.Nodes.Count);
                candidate.AddRange(visibleNodes);
                candidate.AddRange(append.Nodes);
                VisibleFit fit = FitVisibleNodes(candidate, currentPrompt);
                return new PreparedMutation(fit.Nodes, currentPrompt, wasTruncated || fit.DroppedCount != 0, checked(droppedNodeCount + fit.DroppedCount), fit.DroppedCount != 0);
            }
            case ClearConsoleOperation:
                return new PreparedMutation([], currentPrompt, wasTruncated, droppedNodeCount, DroppedThisOperation: false);
            case OpenPromptOperation open:
            {
                if (currentPrompt is not null)
                {
                    throw new ConsoleContractException(
                        ConsoleContractViolationReason.PromptAlreadyActive,
                        "A console prompt is already active.");
                }

                open.Prompt.Validate(limits);
                VisibleFit fit = FitVisibleNodes(visibleNodes, open.Prompt);
                return new PreparedMutation(fit.Nodes, open.Prompt, wasTruncated || fit.DroppedCount != 0, checked(droppedNodeCount + fit.DroppedCount), fit.DroppedCount != 0);
            }
            case ClosePromptOperation close:
                if (currentPrompt is null)
                {
                    throw new ConsoleContractException(
                        ConsoleContractViolationReason.PromptAlreadyCompleted,
                        "There is no active console prompt.");
                }

                if (!string.Equals(currentPrompt.PromptId, close.PromptId, StringComparison.Ordinal))
                {
                    throw new ConsoleContractException(
                        ConsoleContractViolationReason.PromptIdMismatch,
                        "The close operation does not match the active prompt.");
                }

                // Copy before Apply clears the mutable backing list. Returning
                // visibleNodes itself would erase all output that preceded an
                // accepted/cancelled prompt while reducing ClosePrompt.
                return new PreparedMutation(visibleNodes.ToArray(), null, wasTruncated, droppedNodeCount, DroppedThisOperation: false);
            default:
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidNodeType,
                    "The console operation type is not part of the contract.");
        }
    }

    private VisibleFit FitVisibleNodes(IEnumerable<ConsoleNode> source, ConsolePrompt? prompt)
    {
        var candidate = source.ToList();
        ConsoleNodeMetrics metrics = ConsoleSizeEstimator.MeasureNodes(candidate);
        foreach (ConsoleNode node in candidate)
        {
            ConsoleNodeMetrics nodeMetrics = ConsoleSizeEstimator.MeasureNode(node);
            if (nodeMetrics.NodeCount > options.MaxVisibleNodes || nodeMetrics.TextLength > options.MaxVisibleTextLength)
            {
                throw new ConsoleContractException(
                    nodeMetrics.TextLength > options.MaxVisibleTextLength
                        ? ConsoleContractViolationReason.NodeExceedsVisibleTextBudget
                        : ConsoleContractViolationReason.NodeExceedsHistoryBudget,
                    "A single console node exceeds the visible state budget.");
            }

            if (ConsoleSizeEstimator.MeasureSnapshot(nodeMetrics, prompt) > options.MaxEstimatedBytes)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.NodeExceedsHistoryBudget,
                    "A single console node exceeds the estimated state budget.");
            }
        }

        long promptBytes = ConsoleSizeEstimator.MeasurePrompt(prompt);
        if (checked(128L + promptBytes) > options.MaxEstimatedBytes)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.NodeExceedsHistoryBudget,
                "The active prompt exceeds the estimated state budget.");
        }

        long dropped = 0;
        while (candidate.Count != 0 &&
            (metrics.NodeCount > options.MaxVisibleNodes ||
             metrics.TextLength > options.MaxVisibleTextLength ||
             checked(128L + metrics.EstimatedBytes + promptBytes) > options.MaxEstimatedBytes))
        {
            ConsoleNode removed = candidate[0];
            candidate.RemoveAt(0);
            metrics -= ConsoleSizeEstimator.MeasureNode(removed);
            dropped = checked(dropped + 1);
        }

        if (metrics.NodeCount > options.MaxVisibleNodes ||
            metrics.TextLength > options.MaxVisibleTextLength ||
            checked(128L + metrics.EstimatedBytes + promptBytes) > options.MaxEstimatedBytes)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.NodeExceedsHistoryBudget,
                "The visible console state cannot fit within its configured limits.");
        }

        return new VisibleFit(candidate, dropped);
    }

    private long NextSequence()
    {
        if (currentSequence == long.MaxValue)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.SequenceExhausted,
                "The console sequence is exhausted and cannot wrap.");
        }

        return checked(currentSequence + 1);
    }

    private ConsoleSnapshot CreateSnapshot(long sequence, IEnumerable<ConsoleNode> nodes, ConsolePrompt? prompt)
    {
        return new ConsoleSnapshot(sequence, nodes, prompt, wasTruncated, droppedNodeCount);
    }

    private sealed record PreparedMutation(
        IReadOnlyList<ConsoleNode> VisibleNodes,
        ConsolePrompt? CurrentPrompt,
        bool WasTruncated,
        long DroppedNodeCount,
        bool DroppedThisOperation);

    private sealed record VisibleFit(IReadOnlyList<ConsoleNode> Nodes, long DroppedCount);
}

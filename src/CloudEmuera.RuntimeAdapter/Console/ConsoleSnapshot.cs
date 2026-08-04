namespace CloudEmuera.RuntimeAdapter;

public sealed record ConsoleTruncationMetadata(bool WasTruncated, long DroppedNodeCount);

public sealed class ConsoleSnapshot
{
    public ConsoleSnapshot(
        long snapshotSequence,
        IEnumerable<ConsoleNode> visibleNodes,
        ConsolePrompt? currentPrompt = null,
        bool wasTruncated = false,
        long droppedNodeCount = 0)
    {
        if (snapshotSequence < 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(snapshotSequence);
        }

        ArgumentNullException.ThrowIfNull(visibleNodes);
        ConsoleNode[] copy = visibleNodes.ToArray();
        ConsoleContractLimits.Default.Validate();
        ConsoleNodeValidation.ValidateBatchIfNotEmpty(copy, ConsoleContractLimits.Default);
        if (currentPrompt is not null)
        {
            currentPrompt.Validate(ConsoleContractLimits.Default);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(droppedNodeCount);

        SnapshotSequence = snapshotSequence;
        VisibleNodes = Array.AsReadOnly(copy);
        VisibleLines = BuildLines(copy);
        CurrentPrompt = currentPrompt;
        WasTruncated = wasTruncated;
        DroppedNodeCount = droppedNodeCount;
        ConsoleNodeMetrics metrics = ConsoleSizeEstimator.MeasureNodes(copy);
        VisibleNodeCount = metrics.NodeCount;
        VisibleTextLength = metrics.TextLength;
        EstimatedBytes = ConsoleSizeEstimator.MeasureSnapshot(metrics, currentPrompt);
    }

    public static ConsoleSnapshot Empty { get; } = new(0, Array.Empty<ConsoleNode>());

    public long SnapshotSequence { get; }

    public long Sequence => SnapshotSequence;

    public IReadOnlyList<ConsoleNode> VisibleNodes { get; }

    public IReadOnlyList<IReadOnlyList<ConsoleNode>> VisibleLines { get; }

    public ConsolePrompt? CurrentPrompt { get; }

    public bool WasTruncated { get; }

    public long DroppedNodeCount { get; }

    public ConsoleTruncationMetadata Truncation => new(WasTruncated, DroppedNodeCount);

    public int VisibleNodeCount { get; }

    public long VisibleTextLength { get; }

    public long EstimatedBytes { get; }

    internal static ConsoleSnapshot Create(
        long sequence,
        IReadOnlyList<ConsoleNode> nodes,
        ConsolePrompt? prompt,
        bool wasTruncated,
        long droppedNodeCount,
        ConsoleNodeMetrics metrics)
    {
        // The public constructor recomputes these values. Metrics are kept in
        // the store for limit decisions, not trusted as public state.
        return new ConsoleSnapshot(sequence, nodes, prompt, wasTruncated, droppedNodeCount);
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<IReadOnlyList<ConsoleNode>> BuildLines(IReadOnlyList<ConsoleNode> nodes)
    {
        var lines = new List<IReadOnlyList<ConsoleNode>>();
        var current = new List<ConsoleNode>();
        foreach (ConsoleNode node in nodes)
        {
            if (node is LineBreakNode)
            {
                lines.Add(Array.AsReadOnly(current.ToArray()));
                current.Clear();
            }
            else
            {
                current.Add(node);
            }
        }

        if (current.Count != 0)
        {
            lines.Add(Array.AsReadOnly(current.ToArray()));
        }

        return Array.AsReadOnly(lines.ToArray());
    }
}

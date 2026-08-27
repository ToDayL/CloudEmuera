namespace CloudEmuera.RuntimeAdapter;

public sealed record ConsoleTruncationMetadata(
    bool WasTruncated,
    long DroppedNodeCount,
    long DroppedLineCount = 0,
    long DroppedTextLength = 0);

public abstract record StructuredConsoleResumeResult;

public sealed record StructuredConsoleUpToDateResult(long CurrentSequence) : StructuredConsoleResumeResult;

public sealed record StructuredConsoleDeltaBatchResult(
    long FromSequence,
    long ToSequence,
    IReadOnlyList<SequencedConsoleTransaction> Transactions) : StructuredConsoleResumeResult;

public sealed record StructuredConsoleSnapshotWithDeltasResult(
    ConsoleSnapshot Snapshot,
    IReadOnlyList<SequencedConsoleTransaction> TransactionsAfterSnapshot,
    long CurrentSequence) : StructuredConsoleResumeResult;

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
        UsesLegacyNodeModel = true;
        VisibleNodes = Array.AsReadOnly(copy);
        VisibleLines = BuildLines(copy);
        Scrollback = BuildLegacyScrollback(copy);
        BackgroundLayers = Array.Empty<BackgroundLayer>();
        CanvasScene = new CanvasScene();
        MediaState = new MediaState();
        WindowMetadata = new WindowMetadata();
        TooltipPresentation = new ConsoleTooltipPresentation();
        TooltipResources = Array.Empty<ConsoleTooltipResource>();
        CurrentPrompt = currentPrompt;
        WasTruncated = wasTruncated;
        DroppedNodeCount = droppedNodeCount;
        Truncation = new ConsoleTruncationMetadata(wasTruncated, droppedNodeCount);
        ConsoleNodeMetrics metrics = ConsoleSizeEstimator.MeasureNodes(copy);
        VisibleNodeCount = metrics.NodeCount;
        VisibleTextLength = metrics.TextLength;
        EstimatedBytes = ConsoleSizeEstimator.MeasureSnapshot(metrics, currentPrompt);
    }

    public ConsoleSnapshot(
        long snapshotSequence,
        IEnumerable<ConsoleLine> scrollback,
        IEnumerable<BackgroundLayer>? backgroundLayers = null,
        CanvasScene? canvasScene = null,
        MediaState? mediaState = null,
        ConsolePrompt? currentPrompt = null,
        WindowMetadata? windowMetadata = null,
        ConsoleTruncationMetadata? truncation = null,
        ConsoleTooltipPresentation? tooltipPresentation = null,
        IEnumerable<ConsoleTooltipResource>? tooltipResources = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotSequence);
        ArgumentNullException.ThrowIfNull(scrollback);
        ConsoleLine[] lines = scrollback.ToArray();
        ConsoleContractLimits.Default.Validate();
        if (lines.Length > ConsoleContractLimits.Default.MaxScrollbackLines)
            throw new ConsoleContractException(ConsoleContractViolationReason.LineTooLarge, "The scrollback exceeds its line limit.");
        if (lines.SelectMany(line => line.Nodes).Count() > ConsoleContractLimits.Default.MaxScrollbackNodes)
            throw new ConsoleContractException(ConsoleContractViolationReason.NodeExceedsHistoryBudget, "The scrollback exceeds its node limit.");
        if (currentPrompt is not null)
            currentPrompt.Validate(ConsoleContractLimits.Default);
        ConsoleTruncationMetadata metadata = truncation ?? new ConsoleTruncationMetadata(false, 0);
        ConsoleTooltipResource[] resources = (tooltipResources ?? Array.Empty<ConsoleTooltipResource>()).ToArray();
        if (resources.Length > ConsoleContractLimits.Default.MaxTooltipResources ||
            resources.Sum(resource => (long)resource.PngData.Count) > ConsoleContractLimits.Default.MaxTooltipResourcesBytes ||
            resources.Select(resource => resource.GraphicsId).Distinct().Count() != resources.Length)
            throw new ConsoleContractException(ConsoleContractViolationReason.TooltipResourceLimitExceeded, "The tooltip resources exceed their bounded collection contract.");
        if (metadata.DroppedNodeCount < 0 || metadata.DroppedLineCount < 0 || metadata.DroppedTextLength < 0)
            throw new ArgumentOutOfRangeException(nameof(truncation));

        SnapshotSequence = snapshotSequence;
        UsesLegacyNodeModel = false;
        Scrollback = Array.AsReadOnly(lines);
        BackgroundLayers = Array.AsReadOnly((backgroundLayers ?? Array.Empty<BackgroundLayer>()).ToArray());
        CanvasScene = canvasScene ?? new CanvasScene();
        MediaState = mediaState ?? new MediaState();
        WindowMetadata = windowMetadata ?? new WindowMetadata();
        TooltipPresentation = tooltipPresentation ?? new ConsoleTooltipPresentation();
        TooltipResources = Array.AsReadOnly(resources.OrderBy(resource => resource.GraphicsId).ToArray());
        CurrentPrompt = currentPrompt;
        WasTruncated = metadata.WasTruncated;
        DroppedNodeCount = metadata.DroppedNodeCount;
        Truncation = metadata;
        VisibleNodes = Array.AsReadOnly(FlattenLines(lines));
        VisibleLines = Array.AsReadOnly(lines.Select(line => line.Nodes).ToArray());
        ConsoleNodeMetrics metrics = ConsoleSizeEstimator.MeasureNodes(VisibleNodes);
        VisibleNodeCount = metrics.NodeCount;
        VisibleTextLength = metrics.TextLength;
        EstimatedBytes = ConsoleSizeEstimator.MeasureStructuredSnapshot(this, metrics);
    }

    public static ConsoleSnapshot Empty { get; } = new(0, Array.Empty<ConsoleNode>());

    public long SnapshotSequence { get; }

    internal bool UsesLegacyNodeModel { get; }

    public long Sequence => SnapshotSequence;

    public IReadOnlyList<ConsoleNode> VisibleNodes { get; }

    public IReadOnlyList<IReadOnlyList<ConsoleNode>> VisibleLines { get; }

    public IReadOnlyList<ConsoleLine> Scrollback { get; }

    public IReadOnlyList<BackgroundLayer> BackgroundLayers { get; }

    public CanvasScene CanvasScene { get; }

    public MediaState MediaState { get; }

    public WindowMetadata WindowMetadata { get; }

    public ConsoleTooltipPresentation TooltipPresentation { get; }

    public IReadOnlyList<ConsoleTooltipResource> TooltipResources { get; }

    public ConsolePrompt? CurrentPrompt { get; }

    public bool WasTruncated { get; }

    public long DroppedNodeCount { get; }

    public ConsoleTruncationMetadata Truncation { get; }

    public int VisibleNodeCount { get; }

    public long VisibleTextLength { get; }

    public long EstimatedBytes { get; }

    /// <summary>
    /// Validates this immutable snapshot against the supplied deployment
    /// limits. Wire readers must call this overload instead of relying only on
    /// the default constructor limits.
    /// </summary>
    public void Validate(ConsoleHistoryOptions options) => ConsoleSnapshotValidation.Validate(this, options);

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

    private static System.Collections.ObjectModel.ReadOnlyCollection<ConsoleLine> BuildLegacyScrollback(IReadOnlyList<ConsoleNode> nodes)
    {
        var lines = new List<ConsoleLine>();
        var current = new List<ConsoleNode>();
        int lineNumber = 0;
        foreach (ConsoleNode node in nodes)
        {
            if (node is LineBreakNode)
            {
                lines.Add(new ConsoleLine($"legacy-{lineNumber++}", current));
                current.Clear();
            }
            else
            {
                current.Add(node);
            }
        }

        if (current.Count != 0 || lines.Count == 0)
            lines.Add(new ConsoleLine($"legacy-{lineNumber}", current));
        return Array.AsReadOnly(lines.ToArray());
    }

    private static ConsoleNode[] FlattenLines(IReadOnlyList<ConsoleLine> lines)
    {
        var nodes = new List<ConsoleNode>();
        foreach (ConsoleLine line in lines)
        {
            nodes.AddRange(line.Nodes);
            nodes.Add(LineBreakNode.Instance);
        }
        if (nodes.Count > 0 && nodes[^1] is LineBreakNode)
            nodes.RemoveAt(nodes.Count - 1);
        return nodes.ToArray();
    }
}

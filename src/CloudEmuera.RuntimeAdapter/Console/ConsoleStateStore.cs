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
    private bool structuredMode;
    private List<ConsoleLine> structuredScrollback = [];
    private Dictionary<string, BackgroundLayer> backgroundLayers = new(StringComparer.Ordinal);
    private Dictionary<string, CanvasDrawable> drawables = new(StringComparer.Ordinal);
    private Dictionary<string, HitRegion> hitRegions = new(StringComparer.Ordinal);
    private Dictionary<string, MediaChannelState> mediaChannels = new(StringComparer.Ordinal);
    private WindowMetadata windowMetadata = new();
    private readonly List<SequencedConsoleTransaction> transactionHistory = [];
    private readonly List<SequencedConsoleTransaction> pendingCommitTransactions = [];
    private long droppedLineCount;
    private long droppedTextLength;
    private long generatedLineId;
    private long pendingCommitEstimatedBytes;
    private bool requiresSnapshotAtCommit;
    private DisplayCommit? committedFrame;

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

    internal ConsoleStateStore(ConsoleSnapshot baseline, ConsoleHistoryOptions options)
        : this(options)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ConsoleSnapshotValidation.Validate(baseline, options);

        currentSequence = baseline.SnapshotSequence;
        baselineSnapshot = baseline;
        sequenceInitialized = true;
        structuredMode = true;
        // The legacy node-based snapshot constructor represents an empty
        // console with one synthetic `legacy-0` line. It is not a real
        // structured line and must not become visible when a snapshot is
        // replayed through the structured reducer.
        bool syntheticEmptyLine = baseline.UsesLegacyNodeModel &&
            baseline.VisibleNodes.Count == 0 &&
            baseline.Scrollback.Count == 1 &&
            baseline.Scrollback[0].Nodes.Count == 0;
        structuredScrollback = syntheticEmptyLine ? [] : baseline.Scrollback.ToList();
        backgroundLayers = baseline.BackgroundLayers.ToDictionary(item => item.LayerId, StringComparer.Ordinal);
        drawables = baseline.CanvasScene.Drawables.ToDictionary(item => item.DrawableId, StringComparer.Ordinal);
        hitRegions = baseline.CanvasScene.HitRegions.ToDictionary(item => item.RegionId, StringComparer.Ordinal);
        mediaChannels = baseline.MediaState.Channels.ToDictionary(item => item.Channel, StringComparer.Ordinal);
        windowMetadata = baseline.WindowMetadata;
        currentPrompt = baseline.CurrentPrompt;
        wasTruncated = baseline.Truncation.WasTruncated;
        droppedNodeCount = baseline.Truncation.DroppedNodeCount;
        droppedLineCount = baseline.Truncation.DroppedLineCount;
        droppedTextLength = baseline.Truncation.DroppedTextLength;
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
                return structuredMode
                    ? CreateStructuredSnapshot(currentSequence)
                    : CreateSnapshot(currentSequence, visibleNodes, currentPrompt);
            }
        }
    }

    public ConsoleSnapshot CurrentSnapshot => Snapshot;

    /// <summary>
    /// Latest runtime state. This is a working snapshot and must not be sent
    /// to a browser unless a <see cref="DisplayCommit"/> has promoted it.
    /// </summary>
    public ConsoleSnapshot WorkingSnapshot => Snapshot;

    /// <summary>Latest state explicitly committed for browser display.</summary>
    public ConsoleSnapshot? CommittedSnapshot
    {
        get
        {
            lock (sync)
            {
                return committedFrame?.Snapshot;
            }
        }
    }

    public long CommittedSequence
    {
        get
        {
            lock (sync)
            {
                return committedFrame?.CommitSequence ?? 0;
            }
        }
    }

    public long CommittedFrameId
    {
        get
        {
            lock (sync)
            {
                return committedFrame?.FrameId ?? 0;
            }
        }
    }

    public bool RequiresSnapshotAtCommit
    {
        get
        {
            lock (sync)
            {
                return requiresSnapshotAtCommit;
            }
        }
    }

    public DisplayCommit? CurrentDisplayCommit
    {
        get
        {
            lock (sync)
            {
                return committedFrame;
            }
        }
    }

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

    public bool IsStructuredMode
    {
        get
        {
            lock (sync)
            {
                return structuredMode;
            }
        }
    }

    public IReadOnlyList<SequencedConsoleTransaction> TransactionHistory
    {
        get
        {
            lock (sync)
            {
                return Array.AsReadOnly(transactionHistory.ToArray());
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

    /// <summary>
    /// Applies a structured transaction atomically. The candidate state is
    /// built entirely off to the side; sequence allocation and publication
    /// happen only after every operation and budget check succeeds.
    /// </summary>
    public SequencedConsoleTransaction ApplyTransaction(ConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        lock (sync)
        {
            long operationSequence = NextSequence();
            return ApplyStructuredTransaction(transaction, operationSequence, publishHistory: true);
        }
    }

    internal ConsoleSnapshot ApplyExternalTransaction(SequencedConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        lock (sync)
        {
            if (transaction.Sequence != NextSequence())
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidCursor,
                    "The external console transaction sequence is not the next sequence.",
                    nameof(transaction));
            }

            _ = ApplyStructuredTransaction(transaction.Transaction, transaction.Sequence, publishHistory: false);
            return CreateStructuredSnapshot(currentSequence);
        }
    }

    private SequencedConsoleTransaction ApplyStructuredTransaction(
        ConsoleTransaction transaction,
        long operationSequence,
        bool publishHistory)
    {
        ValidateTransactionLimits(transaction);
        ValidateDisplayCommitBoundary(transaction);
        EnsureStructuredState();
        var candidate = new StructuredCandidate(
            structuredScrollback.ToList(),
            new Dictionary<string, BackgroundLayer>(backgroundLayers, StringComparer.Ordinal),
            new Dictionary<string, CanvasDrawable>(drawables, StringComparer.Ordinal),
            new Dictionary<string, HitRegion>(hitRegions, StringComparer.Ordinal),
            new Dictionary<string, MediaChannelState>(mediaChannels, StringComparer.Ordinal),
            windowMetadata,
            currentPrompt,
            droppedLineCount,
            droppedNodeCount,
            droppedTextLength);
        candidate.WasTruncated = wasTruncated;

        foreach (ConsoleOperation operation in transaction.Operations)
            ApplyStructuredOperation(candidate, operation);

        FitStructuredCandidate(candidate);
        ConsoleSnapshot snapshot = CreateStructuredSnapshot(
            operationSequence,
            candidate.Scrollback,
            candidate.BackgroundLayers,
            candidate.Drawables,
            candidate.HitRegions,
            candidate.MediaChannels,
            candidate.WindowMetadata,
            candidate.CurrentPrompt,
            candidate.WasTruncated,
            candidate.DroppedNodeCount,
            candidate.DroppedLineCount,
            candidate.DroppedTextLength);
        ConsoleSnapshotValidation.Validate(snapshot, options);

        structuredScrollback = candidate.Scrollback;
        backgroundLayers = candidate.BackgroundLayers;
        drawables = candidate.Drawables;
        hitRegions = candidate.HitRegions;
        mediaChannels = candidate.MediaChannels;
        windowMetadata = candidate.WindowMetadata;
        currentPrompt = candidate.CurrentPrompt;
        visibleNodes.Clear();
        visibleNodes.AddRange(FlattenStructuredLines(structuredScrollback));
        wasTruncated = candidate.WasTruncated;
        droppedNodeCount = candidate.DroppedNodeCount;
        droppedLineCount = candidate.DroppedLineCount;
        droppedTextLength = candidate.DroppedTextLength;
        currentSequence = operationSequence;
        structuredMode = true;

        var sequenced = new SequencedConsoleTransaction(operationSequence, transaction);
        if (publishHistory)
        {
            transactionHistory.Add(sequenced);
            if (transactionHistory.Count > options.MaxDeltaCount)
            {
                baselineSnapshot = snapshot;
                transactionHistory.Clear();
            }

            RecordPendingCommitTransaction(sequenced);
            if (ContainsWaitingCommit(transaction))
                _ = CommitDisplayFrameLocked(DisplayCommitReason.WaitingForInput);
        }

        return sequenced;
    }

    public SequencedConsoleTransaction ApplyStructured(ConsoleTransaction transaction) => ApplyTransaction(transaction);

    /// <summary>
    /// Promotes the current working state to the browser-visible state. The
    /// caller uses this for terminal runtime boundaries; opening a prompt is
    /// committed automatically after its transaction succeeds.
    /// </summary>
    public DisplayCommit CommitDisplayFrame(DisplayCommitReason reason)
    {
        lock (sync)
        {
            EnsureStructuredState();
            return CommitDisplayFrameLocked(reason);
        }
    }

    /// <summary>
    /// Returns only the latest committed frame. If the caller missed a frame
    /// or the frame's bounded delta representation was compacted, the result
    /// is a complete committed snapshot instead of a working-state delta.
    /// </summary>
    public DisplayCommitReadResult ReadCommittedSince(long lastFrameId, long lastCommittedSequence)
    {
        lock (sync)
        {
            if (lastFrameId < 0 || lastCommittedSequence < 0 || lastCommittedSequence > currentSequence)
                throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "The committed display cursor is invalid.");

            if (committedFrame is null)
                return new DisplayCommitReadResult(DisplayCommitReadKind.UpToDate, 0, 0);

            DisplayCommit current = committedFrame;
            if (lastFrameId == current.FrameId && lastCommittedSequence == current.CommitSequence)
                return new DisplayCommitReadResult(DisplayCommitReadKind.UpToDate, current.FrameId, current.CommitSequence);
            if (lastFrameId > current.FrameId || lastCommittedSequence > current.CommitSequence)
                throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "The committed display cursor is ahead of the current frame.");

            bool canUseDelta = !current.RequiresSnapshot && current.Transactions.Count != 0 &&
                lastFrameId == current.FrameId - 1 &&
                lastCommittedSequence == current.Transactions[0].Sequence - 1;
            return new DisplayCommitReadResult(
                canUseDelta ? DisplayCommitReadKind.DeltaFrame : DisplayCommitReadKind.Snapshot,
                current.FrameId,
                current.CommitSequence,
                canUseDelta ? current : new DisplayCommit(
                    current.FrameId,
                    current.CommitSequence,
                    current.Reason,
                    requiresSnapshot: true,
                    snapshot: current.Snapshot));
        }
    }

    public ConsoleSnapshot StructuredSnapshot
    {
        get
        {
            lock (sync)
            {
                EnsureStructuredState();
                return CreateStructuredSnapshot(currentSequence);
            }
        }
    }

    private void EnsureStructuredState()
    {
        if (structuredMode)
            return;

        structuredScrollback = BuildStructuredLines(visibleNodes);
        backgroundLayers = new Dictionary<string, BackgroundLayer>(StringComparer.Ordinal);
        drawables = new Dictionary<string, CanvasDrawable>(StringComparer.Ordinal);
        hitRegions = new Dictionary<string, HitRegion>(StringComparer.Ordinal);
        mediaChannels = new Dictionary<string, MediaChannelState>(StringComparer.Ordinal);
        windowMetadata = new WindowMetadata();
        baselineSnapshot = CreateStructuredSnapshot(currentSequence);
        transactionHistory.Clear();
        pendingCommitTransactions.Clear();
        pendingCommitEstimatedBytes = 0;
        requiresSnapshotAtCommit = false;
        structuredMode = true;
    }

    private static void ValidateDisplayCommitBoundary(ConsoleTransaction transaction)
    {
        int promptCount = 0;
        for (int index = 0; index < transaction.Operations.Count; index++)
        {
            if (transaction.Operations[index] is not OpenPromptOperation)
                continue;
            promptCount++;
            if (promptCount > 1 || index != transaction.Operations.Count - 1)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidPrompt,
                    "OpenPrompt must be the final operation of a display-commit transaction.");
            }
        }
    }

    private static bool ContainsWaitingCommit(ConsoleTransaction transaction) =>
        transaction.Operations.Count != 0 && transaction.Operations[^1] is OpenPromptOperation;

    private void RecordPendingCommitTransaction(SequencedConsoleTransaction transaction, long? estimatedBytesOverride = null)
    {
        if (requiresSnapshotAtCommit)
            return;

        long estimatedBytes = estimatedBytesOverride ?? transaction.Transaction.Operations.Sum(ConsoleSizeEstimator.MeasureOperation);
        if (pendingCommitTransactions.Count >= options.MaxDeltaCount ||
            estimatedBytes > options.MaxEstimatedBytes ||
            pendingCommitEstimatedBytes > options.MaxEstimatedBytes - estimatedBytes)
        {
            pendingCommitTransactions.Clear();
            pendingCommitEstimatedBytes = 0;
            requiresSnapshotAtCommit = true;
            return;
        }

        pendingCommitTransactions.Add(transaction);
        pendingCommitEstimatedBytes = checked(pendingCommitEstimatedBytes + estimatedBytes);
    }

    private DisplayCommit CommitDisplayFrameLocked(DisplayCommitReason reason)
    {
        if (reason == DisplayCommitReason.WaitingForInput && currentPrompt is null)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "A waiting-for-input display commit requires an active prompt.");
        }
        if ((reason is DisplayCommitReason.RuntimeCompleted or DisplayCommitReason.RuntimeFailed) && currentPrompt is not null)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "A terminal display commit cannot contain an active prompt.");
        }
        if (committedFrame is not null && currentSequence < committedFrame.CommitSequence)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "The committed sequence cannot move backwards.");
        if (committedFrame is not null && currentSequence == committedFrame.CommitSequence &&
            pendingCommitTransactions.Count == 0 && !requiresSnapshotAtCommit && reason == committedFrame.Reason)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidCursor,
                "The same display state cannot be committed twice.");
        }
        if (committedFrame?.FrameId == long.MaxValue)
            throw new ConsoleContractException(ConsoleContractViolationReason.SequenceExhausted, "The display frame id is exhausted.");

        ConsoleSnapshot snapshot = structuredMode
            ? CreateStructuredSnapshot(currentSequence)
            : CreateSnapshot(currentSequence, visibleNodes, currentPrompt);
        bool requiresSnapshot = committedFrame is null || requiresSnapshotAtCommit || pendingCommitTransactions.Count == 0;
        if (!requiresSnapshot && committedFrame is not null &&
            pendingCommitTransactions[0].Sequence != committedFrame.CommitSequence + 1)
            requiresSnapshot = true;

        long frameId = checked((committedFrame?.FrameId ?? 0) + 1);
        DisplayCommit commit = new(
            frameId,
            currentSequence,
            reason,
            requiresSnapshot,
            snapshot,
            requiresSnapshot ? Array.Empty<SequencedConsoleTransaction>() : pendingCommitTransactions.ToArray());
        committedFrame = commit;
        pendingCommitTransactions.Clear();
        pendingCommitEstimatedBytes = 0;
        requiresSnapshotAtCommit = false;
        return commit;
    }

    private void ApplyStructuredOperation(StructuredCandidate candidate, ConsoleOperation operation)
    {
        switch (operation)
        {
            case AppendNodesOperation append:
                ConsoleNodeValidation.ValidateBatch(append.Nodes, limits);
                AppendNodes(candidate.Scrollback, append.Nodes);
                break;
            case ClearConsoleOperation:
            case ClearScrollbackOperation:
                candidate.Scrollback.Clear();
                break;
            case OpenPromptOperation open:
                if (candidate.CurrentPrompt is not null)
                    throw new ConsoleContractException(ConsoleContractViolationReason.PromptAlreadyActive, "A console prompt is already active.");
                open.Prompt.Validate(limits);
                candidate.CurrentPrompt = open.Prompt;
                break;
            case ClosePromptOperation close:
                if (candidate.CurrentPrompt is null)
                    throw new ConsoleContractException(ConsoleContractViolationReason.PromptAlreadyCompleted, "There is no active console prompt.");
                if (!string.Equals(candidate.CurrentPrompt.PromptId, close.PromptId, StringComparison.Ordinal))
                    throw new ConsoleContractException(ConsoleContractViolationReason.PromptIdMismatch, "The close operation does not match the active prompt.");
                candidate.CurrentPrompt = null;
                break;
            case AppendLineOperation appendLine:
                ValidateLineLimits(appendLine.Line);
                if (candidate.Scrollback.Any(line => line.LineId == appendLine.Line.LineId))
                    throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "The line id is already present.");
                candidate.Scrollback.Add(appendLine.Line);
                break;
            case AppendInlineOperation inline:
                ConsoleNodeValidation.ValidateBatch(inline.Nodes, limits);
                ConsoleLine inlineLine = FindLine(candidate.Scrollback, inline.LineId);
                if (inline.Nodes.Any(node => node is LineBreakNode))
                    throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "Inline output cannot contain a line break.");
                candidate.Scrollback[candidate.Scrollback.IndexOf(inlineLine)] = inlineLine.WithNodes(inlineLine.Nodes.Concat(inline.Nodes));
                break;
            case ReplaceLineOperation replace:
                ValidateLineLimits(replace.Line);
                int replaceIndex = candidate.Scrollback.FindIndex(line => line.LineId == replace.Line.LineId);
                if (replaceIndex < 0)
                    throw new ConsoleContractException(ConsoleContractViolationReason.InvalidIdentifier, "The line id does not exist.");
                candidate.Scrollback[replaceIndex] = replace.Line;
                break;
            case DeleteLinesOperation delete:
                foreach (string lineId in delete.LineIds)
                {
                    int index = candidate.Scrollback.FindIndex(line => line.LineId == lineId);
                    if (index < 0)
                        throw new ConsoleContractException(ConsoleContractViolationReason.InvalidIdentifier, "The line id does not exist.");
                    candidate.Scrollback.RemoveAt(index);
                }
                break;
            case SetWindowMetadataOperation window:
                candidate.WindowMetadata = window.Metadata;
                break;
            case UpsertBackgroundOperation background:
                candidate.BackgroundLayers[background.Layer.LayerId] = background.Layer;
                break;
            case RemoveBackgroundOperation removeBackground:
                candidate.BackgroundLayers.Remove(removeBackground.LayerId);
                break;
            case ClearBackgroundsOperation:
                candidate.BackgroundLayers.Clear();
                break;
            case UpsertDrawableOperation drawable:
                candidate.Drawables[drawable.Drawable.DrawableId] = drawable.Drawable;
                break;
            case RemoveDrawableOperation removeDrawable:
                candidate.Drawables.Remove(removeDrawable.DrawableId);
                break;
            case ClearSceneRangeOperation range:
                foreach (string id in candidate.Drawables.Values
                    .Where(item => item.ZIndex >= range.MinimumZIndex && item.ZIndex <= range.MaximumZIndex)
                    .Select(item => item.DrawableId)
                    .ToArray())
                    candidate.Drawables.Remove(id);
                break;
            case ClearSceneOperation:
                candidate.Drawables.Clear();
                candidate.HitRegions.Clear();
                break;
            case UpsertHitRegionOperation hit:
                candidate.HitRegions[hit.Region.RegionId] = hit.Region;
                break;
            case RemoveHitRegionOperation removeHit:
                candidate.HitRegions.Remove(removeHit.RegionId);
                break;
            case ClearHitRegionsOperation:
                candidate.HitRegions.Clear();
                break;
            case SetMediaChannelOperation media:
                candidate.MediaChannels[media.Channel.Channel] = media.Channel;
                break;
            case StopMediaChannelOperation stop:
                if (candidate.MediaChannels.TryGetValue(stop.Channel, out MediaChannelState? current))
                {
                    candidate.MediaChannels[stop.Channel] = new MediaChannelState(
                        stop.Channel,
                        current.AssetId,
                        ConsoleMediaPlaybackState.Stopped,
                        current.Loop,
                        current.Volume,
                        checked(current.Revision + 1),
                        current.StartPolicy);
                }
                break;
            case StopAllMediaOperation:
                foreach (MediaChannelState mediaChannel in candidate.MediaChannels.Values.ToArray())
                    candidate.MediaChannels[mediaChannel.Channel] = new MediaChannelState(
                        mediaChannel.Channel,
                        mediaChannel.AssetId,
                        ConsoleMediaPlaybackState.Stopped,
                        mediaChannel.Loop,
                        mediaChannel.Volume,
                        checked(mediaChannel.Revision + 1),
                        mediaChannel.StartPolicy);
                break;
            default:
                throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "The console operation is not part of the structured contract.");
        }

        if (candidate.BackgroundLayers.Count > limits.MaxBackgroundLayers ||
            candidate.Drawables.Count > limits.MaxDrawables ||
            candidate.HitRegions.Count > limits.MaxHitRegions ||
            candidate.MediaChannels.Count > limits.MaxMediaChannels)
            throw new ConsoleContractException(ConsoleContractViolationReason.SceneTooLarge, "The structured scene or media state exceeds its limit.");
    }

    private void ValidateTransactionLimits(ConsoleTransaction transaction)
    {
        if (transaction.Operations.Count > limits.MaxTransactionOperations)
            throw new ConsoleContractException(
                ConsoleContractViolationReason.BatchTooLarge,
                "The console transaction exceeds its configured operation limit.");

        foreach (ConsoleOperation operation in transaction.Operations)
        {
            switch (operation)
            {
                case AppendNodesOperation append:
                    ConsoleNodeValidation.ValidateBatch(append.Nodes, limits);
                    break;
                case AppendInlineOperation inline:
                    ConsoleNodeValidation.ValidateBatch(inline.Nodes, limits);
                    break;
                case DeleteLinesOperation delete when delete.LineIds.Count > limits.MaxTransactionOperations:
                    throw new ConsoleContractException(
                        ConsoleContractViolationReason.BatchTooLarge,
                        "The delete operation exceeds its configured identifier limit.");
                case AppendLineOperation appendLine:
                    ValidateLineLimits(appendLine.Line);
                    break;
                case ReplaceLineOperation replace:
                    ValidateLineLimits(replace.Line);
                    break;
            }
        }
    }

    private void ValidateLineLimits(ConsoleLine line)
    {
        ConsoleContractValidation.ValidateIdentifier(line.LineId, nameof(line.LineId), limits.MaxLineIdLength);
        if (line.Nodes.Count > limits.MaxNodesPerLine)
            throw new ConsoleContractException(ConsoleContractViolationReason.LineTooLarge, "The console line exceeds its configured node limit.");
        ConsoleNodeValidation.ValidateBatchIfNotEmpty(line.Nodes, limits);
    }

    private void FitStructuredCandidate(StructuredCandidate candidate)
    {
        ConsoleNode[] flattened = FlattenStructuredLines(candidate.Scrollback);
        ConsoleNodeMetrics metrics = ConsoleSizeEstimator.MeasureNodes(flattened);
        while (candidate.Scrollback.Count > limits.MaxScrollbackLines ||
               metrics.NodeCount > Math.Min(limits.MaxScrollbackNodes, options.MaxVisibleNodes) ||
               metrics.TextLength > Math.Min(limits.MaxScrollbackTextLength, options.MaxVisibleTextLength))
        {
            if (candidate.Scrollback.Count == 0)
                break;
            ConsoleLine removed = candidate.Scrollback[0];
            ConsoleNodeMetrics removedMetrics = ConsoleSizeEstimator.MeasureNodes(removed.Nodes);
            if (candidate.Scrollback.Count == 1 &&
                (removedMetrics.NodeCount > Math.Min(limits.MaxScrollbackNodes, options.MaxVisibleNodes) ||
                 removedMetrics.TextLength > Math.Min(limits.MaxScrollbackTextLength, options.MaxVisibleTextLength)))
                throw new ConsoleContractException(ConsoleContractViolationReason.NodeExceedsHistoryBudget, "A single structured line exceeds its budget.");
            candidate.Scrollback.RemoveAt(0);
            metrics -= removedMetrics;
            candidate.DroppedLineCount = checked(candidate.DroppedLineCount + 1);
            candidate.DroppedNodeCount = checked(candidate.DroppedNodeCount + removedMetrics.NodeCount);
            candidate.DroppedTextLength = checked(candidate.DroppedTextLength + removedMetrics.TextLength);
            candidate.WasTruncated = true;
        }
    }

    private static ConsoleLine FindLine(IReadOnlyList<ConsoleLine> lines, string lineId) =>
        lines.FirstOrDefault(line => string.Equals(line.LineId, lineId, StringComparison.Ordinal))
        ?? throw new ConsoleContractException(ConsoleContractViolationReason.InvalidIdentifier, "The line id does not exist.");

    private void AppendNodes(List<ConsoleLine> lines, IReadOnlyList<ConsoleNode> nodes)
    {
        if (lines.Count == 0)
            lines.Add(new ConsoleLine(NextGeneratedLineId(lines), Array.Empty<ConsoleNode>()));
        foreach (ConsoleNode node in nodes)
        {
            if (node is LineBreakNode)
            {
                lines.Add(new ConsoleLine(NextGeneratedLineId(lines), Array.Empty<ConsoleNode>()));
                continue;
            }

            ConsoleLine last = lines[^1];
            if (last.Nodes.Count >= limits.MaxNodesPerLine)
                throw new ConsoleContractException(ConsoleContractViolationReason.LineTooLarge, "The current line exceeds its node limit.");
            lines[^1] = last.WithNodes(last.Nodes.Append(node));
        }
    }

    private string NextGeneratedLineId(IEnumerable<ConsoleLine>? existingLines = null)
    {
        string candidate;
        do
        {
            candidate = $"line-{checked(++generatedLineId):x}";
        }
        while (existingLines is not null && existingLines.Any(line => string.Equals(line.LineId, candidate, StringComparison.Ordinal)));

        return candidate;
    }

    public SequencedConsoleEvent Apply(ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (sync)
        {
            if (structuredMode)
            {
                SequencedConsoleTransaction structured = ApplyTransaction(new ConsoleTransaction([operation]));
                return new SequencedConsoleEvent(structured.Sequence, operation);
            }

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

            // The legacy node reducer is still the hot path for ordinary
            // upstream text operations. Record the same explicit display
            // boundary metadata as the structured reducer so a prompt opened
            // through IGameConsole also promotes one atomic waiting frame.
            var transaction = new SequencedConsoleTransaction(
                nextSequence,
                new ConsoleTransaction([operation]));
            RecordPendingCommitTransaction(transaction, operationEstimatedBytes);
            if (operation is OpenPromptOperation)
                _ = CommitDisplayFrameLocked(DisplayCommitReason.WaitingForInput);

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

    /// <summary>
    /// Reads the lossless structured transaction stream used by the v3 Worker
    /// protocol. Legacy <see cref="ReadSince"/> intentionally remains a
    /// compatibility API for the historical v2 tests and cannot represent
    /// scenes, media or window metadata without flattening them.
    /// </summary>
    public StructuredConsoleResumeResult ReadStructuredSince(long lastSequence)
    {
        lock (sync)
        {
            if (!structuredMode)
                EnsureStructuredState();

            if (lastSequence < 0 || lastSequence > currentSequence)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidCursor,
                    "The structured console cursor is outside the current sequence range.",
                    nameof(lastSequence));
            }

            if (lastSequence == currentSequence)
                return new StructuredConsoleUpToDateResult(currentSequence);

            if (lastSequence == 0 || transactionHistory.Count == 0 || lastSequence < baselineSnapshot.SnapshotSequence)
            {
                return new StructuredConsoleSnapshotWithDeltasResult(
                    baselineSnapshot,
                    transactionHistory.ToArray(),
                    currentSequence);
            }

            long firstSequence = transactionHistory[0].Sequence;
            long lastHistorySequence = transactionHistory[^1].Sequence;
            if (lastHistorySequence == currentSequence && lastSequence >= firstSequence - 1)
            {
                return new StructuredConsoleDeltaBatchResult(
                    lastSequence,
                    currentSequence,
                    transactionHistory.Where(item => item.Sequence > lastSequence).ToArray());
            }

            return new StructuredConsoleSnapshotWithDeltasResult(
                baselineSnapshot,
                transactionHistory.ToArray(),
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
            case ClearScrollbackOperation:
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

    private ConsoleSnapshot CreateStructuredSnapshot(long sequence)
    {
        return CreateStructuredSnapshot(
            sequence,
            structuredScrollback,
            backgroundLayers,
            drawables,
            hitRegions,
            mediaChannels,
            windowMetadata,
            currentPrompt,
            wasTruncated,
            droppedNodeCount,
            droppedLineCount,
            droppedTextLength);
    }

    private static ConsoleSnapshot CreateStructuredSnapshot(
        long sequence,
        IReadOnlyList<ConsoleLine> scrollback,
        IReadOnlyDictionary<string, BackgroundLayer> backgrounds,
        IReadOnlyDictionary<string, CanvasDrawable> structuredDrawables,
        IReadOnlyDictionary<string, HitRegion> structuredHitRegions,
        IReadOnlyDictionary<string, MediaChannelState> channels,
        WindowMetadata metadata,
        ConsolePrompt? prompt,
        bool truncated,
        long droppedNodes,
        long droppedLines,
        long droppedText)
    {
        return new ConsoleSnapshot(
            sequence,
            scrollback,
            backgrounds.Values.OrderBy(layer => layer.Depth).ThenBy(layer => layer.LayerId, StringComparer.Ordinal),
            new CanvasScene(
                structuredDrawables.Values.OrderBy(drawable => drawable.ZIndex).ThenBy(drawable => drawable.DrawableId, StringComparer.Ordinal),
                structuredHitRegions.Values.OrderBy(region => region.RegionId, StringComparer.Ordinal)),
            new MediaState(channels.Values.OrderBy(channel => channel.Channel, StringComparer.Ordinal)),
            prompt,
            metadata,
            new ConsoleTruncationMetadata(truncated, droppedNodes, droppedLines, droppedText));
    }

    private List<ConsoleLine> BuildStructuredLines(IEnumerable<ConsoleNode> nodes)
    {
        var lines = new List<ConsoleLine>();
        var current = new List<ConsoleNode>();
        foreach (ConsoleNode node in nodes)
        {
            if (node is LineBreakNode)
            {
                lines.Add(new ConsoleLine(NextGeneratedLineId(lines), current));
                current = [];
            }
            else
            {
                current.Add(node);
            }
        }

        if (current.Count != 0)
            lines.Add(new ConsoleLine(NextGeneratedLineId(lines), current));
        return lines;
    }

    private static ConsoleNode[] FlattenStructuredLines(IEnumerable<ConsoleLine> lines)
    {
        var nodes = new List<ConsoleNode>();
        foreach (ConsoleLine line in lines)
        {
            nodes.AddRange(line.Nodes);
            nodes.Add(LineBreakNode.Instance);
        }
        if (nodes.Count > 0)
            nodes.RemoveAt(nodes.Count - 1);
        return nodes.ToArray();
    }

    private sealed class StructuredCandidate(
        List<ConsoleLine> scrollback,
        Dictionary<string, BackgroundLayer> backgroundLayers,
        Dictionary<string, CanvasDrawable> drawables,
        Dictionary<string, HitRegion> hitRegions,
        Dictionary<string, MediaChannelState> mediaChannels,
        WindowMetadata windowMetadata,
        ConsolePrompt? currentPrompt,
        long droppedLineCount,
        long droppedNodeCount,
        long droppedTextLength)
    {
        public List<ConsoleLine> Scrollback { get; } = scrollback;

        public Dictionary<string, BackgroundLayer> BackgroundLayers { get; } = backgroundLayers;

        public Dictionary<string, CanvasDrawable> Drawables { get; } = drawables;

        public Dictionary<string, HitRegion> HitRegions { get; } = hitRegions;

        public Dictionary<string, MediaChannelState> MediaChannels { get; } = mediaChannels;

        public WindowMetadata WindowMetadata { get; set; } = windowMetadata;

        public ConsolePrompt? CurrentPrompt { get; set; } = currentPrompt;

        public long DroppedLineCount { get; set; } = droppedLineCount;

        public long DroppedNodeCount { get; set; } = droppedNodeCount;

        public long DroppedTextLength { get; set; } = droppedTextLength;

        public bool WasTruncated { get; set; }
    }

    private sealed record PreparedMutation(
        IReadOnlyList<ConsoleNode> VisibleNodes,
        ConsolePrompt? CurrentPrompt,
        bool WasTruncated,
        long DroppedNodeCount,
        bool DroppedThisOperation);

    private sealed record VisibleFit(IReadOnlyList<ConsoleNode> Nodes, long DroppedCount);
}

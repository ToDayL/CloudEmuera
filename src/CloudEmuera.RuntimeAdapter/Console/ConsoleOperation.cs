namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleOperationKind
{
    AppendNodes,
    ClearConsole,
    ClearScrollback,
    OpenPrompt,
    ClosePrompt,
    AppendLine,
    AppendInline,
    ReplaceLine,
    DeleteLines,
    SetWindowMetadata,
    UpsertBackground,
    RemoveBackground,
    ClearBackgrounds,
    UpsertDrawable,
    RemoveDrawable,
    ClearSceneRange,
    ClearScene,
    UpsertHitRegion,
    RemoveHitRegion,
    ClearHitRegions,
    SetMediaChannel,
    StopMediaChannel,
    StopAllMedia,
    SetTooltipPresentation,
    UpsertTooltipResource,
    RemoveTooltipResource,
    ClearTooltipResources
}

public abstract class ConsoleOperation
{
    private protected ConsoleOperation()
    {
    }

    public abstract ConsoleOperationKind Kind { get; }

    public static AppendNodesOperation Append(IEnumerable<ConsoleNode> nodes) => new(nodes);

    public static AppendNodesOperation AppendNodes(IEnumerable<ConsoleNode> nodes) => new(nodes);

    public static ClearConsoleOperation Clear() => new();

    public static ClearConsoleOperation ClearConsole() => new();

    public static ClearScrollbackOperation ClearScrollback() => new();

    public static OpenPromptOperation Open(ConsolePrompt prompt) => new(prompt);

    public static ClosePromptOperation Close(
        string promptId,
        ConsolePromptCloseReason reason = ConsolePromptCloseReason.Completed) =>
        new(promptId, reason);

    public static AppendLineOperation AppendLine(ConsoleLine line) => new(line);

    public static AppendInlineOperation AppendInline(string lineId, IEnumerable<ConsoleNode> nodes) => new(lineId, nodes);

    public static ReplaceLineOperation ReplaceLine(ConsoleLine line) => new(line);

    public static DeleteLinesOperation DeleteLines(IEnumerable<string> lineIds) => new(lineIds);

    public static SetWindowMetadataOperation SetWindow(WindowMetadata metadata) => new(metadata);

    public static UpsertBackgroundOperation UpsertBackground(BackgroundLayer layer) => new(layer);

    public static RemoveBackgroundOperation RemoveBackground(string layerId) => new(layerId);

    public static ClearBackgroundsOperation ClearBackgrounds() => new();

    public static UpsertDrawableOperation UpsertDrawable(CanvasDrawable drawable) => new(drawable);

    public static RemoveDrawableOperation RemoveDrawable(string drawableId) => new(drawableId);

    public static ClearSceneRangeOperation ClearSceneRange(int minimumZIndex, int maximumZIndex) => new(minimumZIndex, maximumZIndex);

    public static ClearSceneOperation ClearScene() => new();

    public static UpsertHitRegionOperation UpsertHitRegion(HitRegion region) => new(region);

    public static RemoveHitRegionOperation RemoveHitRegion(string regionId) => new(regionId);

    public static ClearHitRegionsOperation ClearHitRegions() => new();

    public static SetMediaChannelOperation SetMediaChannel(MediaChannelState channel) => new(channel);

    public static StopMediaChannelOperation StopMediaChannel(string channel) => new(channel);

    public static StopAllMediaOperation StopAllMedia() => new();

    public static SetTooltipPresentationOperation SetTooltipPresentation(ConsoleTooltipPresentation presentation) => new(presentation);

    public static UpsertTooltipResourceOperation UpsertTooltipResource(ConsoleTooltipResource resource) => new(resource);

    public static RemoveTooltipResourceOperation RemoveTooltipResource(int graphicsId) => new(graphicsId);

    public static ClearTooltipResourcesOperation ClearTooltipResources() => new();
}

public sealed class AppendNodesOperation : ConsoleOperation
{
    public AppendNodesOperation(IEnumerable<ConsoleNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ConsoleNode[] copy = nodes.ToArray();
        ConsoleNodeValidation.ValidateBatch(copy, ConsoleContractLimits.Default);
        Nodes = Array.AsReadOnly(copy);
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.AppendNodes;

    public IReadOnlyList<ConsoleNode> Nodes { get; }
}

public sealed class ClearConsoleOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearConsole;
}

/// <summary>Clears only the line-oriented scrollback in the structured model.</summary>
public sealed class ClearScrollbackOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearScrollback;
}

public enum ConsolePromptCloseReason
{
    Completed,
    InputAccepted,
    Cancelled,
    TimedOut,
    Explicit
}

public sealed class OpenPromptOperation : ConsoleOperation
{
    public OpenPromptOperation(ConsolePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.HasPromptId)
        {
            prompt.Validate(ConsoleContractLimits.Default);
        }
        else
        {
            prompt.ValidateTemplate(ConsoleContractLimits.Default);
        }

        Prompt = prompt;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.OpenPrompt;

    public ConsolePrompt Prompt { get; }
}

public sealed class ClosePromptOperation : ConsoleOperation
{
    public ClosePromptOperation(
        string promptId,
        ConsolePromptCloseReason reason = ConsolePromptCloseReason.Completed)
    {
        ConsoleContractValidation.ValidateIdentifier(
            promptId,
            nameof(promptId),
            ConsoleContractLimits.Default.MaxPromptIdLength);
        PromptId = promptId;
        Reason = reason;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClosePrompt;

    public string PromptId { get; }

    public ConsolePromptCloseReason Reason { get; }
}

public sealed class AppendLineOperation : ConsoleOperation
{
    public AppendLineOperation(ConsoleLine line)
    {
        Line = line ?? throw new ArgumentNullException(nameof(line));
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.AppendLine;

    public ConsoleLine Line { get; }
}

public sealed class AppendInlineOperation : ConsoleOperation
{
    public AppendInlineOperation(string lineId, IEnumerable<ConsoleNode> nodes)
    {
        ConsoleContractValidation.ValidateIdentifier(lineId, nameof(lineId), ConsoleContractLimits.Default.MaxLineIdLength);
        ArgumentNullException.ThrowIfNull(nodes);
        ConsoleNode[] copy = nodes.ToArray();
        ConsoleNodeValidation.ValidateBatch(copy, ConsoleContractLimits.Default);
        LineId = lineId;
        Nodes = Array.AsReadOnly(copy);
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.AppendInline;

    public string LineId { get; }

    public IReadOnlyList<ConsoleNode> Nodes { get; }
}

public sealed class ReplaceLineOperation : ConsoleOperation
{
    public ReplaceLineOperation(ConsoleLine line)
    {
        Line = line ?? throw new ArgumentNullException(nameof(line));
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.ReplaceLine;

    public ConsoleLine Line { get; }
}

public sealed class DeleteLinesOperation : ConsoleOperation
{
    public DeleteLinesOperation(IEnumerable<string> lineIds)
    {
        ArgumentNullException.ThrowIfNull(lineIds);
        string[] copy = lineIds.ToArray();
        if (copy.Length == 0 || copy.Length > ConsoleContractLimits.Default.MaxTransactionOperations)
            throw new ConsoleContractException(ConsoleContractViolationReason.EmptyBatch, "At least one line id is required.");
        foreach (string id in copy)
            ConsoleContractValidation.ValidateIdentifier(id, nameof(lineIds), ConsoleContractLimits.Default.MaxLineIdLength);
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Line ids must be unique.");
        LineIds = Array.AsReadOnly(copy);
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.DeleteLines;

    public IReadOnlyList<string> LineIds { get; }
}

public sealed class SetWindowMetadataOperation : ConsoleOperation
{
    public SetWindowMetadataOperation(WindowMetadata metadata)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.SetWindowMetadata;

    public WindowMetadata Metadata { get; }
}

public sealed class UpsertBackgroundOperation : ConsoleOperation
{
    public UpsertBackgroundOperation(BackgroundLayer layer) => Layer = layer ?? throw new ArgumentNullException(nameof(layer));

    public override ConsoleOperationKind Kind => ConsoleOperationKind.UpsertBackground;

    public BackgroundLayer Layer { get; }
}

public sealed class RemoveBackgroundOperation : ConsoleOperation
{
    public RemoveBackgroundOperation(string layerId)
    {
        ConsoleContractValidation.ValidateIdentifier(layerId, nameof(layerId), ConsoleContractLimits.Default.MaxLayerIdLength);
        LayerId = layerId;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.RemoveBackground;

    public string LayerId { get; }
}

public sealed class ClearBackgroundsOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearBackgrounds;
}

public sealed class UpsertDrawableOperation : ConsoleOperation
{
    public UpsertDrawableOperation(CanvasDrawable drawable) => Drawable = drawable ?? throw new ArgumentNullException(nameof(drawable));

    public override ConsoleOperationKind Kind => ConsoleOperationKind.UpsertDrawable;

    public CanvasDrawable Drawable { get; }
}

public sealed class RemoveDrawableOperation : ConsoleOperation
{
    public RemoveDrawableOperation(string drawableId)
    {
        ConsoleContractValidation.ValidateIdentifier(drawableId, nameof(drawableId), ConsoleContractLimits.Default.MaxDrawableIdLength);
        DrawableId = drawableId;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.RemoveDrawable;

    public string DrawableId { get; }
}

public sealed class ClearSceneRangeOperation : ConsoleOperation
{
    public ClearSceneRangeOperation(int minimumZIndex, int maximumZIndex)
    {
        if (minimumZIndex > maximumZIndex)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "The scene range is inverted.");
        MinimumZIndex = minimumZIndex;
        MaximumZIndex = maximumZIndex;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearSceneRange;

    public int MinimumZIndex { get; }

    public int MaximumZIndex { get; }
}

public sealed class ClearSceneOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearScene;
}

public sealed class UpsertHitRegionOperation : ConsoleOperation
{
    public UpsertHitRegionOperation(HitRegion region) => Region = region ?? throw new ArgumentNullException(nameof(region));

    public override ConsoleOperationKind Kind => ConsoleOperationKind.UpsertHitRegion;

    public HitRegion Region { get; }
}

public sealed class RemoveHitRegionOperation : ConsoleOperation
{
    public RemoveHitRegionOperation(string regionId)
    {
        ConsoleContractValidation.ValidateIdentifier(regionId, nameof(regionId), ConsoleContractLimits.Default.MaxHitRegionIdLength);
        RegionId = regionId;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.RemoveHitRegion;

    public string RegionId { get; }
}

public sealed class ClearHitRegionsOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearHitRegions;
}

public sealed class SetMediaChannelOperation : ConsoleOperation
{
    public SetMediaChannelOperation(MediaChannelState channel) => Channel = channel ?? throw new ArgumentNullException(nameof(channel));

    public override ConsoleOperationKind Kind => ConsoleOperationKind.SetMediaChannel;

    public MediaChannelState Channel { get; }
}

public sealed class StopMediaChannelOperation : ConsoleOperation
{
    public StopMediaChannelOperation(string channel)
    {
        ConsoleContractValidation.ValidateIdentifier(channel, nameof(channel), ConsoleContractLimits.Default.MaxMediaChannelLength);
        Channel = channel;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.StopMediaChannel;

    public string Channel { get; }
}

public sealed class StopAllMediaOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.StopAllMedia;
}

public sealed class SetTooltipPresentationOperation : ConsoleOperation
{
    public SetTooltipPresentationOperation(ConsoleTooltipPresentation presentation) =>
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));

    public override ConsoleOperationKind Kind => ConsoleOperationKind.SetTooltipPresentation;

    public ConsoleTooltipPresentation Presentation { get; }
}

public sealed class UpsertTooltipResourceOperation : ConsoleOperation
{
    public UpsertTooltipResourceOperation(ConsoleTooltipResource resource) =>
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));

    public override ConsoleOperationKind Kind => ConsoleOperationKind.UpsertTooltipResource;

    public ConsoleTooltipResource Resource { get; }
}

public sealed class RemoveTooltipResourceOperation : ConsoleOperation
{
    public RemoveTooltipResourceOperation(int graphicsId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(graphicsId);
        GraphicsId = graphicsId;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.RemoveTooltipResource;

    public int GraphicsId { get; }
}

public sealed class ClearTooltipResourcesOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearTooltipResources;
}

public sealed class ConsoleTransaction
{
    public ConsoleTransaction(IEnumerable<ConsoleOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ConsoleOperation[] copy = operations.ToArray();
        if (copy.Length == 0 || copy.Length > ConsoleContractLimits.Default.MaxTransactionOperations)
            throw new ConsoleContractException(ConsoleContractViolationReason.EmptyBatch, "A console transaction must contain a bounded operation list.");
        if (copy.Any(item => item is null))
            throw new ConsoleContractException(ConsoleContractViolationReason.NullValue, "A console operation is required.");
        Operations = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<ConsoleOperation> Operations { get; }
}

public sealed class SequencedConsoleTransaction
{
    public SequencedConsoleTransaction(long sequence, ConsoleTransaction transaction)
    {
        if (sequence <= 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "A transaction sequence must be positive.");
        Sequence = sequence;
        Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public long Sequence { get; }

    public ConsoleTransaction Transaction { get; }
}

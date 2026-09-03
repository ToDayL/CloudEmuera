using System.Text.Json;
using CloudEmuera.Contracts.Realtime;
using CloudEmuera.Ipc.V9;
using R = CloudEmuera.RuntimeAdapter;
using RuntimeMapper = CloudEmuera.Realtime.StructuredConsoleWireMapper;

namespace CloudEmuera.Api.Realtime;

public static class RealtimePayloadMapper
{
    public static R.ConsoleSnapshot FromProto(ConsoleSnapshot snapshot) => RuntimeMapper.FromProto(snapshot);

    public static IReadOnlyList<R.SequencedConsoleTransaction> FromProto(DisplayBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.Transactions
            .Select(transaction => new R.SequencedConsoleTransaction(
                transaction.Sequence,
                RuntimeMapper.FromProto(transaction)))
            .ToArray();
    }

    public static IReadOnlyList<R.SequencedConsoleTransaction> FromProto(DisplayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.Transactions
            .Select(transaction => new R.SequencedConsoleTransaction(
                transaction.Sequence,
                RuntimeMapper.FromProto(transaction)))
            .ToArray();
    }

    public static R.DisplayCommitReason FromProto(DisplayCommitReason reason) => reason switch
    {
        DisplayCommitReason.WaitingForInput => R.DisplayCommitReason.WaitingForInput,
        DisplayCommitReason.RuntimeCompleted => R.DisplayCommitReason.RuntimeCompleted,
        DisplayCommitReason.RuntimeFailed => R.DisplayCommitReason.RuntimeFailed,
        DisplayCommitReason.ExplicitRefresh => R.DisplayCommitReason.ExplicitRefresh,
        _ => throw new InvalidDataException("The display commit reason is outside the IPC contract.")
    };

    public static RealtimeSnapshot ToSnapshot(ulong workerEpoch, R.ConsoleSnapshot snapshot, long committedFrameId = 0) =>
        new(workerEpoch, snapshot.SnapshotSequence, committedFrameId, ToState(snapshot));

    public static RealtimeDisplayFrame ToDisplayFrame(ulong workerEpoch, R.DisplayCommit commit) =>
        new(
            workerEpoch,
            commit.FrameId,
            commit.CommitSequence,
            commit.Reason switch
            {
                R.DisplayCommitReason.WaitingForInput => "WAITING_FOR_INPUT",
                R.DisplayCommitReason.RuntimeCompleted => "RUNTIME_COMPLETED",
                R.DisplayCommitReason.RuntimeFailed => "RUNTIME_FAILED",
                R.DisplayCommitReason.ExplicitRefresh => "EXPLICIT_REFRESH",
                _ => throw new InvalidDataException("The display commit reason is outside the realtime contract.")
            },
            commit.RequiresSnapshot,
            commit.RequiresSnapshot ? ToState(commit.Snapshot) : null,
            commit.Transactions.Select(ToTransaction).ToArray());

    public static RealtimeTransactionBatch ToTransactionBatch(
        ulong workerEpoch,
        IReadOnlyList<R.SequencedConsoleTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        if (transactions.Count == 0)
            throw new ArgumentException("A realtime transaction batch cannot be empty.", nameof(transactions));

        return new RealtimeTransactionBatch(
            workerEpoch,
            transactions[0].Sequence,
            transactions[^1].Sequence,
            transactions.Select(ToTransaction).ToArray());
    }

    private static RealtimeConsoleState ToState(R.ConsoleSnapshot snapshot) => new(
        snapshot.Scrollback.Select(ToLine).ToArray(),
        snapshot.BackgroundLayers.Select(ToBackground).ToArray(),
        new RealtimeCanvasScene(
            snapshot.CanvasScene.Drawables.Select(ToDrawable).ToArray(),
            snapshot.CanvasScene.HitRegions.Select(ToHitRegion).ToArray()),
        new RealtimeMediaState(snapshot.MediaState.Channels.Select(ToMedia).ToArray()),
        ToTooltipPresentation(snapshot.TooltipPresentation),
        snapshot.TooltipResources.Select(ToTooltipResource).ToArray(),
        snapshot.CurrentPrompt is null ? null : ToPrompt(snapshot.CurrentPrompt),
        ToWindow(snapshot.WindowMetadata),
        new RealtimeTruncation(
            snapshot.Truncation.WasTruncated,
            snapshot.Truncation.DroppedNodeCount,
            snapshot.Truncation.DroppedLineCount,
            snapshot.Truncation.DroppedTextLength));

    private static RealtimeLine ToLine(R.ConsoleLine line) => new(
        line.LineId,
        line.Nodes.Select(ToNode).ToArray(),
        ToAlignment(line.Alignment),
        line.Temporary,
        line.NoWrap,
        line.LayoutWidth,
        line.LineHeight,
        line.LogicalLineId,
        line.PhysicalIndex,
        line.IsLogicalStart);

    private static RealtimeNode ToNode(R.ConsoleNode node) => node switch
    {
        R.TextNode text => new("text", Text: text.Text, Style: ToStyle(text.Style)),
        R.LineBreakNode => new("lineBreak"),
        R.ButtonNode button => new(
            "button",
            Children: button.Children.Select(ToNode).ToArray(),
            Value: button.Value,
            Tooltip: button.Tooltip,
            Enabled: button.Enabled,
            Generation: button.Generation,
            PositionX: button.PositionX),
        R.PositionedInlineSegmentNode segment => new(
            "positionedInlineSegment",
            Children: segment.Children.Select(ToNode).ToArray(),
            PositionX: segment.PositionX,
            MeasuredWidth: segment.MeasuredWidth,
            Action: segment.Action is { } action
                ? new ConsoleInlineActionPayload(action.Value, action.Tooltip, action.Enabled, action.Generation)
                : null),
        R.ImageNode image => new(
            "image",
            AssetId: image.AssetId.Value,
            SourceRect: image.SourceRect is { } source ? ToRect(source) : null,
            Destination: image.Destination is { } destination ? ToRect(destination) : null,
            AltText: image.AltText,
            Decorative: image.Decorative,
            ZIndex: image.ZIndex),
        R.SpriteNode sprite => new(
            "sprite",
            AssetId: sprite.AssetId.Value,
            SourceRect: ToRect(sprite.SourceRect),
            Destination: ToRect(sprite.Destination),
            Frame: sprite.Frame,
            ZIndex: sprite.ZIndex,
            Opacity: sprite.Opacity,
            AltText: sprite.AltText,
            HoverAssetId: sprite.HoverAssetId?.Value,
            HoverSourceRect: sprite.HoverSourceRect is { } hover ? ToRect(hover) : null,
            MappingAssetId: sprite.MappingAssetId?.Value,
            MappingSourceRect: sprite.MappingSourceRect is { } mapping ? ToRect(mapping) : null,
            AnimationFrames: sprite.AnimationFrames.Select(ToAnimationFrame).ToArray()),
        R.ShapeNode shape => new(
            "shape",
            Shape: ToShapeKind(shape.Shape),
            Bounds: ToRect(shape.Bounds),
            Fill: ToColor(shape.Fill),
            Stroke: ToColor(shape.Stroke),
            ButtonColor: ToColor(shape.ButtonColor),
            ZIndex: shape.ZIndex,
            Points: shape.Points.Select(ToPoint).ToArray()),
        R.DivNode div => new(
            "div",
            Children: div.Children.Select(ToNode).ToArray(),
            Bounds: ToRect(div.Bounds),
            ZIndex: div.ZIndex,
            Background: ToColor(div.Background),
            IsRelative: div.IsRelative,
            Box: ToBox(div.Box)),
        R.HtmlIslandNode html => new(
            "htmlIsland",
            Root: html.Root is { } root ? ToHtml(root) : null,
            Layout: html.Layout is { } layout ? ToRect(layout) : null,
            Nodes: html.StructuredNodes?.Select(ToNode).ToArray()),
        _ => throw new InvalidDataException("The runtime node is outside the realtime contract.")
    };

    private static RealtimeSpriteAnimationFrame ToAnimationFrame(R.SpriteAnimationFrame frame) => new(
        frame.AssetId.Value,
        ToRect(frame.SourceRect),
        ToPoint(frame.Offset),
        frame.DurationMilliseconds);

    private static RealtimeHtmlNode ToHtml(R.ConsoleHtmlNode node) => node switch
    {
        R.ConsoleHtmlTextNode text => new("text", Text: text.Text),
        R.ConsoleHtmlBreakNode => new("break"),
        R.ConsoleHtmlElementNode element => new(
            "element",
            Tag: element.Tag,
            Children: element.Children.Select(ToHtml).ToArray(),
            Style: ToStyle(element.Style),
            AssetId: element.AssetId,
            AltText: element.AltText),
        _ => throw new InvalidDataException("The runtime HTML node is outside the realtime contract.")
    };

    private static RealtimeTextStyle ToStyle(R.ConsoleTextStyle style) => new(
        ToDecorations(style.Decorations),
        style.FontFamily,
        style.FontSize,
        style.LineHeight,
        ToColor(style.Foreground),
        ToColor(style.Background),
        ToColor(style.ButtonColor));

    private static string[] ToDecorations(R.ConsoleFontStyle decorations)
    {
        var values = new List<string>(4);
        if ((decorations & R.ConsoleFontStyle.Bold) != 0) values.Add("bold");
        if ((decorations & R.ConsoleFontStyle.Italic) != 0) values.Add("italic");
        if ((decorations & R.ConsoleFontStyle.Underline) != 0) values.Add("underline");
        if ((decorations & R.ConsoleFontStyle.Strike) != 0) values.Add("strike");
        return values.ToArray();
    }

    private static RealtimeColor? ToColor(R.ConsoleColor? color) => color is { } value
        ? new RealtimeColor(value.Red, value.Green, value.Blue, value.Alpha)
        : null;

    private static RealtimePoint ToPoint(R.ConsolePoint point) => new(point.X, point.Y);

    private static RealtimeRect ToRect(R.ConsoleRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static RealtimeBoxModel? ToBox(R.ConsoleBoxModel? box) => box is null ? null : new(
        ToInsets(box.Margin),
        ToInsets(box.Padding),
        ToInsets(box.Border),
        ToInsets(box.Radius),
        box.BorderColors.Select(ToColor).ToArray());

    private static RealtimeInsets ToInsets(R.ConsoleInsets insets) => new(insets.Top, insets.Right, insets.Bottom, insets.Left);

    private static RealtimeBackgroundLayer ToBackground(R.BackgroundLayer layer) => new(
        layer.LayerId,
        layer.AssetId.Value,
        ToBackgroundMode(layer.Mode),
        layer.Opacity,
        layer.Depth);

    private static RealtimeDrawable ToDrawable(R.CanvasDrawable drawable) => drawable switch
    {
        R.SpriteDrawable sprite => new(
            "sprite",
            sprite.DrawableId,
            ToRect(sprite.Bounds),
            sprite.ZIndex,
            sprite.Opacity,
            sprite.AssetId.Value,
            ToRect(sprite.SourceRect),
            sprite.Frame,
            sprite.AnimationFrames.Select(ToAnimationFrame).ToArray()),
        R.ShapeDrawable shape => new(
            "shape",
            shape.DrawableId,
            ToRect(shape.Bounds),
            shape.ZIndex,
            shape.Opacity,
            Shape: ToShapeKind(shape.Shape),
            Fill: ToColor(shape.Fill),
            Stroke: ToColor(shape.Stroke),
            Points: shape.Points.Select(ToPoint).ToArray()),
        R.HtmlIslandDrawable html => new(
            "htmlIsland",
            html.DrawableId,
            ToRect(html.Bounds),
            html.ZIndex,
            html.Opacity,
            Root: html.Root is { } root ? ToHtml(root) : null,
            Nodes: html.StructuredNodes?.Select(ToNode).ToArray()),
        R.RasterDrawable raster => new(
            "raster",
            raster.DrawableId,
            ToRect(raster.Bounds),
            raster.ZIndex,
            raster.Opacity,
            PngData: raster.PngData.ToArray(),
            HoverPngData: raster.HoverPngData?.ToArray(),
            HitTestMap: raster.HitTestMap),
        _ => throw new InvalidDataException("The runtime drawable is outside the realtime contract.")
    };

    private static RealtimeHitRegion ToHitRegion(R.HitRegion region) => new(
        region.RegionId,
        ToRect(region.Bounds),
        region.InputValue,
        region.Enabled,
        region.Tooltip);

    private static RealtimeMediaChannel ToMedia(R.MediaChannelState channel) => new(
        channel.Channel,
        channel.AssetId?.Value,
        ToPlaybackState(channel.PlaybackState),
        channel.Loop,
        channel.Volume,
        channel.Revision,
        ToStartPolicy(channel.StartPolicy));

    private static RealtimeTooltipPresentation ToTooltipPresentation(R.ConsoleTooltipPresentation presentation) => new(
        presentation.CustomEnabled,
        ToColor(presentation.Foreground)!,
        ToColor(presentation.Background)!,
        presentation.DelayMilliseconds,
        presentation.DurationMilliseconds,
        presentation.FontFamily,
        presentation.FontSize,
        new RealtimeTooltipTextFormat(
            ToTooltipHorizontal(presentation.TextFormat.Horizontal),
            ToTooltipVertical(presentation.TextFormat.Vertical),
            presentation.TextFormat.Wrap,
            ToTooltipTrimming(presentation.TextFormat.Trimming),
            presentation.TextFormat.ExpandTabs,
            presentation.TextFormat.RightToLeft),
        presentation.ImageMode,
        presentation.Revision);

    private static RealtimeTooltipResource ToTooltipResource(R.ConsoleTooltipResource resource) => new(
        resource.GraphicsId,
        resource.PngData.ToArray(),
        resource.Width,
        resource.Height,
        resource.Revision);

    private static RealtimePrompt ToPrompt(R.ConsolePrompt prompt) => new(
        prompt.PromptId,
        ToInputType(prompt.InputType),
        prompt.PromptText,
        prompt.DefaultValue,
        ToConstraints(prompt.Constraints),
        ToTimeoutBehavior(prompt.TimeoutBehavior),
        ToTimeoutAction(prompt.TimeoutAction),
        ToSources(prompt.AllowedSources),
        prompt.OneInput,
        prompt.SystemInput,
        prompt.StopMessageSkip,
        prompt.DisplayTime,
        prompt.TimeoutMessage,
        prompt.OpenedAtUnixMilliseconds,
        prompt.DeadlineUnixMilliseconds,
        prompt.Timeout is null ? null : prompt.Timeout == Timeout.InfiniteTimeSpan ? -1 : checked((long)prompt.Timeout.Value.TotalMilliseconds),
        prompt.ButtonGeneration);

    private static RealtimeInputConstraints ToConstraints(R.ConsoleInputConstraints constraints) => constraints switch
    {
        R.TextInputConstraints text => new("text", text.MaxLength, AllowControlCharacters: text.AllowControlCharacters),
        R.IntegerInputConstraints integer => new("integer", Minimum: integer.Minimum, Maximum: integer.Maximum, AllowSign: integer.AllowSign),
        R.AnyValueInputConstraints any => new("anyValue", any.MaxLength),
        _ => throw new InvalidDataException("The input constraint is outside the realtime contract.")
    };

    private static string[] ToSources(R.ConsoleInputSource sources)
    {
        var values = new List<string>(4);
        if ((sources & R.ConsoleInputSource.Keyboard) != 0) values.Add("keyboard");
        if ((sources & R.ConsoleInputSource.Button) != 0) values.Add("button");
        if ((sources & R.ConsoleInputSource.Pointer) != 0) values.Add("pointer");
        if ((sources & R.ConsoleInputSource.System) != 0) values.Add("system");
        return values.ToArray();
    }

    private static RealtimeWindowMetadata ToWindow(R.WindowMetadata metadata) => new(
        metadata.Title,
        metadata.ViewportWidth,
        metadata.ViewportHeight,
        ToColor(metadata.DefaultForeground),
        ToColor(metadata.DefaultBackground),
        new RealtimeFontSpec(metadata.DefaultFont.Family, metadata.DefaultFont.Size, metadata.DefaultFont.LineHeight),
        metadata.FontFaceId,
        metadata.WebFontAssetDigest);

    private static RealtimeTransaction ToTransaction(R.SequencedConsoleTransaction transaction) => new(
        transaction.Sequence,
        transaction.Transaction.Operations.Select(ToOperation).ToArray());

    private static RealtimeOperation ToOperation(R.ConsoleOperation operation) => operation switch
    {
        R.AppendNodesOperation append => new("appendNodes", Nodes: append.Nodes.Select(ToNode).ToArray()),
        R.ClearConsoleOperation => new("clearConsole"),
        R.ClearScrollbackOperation => new("clearScrollback"),
        R.OpenPromptOperation open => new("openPrompt", Prompt: ToPrompt(open.Prompt)),
        R.ClosePromptOperation close => new("closePrompt", PromptId: close.PromptId, Reason: ToCloseReason(close.Reason)),
        R.AppendLineOperation appendLine => new("appendLine", Line: ToLine(appendLine.Line)),
        R.AppendInlineOperation inline => new("appendInline", LineId: inline.LineId, Nodes: inline.Nodes.Select(ToNode).ToArray()),
        R.ReplaceLineOperation replace => new("replaceLine", Line: ToLine(replace.Line)),
        R.DeleteLinesOperation delete => new("deleteLines", LineIds: delete.LineIds.ToArray()),
        R.SetWindowMetadataOperation window => new("setWindowMetadata", WindowMetadata: ToWindow(window.Metadata)),
        R.UpsertBackgroundOperation background => new("upsertBackground", BackgroundLayer: ToBackground(background.Layer)),
        R.RemoveBackgroundOperation remove => new("removeBackground", LayerId: remove.LayerId),
        R.ClearBackgroundsOperation => new("clearBackgrounds"),
        R.UpsertDrawableOperation drawable => new("upsertDrawable", Drawable: ToDrawable(drawable.Drawable)),
        R.RemoveDrawableOperation remove => new("removeDrawable", DrawableId: remove.DrawableId),
        R.ClearSceneRangeOperation range => new("clearSceneRange", MinimumZIndex: range.MinimumZIndex, MaximumZIndex: range.MaximumZIndex),
        R.ClearSceneOperation => new("clearScene"),
        R.UpsertHitRegionOperation hit => new("upsertHitRegion", HitRegion: ToHitRegion(hit.Region)),
        R.RemoveHitRegionOperation remove => new("removeHitRegion", RegionId: remove.RegionId),
        R.ClearHitRegionsOperation => new("clearHitRegions"),
        R.SetMediaChannelOperation media => new("setMediaChannel", MediaChannel: ToMedia(media.Channel)),
        R.StopMediaChannelOperation stop => new("stopMediaChannel", Channel: stop.Channel),
        R.StopAllMediaOperation => new("stopAllMedia"),
        R.SetTooltipPresentationOperation tooltip => new("setTooltipPresentation", TooltipPresentation: ToTooltipPresentation(tooltip.Presentation)),
        R.UpsertTooltipResourceOperation tooltipResource => new("upsertTooltipResource", TooltipResource: ToTooltipResource(tooltipResource.Resource)),
        R.RemoveTooltipResourceOperation removeTooltip => new("removeTooltipResource", GraphicsId: removeTooltip.GraphicsId),
        R.ClearTooltipResourcesOperation => new("clearTooltipResources"),
        _ => throw new InvalidDataException("The console operation is outside the realtime contract.")
    };

    private static string ToTooltipHorizontal(R.ConsoleTooltipHorizontalAlignment alignment) => alignment switch
    {
        R.ConsoleTooltipHorizontalAlignment.Left => "left",
        R.ConsoleTooltipHorizontalAlignment.Center => "center",
        R.ConsoleTooltipHorizontalAlignment.Right => "right",
        _ => throw new InvalidDataException("The tooltip horizontal alignment is outside the realtime contract.")
    };

    private static string ToTooltipVertical(R.ConsoleTooltipVerticalAlignment alignment) => alignment switch
    {
        R.ConsoleTooltipVerticalAlignment.Top => "top",
        R.ConsoleTooltipVerticalAlignment.Center => "center",
        R.ConsoleTooltipVerticalAlignment.Bottom => "bottom",
        _ => throw new InvalidDataException("The tooltip vertical alignment is outside the realtime contract.")
    };

    private static string ToTooltipTrimming(R.ConsoleTooltipTrimming trimming) => trimming switch
    {
        R.ConsoleTooltipTrimming.None => "none",
        R.ConsoleTooltipTrimming.CharacterEllipsis => "characterEllipsis",
        R.ConsoleTooltipTrimming.WordEllipsis => "wordEllipsis",
        R.ConsoleTooltipTrimming.PathEllipsis => "pathEllipsis",
        _ => throw new InvalidDataException("The tooltip trimming mode is outside the realtime contract.")
    };

    private static string ToAlignment(R.ConsoleLineAlignment alignment) => alignment switch
    {
        R.ConsoleLineAlignment.Left => "left",
        R.ConsoleLineAlignment.Center => "center",
        R.ConsoleLineAlignment.Right => "right",
        _ => throw new InvalidDataException("The line alignment is outside the realtime contract.")
    };

    private static string ToBackgroundMode(R.ConsoleBackgroundMode mode) => mode switch
    {
        R.ConsoleBackgroundMode.Stretch => "stretch",
        R.ConsoleBackgroundMode.Contain => "contain",
        R.ConsoleBackgroundMode.Cover => "cover",
        R.ConsoleBackgroundMode.Center => "center",
        R.ConsoleBackgroundMode.Repeat => "repeat",
        _ => throw new InvalidDataException("The background mode is outside the realtime contract.")
    };

    private static string ToShapeKind(R.ConsoleShapeKind shape) => shape switch
    {
        R.ConsoleShapeKind.Rectangle => "rectangle",
        R.ConsoleShapeKind.Ellipse => "ellipse",
        R.ConsoleShapeKind.Line => "line",
        R.ConsoleShapeKind.Polygon => "polygon",
        R.ConsoleShapeKind.Space => "space",
        _ => throw new InvalidDataException("The shape kind is outside the realtime contract.")
    };

    private static string ToPlaybackState(R.ConsoleMediaPlaybackState state) => state switch
    {
        R.ConsoleMediaPlaybackState.Stopped => "stopped",
        R.ConsoleMediaPlaybackState.Requested => "requested",
        _ => throw new InvalidDataException("The media playback state is outside the realtime contract.")
    };

    private static string ToStartPolicy(R.ConsoleMediaStartPolicy policy) => policy switch
    {
        R.ConsoleMediaStartPolicy.Immediate => "immediate",
        R.ConsoleMediaStartPolicy.OnUserGesture => "onUserGesture",
        _ => throw new InvalidDataException("The media start policy is outside the realtime contract.")
    };

    private static string ToInputType(R.ConsoleInputType inputType) => inputType switch
    {
        R.ConsoleInputType.EnterKey => "enterKey",
        R.ConsoleInputType.AnyKey => "anyKey",
        R.ConsoleInputType.Integer => "integer",
        R.ConsoleInputType.Text => "text",
        R.ConsoleInputType.AnyValue => "anyValue",
        R.ConsoleInputType.IntegerButton => "integerButton",
        R.ConsoleInputType.TextButton => "textButton",
        R.ConsoleInputType.PrimitivePointerKey => "primitivePointerKey",
        R.ConsoleInputType.WaitOnly => "waitOnly",
        _ => throw new InvalidDataException("The input type is outside the realtime contract.")
    };

    private static string ToTimeoutBehavior(R.ConsolePromptTimeoutBehavior behavior) => behavior switch
    {
        R.ConsolePromptTimeoutBehavior.Cancel => "cancel",
        R.ConsolePromptTimeoutBehavior.ReturnDefaultValue => "returnDefaultValue",
        R.ConsolePromptTimeoutBehavior.ContinueWithoutValue => "continueWithoutValue",
        _ => throw new InvalidDataException("The prompt timeout behavior is outside the realtime contract.")
    };

    private static string ToTimeoutAction(R.ConsolePromptTimeoutAction action) => action switch
    {
        R.ConsolePromptTimeoutAction.ReturnDefaultValue => "returnDefaultValue",
        R.ConsolePromptTimeoutAction.ContinueWithoutValue => "continueWithoutValue",
        R.ConsolePromptTimeoutAction.CancelRuntime => "cancelRuntime",
        _ => throw new InvalidDataException("The prompt timeout action is outside the realtime contract.")
    };

    private static string ToCloseReason(R.ConsolePromptCloseReason reason) => reason switch
    {
        R.ConsolePromptCloseReason.Completed => "completed",
        R.ConsolePromptCloseReason.InputAccepted => "inputAccepted",
        R.ConsolePromptCloseReason.Cancelled => "cancelled",
        R.ConsolePromptCloseReason.TimedOut => "timedOut",
        R.ConsolePromptCloseReason.Explicit => "explicit",
        _ => throw new InvalidDataException("The prompt close reason is outside the realtime contract.")
    };
}

public sealed class RealtimePayloadSerializer
{
    private readonly RealtimeOutputOptions options;

    public RealtimePayloadSerializer(RealtimeOutputOptions? options = null)
    {
        this.options = options ?? RealtimeOutputOptions.Default;
        this.options.Validate();
    }

    public RealtimeEncodedPayload SerializeSnapshot(ulong workerEpoch, R.ConsoleSnapshot snapshot)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            RealtimePayloadMapper.ToSnapshot(workerEpoch, snapshot),
            RealtimeJsonContext.Default.RealtimeSnapshot);
        EnsureSize(bytes, options.SnapshotMaxBytes, "snapshot");
        return new RealtimeEncodedPayload(RealtimePayloadKind.Snapshot, workerEpoch, snapshot.SnapshotSequence, snapshot.SnapshotSequence, bytes);
    }

    public RealtimeEncodedPayload SerializeSnapshot(ulong workerEpoch, R.DisplayCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            RealtimePayloadMapper.ToSnapshot(workerEpoch, commit.Snapshot, commit.FrameId),
            RealtimeJsonContext.Default.RealtimeSnapshot);
        EnsureSize(bytes, options.SnapshotMaxBytes, "snapshot");
        return new RealtimeEncodedPayload(RealtimePayloadKind.Snapshot, workerEpoch, commit.CommitSequence, commit.CommitSequence, bytes, commit.FrameId, commit.Reason.ToString());
    }

    public RealtimeEncodedPayload SerializeDisplayFrame(ulong workerEpoch, R.DisplayCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        RealtimeDisplayFrame payload = RealtimePayloadMapper.ToDisplayFrame(workerEpoch, commit);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, RealtimeJsonContext.Default.RealtimeDisplayFrame);
        EnsureSize(bytes, options.SnapshotMaxBytes, "display frame");
        return new RealtimeEncodedPayload(RealtimePayloadKind.DisplayFrame, workerEpoch, commit.Transactions.Count == 0 ? commit.CommitSequence : commit.Transactions[0].Sequence, commit.CommitSequence, bytes, commit.FrameId, commit.Reason.ToString());
    }

    public RealtimeEncodedPayload SerializeTransactionBatch(
        ulong workerEpoch,
        IReadOnlyList<R.SequencedConsoleTransaction> transactions)
    {
        RealtimeTransactionBatch payload = RealtimePayloadMapper.ToTransactionBatch(workerEpoch, transactions);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, RealtimeJsonContext.Default.RealtimeTransactionBatch);
        EnsureSize(bytes, options.SnapshotMaxBytes, "transaction batch");
        return new RealtimeEncodedPayload(RealtimePayloadKind.TransactionBatch, workerEpoch, payload.FirstSequence, payload.LastSequence, bytes);
    }

    private static void EnsureSize(byte[] bytes, long limit, string kind)
    {
        if (bytes.Length > limit)
            throw new RealtimePayloadSizeException($"The {kind} payload exceeds its configured byte limit.");
    }
}

public enum RealtimePayloadKind
{
    Snapshot,
    TransactionBatch,
    DisplayFrame
}

public sealed class RealtimeEncodedPayload(
    RealtimePayloadKind kind,
    ulong workerEpoch,
    long firstSequence,
    long lastSequence,
    byte[] bytes,
    long frameId = 0,
    string? commitReason = null)
{
    public RealtimePayloadKind Kind { get; } = kind;
    public ulong WorkerEpoch { get; } = workerEpoch;
    public long FirstSequence { get; } = firstSequence;
    public long LastSequence { get; } = lastSequence;
    public ReadOnlyMemory<byte> Bytes { get; } = bytes ?? throw new ArgumentNullException(nameof(bytes));
    public int ByteLength => Bytes.Length;
    public long FrameId { get; } = frameId;
    public string? CommitReason { get; } = commitReason;
}

public sealed class RealtimePayloadSizeException(string message) : ArgumentException(message);

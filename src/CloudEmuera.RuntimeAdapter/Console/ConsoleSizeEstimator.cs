namespace CloudEmuera.RuntimeAdapter;

internal readonly record struct ConsoleNodeMetrics(int NodeCount, long TextLength, long EstimatedBytes)
{
    public static ConsoleNodeMetrics operator +(ConsoleNodeMetrics left, ConsoleNodeMetrics right) =>
        new(
            checked(left.NodeCount + right.NodeCount),
            checked(left.TextLength + right.TextLength),
            checked(left.EstimatedBytes + right.EstimatedBytes));

    public static ConsoleNodeMetrics operator -(ConsoleNodeMetrics left, ConsoleNodeMetrics right) =>
        new(
            left.NodeCount - right.NodeCount,
            left.TextLength - right.TextLength,
            left.EstimatedBytes - right.EstimatedBytes);
}

internal static class ConsoleSizeEstimator
{
    private const int SnapshotOverhead = 128;

    public static ConsoleNodeMetrics MeasureNodes(IEnumerable<ConsoleNode> nodes)
    {
        ConsoleNodeMetrics metrics = default;
        foreach (ConsoleNode node in nodes)
        {
            metrics += MeasureNode(node);
        }

        return metrics;
    }

    public static ConsoleNodeMetrics MeasureNode(ConsoleNode node) =>
        node switch
        {
            TextNode text => new(
                NodeCount: 1,
                TextLength: text.Text.Length,
                EstimatedBytes: checked(48L + text.Text.Length * 2L + MeasureStyle(text.Style))),
            LineBreakNode => new(1, 0, 16),
            ButtonNode button => MeasureButton(button),
            PositionedInlineSegmentNode segment => MeasurePositionedSegment(segment),
            ImageNode image => MeasureImage(image),
            SpriteNode sprite => MeasureSprite(sprite),
            ShapeNode shape => MeasureShape(shape),
            DivNode div => MeasureDiv(div),
            HtmlIslandNode island => MeasureHtmlIsland(island),
            _ => throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "Unknown console node type.")
        };

    public static long MeasurePrompt(ConsolePrompt? prompt)
    {
        if (prompt is null)
        {
            return 0;
        }

        long result = 96L + prompt.PromptId.Length * 2L;
        result = checked(result + (prompt.PromptText?.Length ?? 0) * 2L);
        result = checked(result + (prompt.DefaultValue?.Length ?? 0) * 2L);
        return prompt.Constraints switch
        {
            TextInputConstraints text => checked(result + 16L),
            IntegerInputConstraints integer => checked(result + 32L + (integer.Minimum is null ? 0 : 8) + (integer.Maximum is null ? 0 : 8)),
            _ => checked(result + 16L)
        };
    }

    public static long MeasureOperation(ConsoleOperation operation) =>
        operation switch
        {
            AppendNodesOperation append => checked(64L + MeasureNodes(append.Nodes).EstimatedBytes),
            ClearConsoleOperation => 32L,
            ClearScrollbackOperation => 32L,
            OpenPromptOperation open => checked(64L + MeasurePrompt(open.Prompt)),
            ClosePromptOperation close => checked(48L + close.PromptId.Length * 2L),
            AppendLineOperation line => checked(64L + line.Line.LineId.Length * 2L + MeasureNodes(line.Line.Nodes).EstimatedBytes),
            AppendInlineOperation inline => checked(64L + inline.LineId.Length * 2L + MeasureNodes(inline.Nodes).EstimatedBytes),
            ReplaceLineOperation line => checked(64L + line.Line.LineId.Length * 2L + MeasureNodes(line.Line.Nodes).EstimatedBytes),
            DeleteLinesOperation delete => checked(48L + delete.LineIds.Sum(id => id.Length * 2L)),
            SetWindowMetadataOperation window => checked(64L + window.Metadata.Title.Length * 2L + window.Metadata.DefaultFont.Family.Length * 2L),
            UpsertBackgroundOperation background => checked(64L + background.Layer.LayerId.Length * 2L + background.Layer.AssetId.Value.Length * 2L),
            RemoveBackgroundOperation remove => checked(32L + remove.LayerId.Length * 2L),
            ClearBackgroundsOperation => 24L,
            UpsertDrawableOperation drawable => checked(128L + drawable.Drawable.DrawableId.Length * 2L),
            RemoveDrawableOperation remove => checked(32L + remove.DrawableId.Length * 2L),
            ClearSceneRangeOperation => 32L,
            ClearSceneOperation => 24L,
            UpsertHitRegionOperation hit => checked(64L + hit.Region.RegionId.Length * 2L + hit.Region.InputValue.Length * 2L),
            RemoveHitRegionOperation remove => checked(32L + remove.RegionId.Length * 2L),
            ClearHitRegionsOperation => 24L,
            SetMediaChannelOperation media => checked(64L + media.Channel.Channel.Length * 2L + (media.Channel.AssetId?.Value.Length ?? 0) * 2L),
            StopMediaChannelOperation stop => checked(32L + stop.Channel.Length * 2L),
            StopAllMediaOperation => 24L,
            SetTooltipPresentationOperation presentation => checked(128L + presentation.Presentation.FontFamily.Length * 2L),
            UpsertTooltipResourceOperation resource => checked(64L + resource.Resource.PngData.Count),
            RemoveTooltipResourceOperation => 32L,
            ClearTooltipResourcesOperation => 24L,
            _ => throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "Unknown console operation type.")
        };

    public static long MeasureSnapshot(ConsoleNodeMetrics visible, ConsolePrompt? prompt) =>
        checked(SnapshotOverhead + visible.EstimatedBytes + MeasurePrompt(prompt));

    public static long MeasureStructuredSnapshot(ConsoleSnapshot snapshot, ConsoleNodeMetrics visible)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return MeasureStructuredSnapshot(
            visible,
            snapshot.CurrentPrompt,
            snapshot.Scrollback,
            snapshot.BackgroundLayers,
            snapshot.CanvasScene.Drawables,
            snapshot.CanvasScene.HitRegions,
            snapshot.MediaState.Channels,
            snapshot.WindowMetadata,
            snapshot.TooltipPresentation,
            snapshot.TooltipResources);
    }

    public static long MeasureStructuredSnapshot(
        ConsoleNodeMetrics visible,
        ConsolePrompt? currentPrompt,
        IEnumerable<ConsoleLine> scrollback,
        IEnumerable<BackgroundLayer> backgroundLayers,
        IEnumerable<CanvasDrawable> drawables,
        IEnumerable<HitRegion> hitRegions,
        IEnumerable<MediaChannelState> mediaChannels,
        WindowMetadata windowMetadata,
        ConsoleTooltipPresentation tooltipPresentation,
        IEnumerable<ConsoleTooltipResource> tooltipResources)
    {
        ArgumentNullException.ThrowIfNull(scrollback);
        ArgumentNullException.ThrowIfNull(backgroundLayers);
        ArgumentNullException.ThrowIfNull(drawables);
        ArgumentNullException.ThrowIfNull(hitRegions);
        ArgumentNullException.ThrowIfNull(mediaChannels);
        ArgumentNullException.ThrowIfNull(windowMetadata);
        ArgumentNullException.ThrowIfNull(tooltipPresentation);
        ArgumentNullException.ThrowIfNull(tooltipResources);

        long value = checked(SnapshotOverhead + visible.EstimatedBytes + MeasurePrompt(currentPrompt));
        value = checked(value + scrollback.Sum(MeasureStructuredLine));
        value = checked(value + backgroundLayers.Sum(layer => 64L + layer.LayerId.Length * 2L + layer.AssetId.Value.Length * 2L));
        value = checked(value + drawables.Sum(MeasureDrawable));
        value = checked(value + hitRegions.Sum(region => 64L + region.RegionId.Length * 2L + region.InputValue.Length * 2L));
        value = checked(value + mediaChannels.Sum(channel => 64L + channel.Channel.Length * 2L + (channel.AssetId?.Value.Length ?? 0) * 2L));
        value = checked(value + 64L + windowMetadata.Title.Length * 2L + windowMetadata.DefaultFont.Family.Length * 2L);
        value = checked(value + 128L + tooltipPresentation.FontFamily.Length * 2L +
            tooltipResources.Sum(resource => 64L + resource.PngData.Count));
        return value;
    }

    public static long MeasureStructuredLine(ConsoleLine line) =>
        MeasureStructuredLine(line, MeasureNodes(line.Nodes));

    public static long MeasureStructuredLine(ConsoleLine line, ConsoleNodeMetrics metrics) =>
        checked(32L + line.LineId.Length * 2L + metrics.EstimatedBytes);

    private static ConsoleNodeMetrics MeasureButton(ButtonNode button)
    {
        ConsoleNodeMetrics result = new(
            NodeCount: 1,
            TextLength: button.Value.Length + (button.Tooltip?.Length ?? 0),
            EstimatedBytes: checked(80L + button.Value.Length * 2L + (button.Tooltip?.Length ?? 0) * 2L + (button.PositionX is null ? 0 : 8L)));
        foreach (ConsoleNode child in button.Children)
        {
            result += MeasureNode(child);
        }

        return result;
    }

    private static ConsoleNodeMetrics MeasurePositionedSegment(PositionedInlineSegmentNode segment)
    {
        // Geometry values are fixed-width integer fields. Their pixel values
        // must not be treated as allocation sizes; doing so makes a wide
        // table consume more budget merely because its columns are farther
        // from the origin.
        ConsoleNodeMetrics result = new(1, 0, checked(80L + 8L +
            (segment.Action is null ? 0 : 48L + segment.Action.Value.Length * 2L + (segment.Action.Tooltip?.Length ?? 0) * 2L)));
        foreach (ConsoleNode child in segment.Children)
            result += MeasureNode(child);
        return result;
    }

    private static ConsoleNodeMetrics MeasureImage(ImageNode image) => new(
        NodeCount: 1,
        TextLength: image.AltText?.Length ?? 0,
        EstimatedBytes: checked(72L + image.AssetId.Value.Length * 2L + (image.AltText?.Length ?? 0) * 2L));

    private static ConsoleNodeMetrics MeasureSprite(SpriteNode sprite) => new(
        NodeCount: 1,
        TextLength: sprite.AltText?.Length ?? 0,
        EstimatedBytes: checked(112L + sprite.AssetId.Value.Length * 2L +
            (sprite.HoverAssetId?.Value.Length ?? 0) * 2L +
            (sprite.MappingAssetId?.Value.Length ?? 0) * 2L +
            sprite.AnimationFrames.Sum(frame => 56L + frame.AssetId.Value.Length * 2L) +
            (sprite.AltText?.Length ?? 0) * 2L));

    private static ConsoleNodeMetrics MeasureShape(ShapeNode shape) => new(
        NodeCount: 1,
        TextLength: 0,
        EstimatedBytes: checked(96L + shape.Points.Count * 16L + (shape.Fill is null ? 0 : 4L) +
            (shape.Stroke is null ? 0 : 4L) + (shape.ButtonColor is null ? 0 : 4L)));

    private static ConsoleNodeMetrics MeasureDiv(DivNode div)
    {
        long boxBytes = div.Box is null
            ? 0
            : 4L * 4L + 4L * 4L + div.Box.BorderColors.Count(color => color is not null) * 4L;
        ConsoleNodeMetrics result = new(1, 0, checked(128L + div.Children.Count * 8L + boxBytes +
            (div.Background is null ? 0 : 4L)));
        foreach (ConsoleNode child in div.Children)
            result += MeasureNode(child);
        return result;
    }

    private static ConsoleNodeMetrics MeasureHtmlIsland(HtmlIslandNode island)
    {
        if (island.StructuredNodes is { } nodes)
        {
            ConsoleNodeMetrics nested = MeasureNodes(nodes);
            return new(
                NodeCount: checked(1 + nested.NodeCount),
                TextLength: nested.TextLength,
                EstimatedBytes: checked(96L + nested.EstimatedBytes));
        }

        return new(
            NodeCount: 1,
            TextLength: 0,
            EstimatedBytes: checked(96L + MeasureHtmlNode(island.Root!)));
    }

    private static long MeasureHtmlNode(ConsoleHtmlNode node) => node switch
    {
        ConsoleHtmlTextNode text => checked(32L + text.Text.Length * 2L),
        ConsoleHtmlBreakNode => 8L,
        ConsoleHtmlElementNode element => checked(48L + element.Tag.Length * 2L + (element.AssetId?.Length ?? 0) * 2L + element.Children.Sum(MeasureHtmlNode)),
        _ => throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "Unknown HTML node type.")
    };

    private static long MeasureDrawable(CanvasDrawable drawable) => drawable switch
    {
        SpriteDrawable sprite => checked(128L + sprite.DrawableId.Length * 2L + sprite.AssetId.Value.Length * 2L
            + sprite.AnimationFrames.Sum(frame => 64L + frame.AssetId.Value.Length * 2L)),
        ShapeDrawable shape => checked(112L + shape.DrawableId.Length * 2L + shape.Points.Count * 16L),
        HtmlIslandDrawable island => checked(112L + island.DrawableId.Length * 2L +
            (island.StructuredNodes is { } nodes ? MeasureNodes(nodes).EstimatedBytes : MeasureHtmlNode(island.Root!))),
        RasterDrawable raster => checked(112L + raster.DrawableId.Length * 2L + raster.PngData.Count + (raster.HoverPngData?.Count ?? 0)),
        _ => throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "Unknown canvas drawable type.")
    };

    private static long MeasureStyle(ConsoleTextStyle style) =>
        checked(16L + (style.Foreground is null ? 0 : 4) + (style.Background is null ? 0 : 4) + (style.ButtonColor is null ? 0 : 4));
}

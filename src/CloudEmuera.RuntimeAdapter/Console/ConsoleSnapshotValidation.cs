namespace CloudEmuera.RuntimeAdapter;

internal static class ConsoleSnapshotValidation
{
    public static void Validate(ConsoleSnapshot snapshot, ConsoleHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (snapshot.SnapshotSequence < 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidCursor, "The snapshot sequence cannot be negative.");

        ConsoleContractLimits limits = options.ContractLimits;
        if (snapshot.Scrollback.Count > limits.MaxScrollbackLines)
            throw new ConsoleContractException(ConsoleContractViolationReason.LineTooLarge, "The snapshot scrollback exceeds its line limit.");
        if (snapshot.BackgroundLayers.Count > limits.MaxBackgroundLayers ||
            snapshot.CanvasScene.Drawables.Count > limits.MaxDrawables ||
            snapshot.CanvasScene.HitRegions.Count > limits.MaxHitRegions ||
            snapshot.MediaState.Channels.Count > limits.MaxMediaChannels)
            throw new ConsoleContractException(ConsoleContractViolationReason.SceneTooLarge, "The snapshot scene or media state exceeds its limit.");

        int nodeCount = 0;
        long textLength = 0;
        var lineIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConsoleLine line in snapshot.Scrollback)
        {
            ConsoleContractValidation.ValidateIdentifier(line.LineId, nameof(line.LineId), limits.MaxLineIdLength);
            if (!lineIds.Add(line.LineId))
                throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Snapshot line ids must be unique.");
            if (line.Nodes.Count > limits.MaxNodesPerLine)
                throw new ConsoleContractException(ConsoleContractViolationReason.LineTooLarge, "A snapshot line exceeds its node limit.");
            ConsoleNodeValidation.ValidateBatchIfNotEmpty(line.Nodes, limits);
            ConsoleNodeMetrics lineMetrics = ConsoleSizeEstimator.MeasureNodes(line.Nodes);
            nodeCount = checked(nodeCount + lineMetrics.NodeCount);
            textLength = checked(textLength + lineMetrics.TextLength);
        }

        if (nodeCount > Math.Min(limits.MaxScrollbackNodes, options.MaxVisibleNodes))
            throw new ConsoleContractException(ConsoleContractViolationReason.NodeExceedsHistoryBudget, "The snapshot exceeds its node budget.");
        if (textLength > Math.Min(limits.MaxScrollbackTextLength, options.MaxVisibleTextLength))
            throw new ConsoleContractException(ConsoleContractViolationReason.NodeExceedsVisibleTextBudget, "The snapshot exceeds its text budget.");

        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (BackgroundLayer layer in snapshot.BackgroundLayers)
        {
            ConsoleContractValidation.ValidateIdentifier(layer.LayerId, nameof(layer.LayerId), limits.MaxLayerIdLength);
            if (!layerIds.Add(layer.LayerId))
                throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Snapshot background ids must be unique.");
            layer.AssetId.Validate(limits);
            ValidateFiniteUnit(layer.Opacity, ConsoleContractViolationReason.InvalidOpacity);
        }

        var drawableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (CanvasDrawable drawable in snapshot.CanvasScene.Drawables)
        {
            ConsoleContractValidation.ValidateIdentifier(drawable.DrawableId, nameof(drawable.DrawableId), limits.MaxDrawableIdLength);
            if (!drawableIds.Add(drawable.DrawableId))
                throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Snapshot drawable ids must be unique.");
            ValidateFiniteUnit(drawable.Opacity, ConsoleContractViolationReason.InvalidOpacity);
            switch (drawable)
            {
                case SpriteDrawable sprite:
                    sprite.AssetId.Validate(limits);
                    if (sprite.AnimationFrames.Count > limits.MaxSpriteFrames)
                        throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "The snapshot sprite animation exceeds its frame limit.");
                    break;
                case ShapeDrawable shape when shape.Points.Count > limits.MaxGeometryPoints:
                    throw new ConsoleContractException(ConsoleContractViolationReason.GeometryTooLarge, "The snapshot shape exceeds its point limit.");
                case HtmlIslandDrawable island:
                    if (island.StructuredNodes is { } structuredNodes)
                        ConsoleNodeValidation.ValidateBatchIfNotEmpty(structuredNodes, limits);
                    else
                        island.Root!.Validate(limits, 1);
                    break;
                case RasterDrawable raster:
                    if (raster.PngData.Count > limits.MaxInlineRasterBytes ||
                        checked(raster.PngData.Count + (raster.HoverPngData?.Count ?? 0)) > limits.MaxInlineRasterBytes)
                        throw new ConsoleContractException(ConsoleContractViolationReason.ImageTooLarge, "The snapshot raster exceeds its byte limit.");
                    break;
            }
        }

        var hitRegionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (HitRegion region in snapshot.CanvasScene.HitRegions)
        {
            ConsoleContractValidation.ValidateIdentifier(region.RegionId, nameof(region.RegionId), limits.MaxHitRegionIdLength);
            if (!hitRegionIds.Add(region.RegionId))
                throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Snapshot hit region ids must be unique.");
            ConsoleContractValidation.ValidateText(region.InputValue, nameof(region.InputValue), limits.MaxButtonValueLength, ConsoleContractViolationReason.ButtonValueTooLong);
            if (region.Tooltip is not null)
                ConsoleContractValidation.ValidateText(region.Tooltip, nameof(region.Tooltip), limits.MaxTooltipLength, ConsoleContractViolationReason.TooltipTooLong);
        }

        var mediaChannels = new HashSet<string>(StringComparer.Ordinal);
        foreach (MediaChannelState channel in snapshot.MediaState.Channels)
        {
            ConsoleContractValidation.ValidateIdentifier(channel.Channel, nameof(channel.Channel), limits.MaxMediaChannelLength);
            if (!mediaChannels.Add(channel.Channel))
                throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Snapshot media channel ids must be unique.");
            channel.AssetId?.Validate(limits);
            ValidateFiniteUnit(channel.Volume, ConsoleContractViolationReason.InvalidOpacity);
        }

        ConsoleContractValidation.ValidateText(
            snapshot.WindowMetadata.Title,
            nameof(WindowMetadata.Title),
            limits.MaxWindowTitleLength,
            ConsoleContractViolationReason.WindowMetadataTooLong);
        ConsoleContractValidation.ValidateLogicalName(snapshot.WindowMetadata.DefaultFont.Family, nameof(ConsoleFontSpec.Family), limits.MaxFontFamilyLength);
        if (snapshot.CurrentPrompt is not null)
            snapshot.CurrentPrompt.Validate(limits);

        ConsoleTruncationMetadata truncation = snapshot.Truncation;
        if (truncation.DroppedNodeCount < 0 || truncation.DroppedLineCount < 0 || truncation.DroppedTextLength < 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Snapshot truncation metadata cannot be negative.");

        ConsoleNodeMetrics visibleMetrics = ConsoleSizeEstimator.MeasureNodes(snapshot.VisibleNodes);
        long estimatedBytes = ConsoleSizeEstimator.MeasureStructuredSnapshot(snapshot, visibleMetrics);
        if (estimatedBytes > options.MaxEstimatedBytes)
            throw new ConsoleContractException(ConsoleContractViolationReason.NodeExceedsHistoryBudget, "The snapshot exceeds its estimated byte budget.");
    }

    private static void ValidateFiniteUnit(float value, ConsoleContractViolationReason reason)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value is < 0f or > 1f)
            throw new ConsoleContractException(reason, "A normalized floating-point value is outside its limit.");
    }
}

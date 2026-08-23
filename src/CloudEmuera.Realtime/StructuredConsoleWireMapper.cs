using System.IO;
using CloudEmuera.Ipc.V6;
using R = CloudEmuera.RuntimeAdapter;
using W = CloudEmuera.Ipc.V6;

namespace CloudEmuera.Realtime;

/// <summary>
/// Lossless mapper for the P1-07 structured protocol. The mapper lives in the
/// host-neutral Realtime assembly so API and Worker share one conversion path
/// while RuntimeAdapter remains independent from protobuf.
/// </summary>
public static class StructuredConsoleWireMapper
{
    public static W.ConsoleTransaction ToProto(R.SequencedConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var result = new W.ConsoleTransaction { Sequence = transaction.Sequence };
        result.Operations.AddRange(transaction.Transaction.Operations.Select(ToProto));
        return result;
    }

    public static R.ConsoleTransaction FromProto(W.ConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Sequence <= 0 || transaction.Operations.Count == 0)
            throw new InvalidDataException("The structured transaction is empty or has an invalid sequence.");
        return new R.ConsoleTransaction(transaction.Operations.Select(FromProto));
    }

    public static W.ConsoleOperation ToProto(R.ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = new W.ConsoleOperation();
        switch (operation)
        {
            case R.AppendNodesOperation append:
                result.AppendNodes = new W.AppendNodes();
                result.AppendNodes.Nodes.AddRange(append.Nodes.Select(ToProto));
                break;
            case R.ClearConsoleOperation:
            case R.ClearScrollbackOperation:
                result.ClearScrollback = new W.ClearScrollback();
                break;
            case R.OpenPromptOperation open:
                result.OpenPrompt = new W.OpenPrompt { Prompt = ToProto(open.Prompt) };
                break;
            case R.ClosePromptOperation close:
                result.ClosePrompt = new W.ClosePrompt
                {
                    PromptId = close.PromptId,
                    Reason = ToProto(close.Reason)
                };
                break;
            case R.AppendLineOperation appendLine:
                result.AppendLine = new W.AppendLine { Line = ToProto(appendLine.Line) };
                break;
            case R.AppendInlineOperation inline:
                result.AppendInline = new W.AppendInline { LineId = inline.LineId };
                result.AppendInline.Nodes.AddRange(inline.Nodes.Select(ToProto));
                break;
            case R.ReplaceLineOperation replace:
                result.ReplaceLine = new W.ReplaceLine { Line = ToProto(replace.Line) };
                break;
            case R.DeleteLinesOperation delete:
                result.DeleteLines = new W.DeleteLines();
                result.DeleteLines.LineIds.AddRange(delete.LineIds);
                break;
            case R.SetWindowMetadataOperation window:
                result.SetWindowMetadata = new W.SetWindowMetadata { Metadata = ToProto(window.Metadata) };
                break;
            case R.UpsertBackgroundOperation background:
                result.UpsertBackground = new W.UpsertBackground { Layer = ToProto(background.Layer) };
                break;
            case R.RemoveBackgroundOperation removeBackground:
                result.RemoveBackground = new W.RemoveBackground { LayerId = removeBackground.LayerId };
                break;
            case R.ClearBackgroundsOperation:
                result.ClearBackgrounds = new W.ClearBackgrounds();
                break;
            case R.UpsertDrawableOperation drawable:
                result.UpsertDrawable = new W.UpsertDrawable { Drawable = ToProto(drawable.Drawable) };
                break;
            case R.RemoveDrawableOperation removeDrawable:
                result.RemoveDrawable = new W.RemoveDrawable { DrawableId = removeDrawable.DrawableId };
                break;
            case R.ClearSceneRangeOperation range:
                result.ClearSceneRange = new W.ClearSceneRange
                {
                    MinimumZIndex = range.MinimumZIndex,
                    MaximumZIndex = range.MaximumZIndex
                };
                break;
            case R.ClearSceneOperation:
                result.ClearScene = new W.ClearScene();
                break;
            case R.UpsertHitRegionOperation hit:
                result.UpsertHitRegion = new W.UpsertHitRegion { Region = ToProto(hit.Region) };
                break;
            case R.RemoveHitRegionOperation removeHit:
                result.RemoveHitRegion = new W.RemoveHitRegion { RegionId = removeHit.RegionId };
                break;
            case R.ClearHitRegionsOperation:
                result.ClearHitRegions = new W.ClearHitRegions();
                break;
            case R.SetMediaChannelOperation media:
                result.SetMediaChannel = new W.SetMediaChannel { Channel = ToProto(media.Channel) };
                break;
            case R.StopMediaChannelOperation stop:
                result.StopMediaChannel = new W.StopMediaChannel { Channel = stop.Channel };
                break;
            case R.StopAllMediaOperation:
                result.StopAllMedia = new W.StopAllMedia();
                break;
            default:
                throw new InvalidDataException("The runtime operation is outside the structured protocol.");
        }

        return result;
    }

    public static R.ConsoleOperation FromProto(W.ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.PayloadCase switch
        {
            W.ConsoleOperation.PayloadOneofCase.AppendNodes =>
                new R.AppendNodesOperation(operation.AppendNodes.Nodes.Select(FromProto)),
            W.ConsoleOperation.PayloadOneofCase.ClearScrollback => new R.ClearScrollbackOperation(),
            W.ConsoleOperation.PayloadOneofCase.OpenPrompt =>
                new R.OpenPromptOperation(FromProto(operation.OpenPrompt.Prompt)),
            W.ConsoleOperation.PayloadOneofCase.ClosePrompt =>
                new R.ClosePromptOperation(operation.ClosePrompt.PromptId, FromProto(operation.ClosePrompt.Reason)),
            W.ConsoleOperation.PayloadOneofCase.AppendLine =>
                new R.AppendLineOperation(FromProto(operation.AppendLine.Line)),
            W.ConsoleOperation.PayloadOneofCase.AppendInline =>
                new R.AppendInlineOperation(operation.AppendInline.LineId, operation.AppendInline.Nodes.Select(FromProto)),
            W.ConsoleOperation.PayloadOneofCase.ReplaceLine =>
                new R.ReplaceLineOperation(FromProto(operation.ReplaceLine.Line)),
            W.ConsoleOperation.PayloadOneofCase.DeleteLines =>
                new R.DeleteLinesOperation(operation.DeleteLines.LineIds),
            W.ConsoleOperation.PayloadOneofCase.SetWindowMetadata =>
                new R.SetWindowMetadataOperation(FromProto(operation.SetWindowMetadata.Metadata)),
            W.ConsoleOperation.PayloadOneofCase.UpsertBackground =>
                new R.UpsertBackgroundOperation(FromProto(operation.UpsertBackground.Layer)),
            W.ConsoleOperation.PayloadOneofCase.RemoveBackground =>
                new R.RemoveBackgroundOperation(operation.RemoveBackground.LayerId),
            W.ConsoleOperation.PayloadOneofCase.ClearBackgrounds => new R.ClearBackgroundsOperation(),
            W.ConsoleOperation.PayloadOneofCase.UpsertDrawable =>
                new R.UpsertDrawableOperation(FromProto(operation.UpsertDrawable.Drawable)),
            W.ConsoleOperation.PayloadOneofCase.RemoveDrawable =>
                new R.RemoveDrawableOperation(operation.RemoveDrawable.DrawableId),
            W.ConsoleOperation.PayloadOneofCase.ClearSceneRange =>
                new R.ClearSceneRangeOperation(operation.ClearSceneRange.MinimumZIndex, operation.ClearSceneRange.MaximumZIndex),
            W.ConsoleOperation.PayloadOneofCase.ClearScene => new R.ClearSceneOperation(),
            W.ConsoleOperation.PayloadOneofCase.UpsertHitRegion =>
                new R.UpsertHitRegionOperation(FromProto(operation.UpsertHitRegion.Region)),
            W.ConsoleOperation.PayloadOneofCase.RemoveHitRegion =>
                new R.RemoveHitRegionOperation(operation.RemoveHitRegion.RegionId),
            W.ConsoleOperation.PayloadOneofCase.ClearHitRegions => new R.ClearHitRegionsOperation(),
            W.ConsoleOperation.PayloadOneofCase.SetMediaChannel =>
                new R.SetMediaChannelOperation(FromProto(operation.SetMediaChannel.Channel)),
            W.ConsoleOperation.PayloadOneofCase.StopMediaChannel =>
                new R.StopMediaChannelOperation(operation.StopMediaChannel.Channel),
            W.ConsoleOperation.PayloadOneofCase.StopAllMedia => new R.StopAllMediaOperation(),
            _ => throw new InvalidDataException("The structured operation has no known payload.")
        };
    }

    public static W.ConsoleSnapshot ToProto(R.ConsoleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new W.ConsoleSnapshot
        {
            SnapshotSequence = snapshot.SnapshotSequence,
            CanvasScene = ToProto(snapshot.CanvasScene),
            MediaState = ToProto(snapshot.MediaState),
            WindowMetadata = ToProto(snapshot.WindowMetadata),
            Truncation = ToProto(snapshot.Truncation),
            HasCurrentPrompt = snapshot.CurrentPrompt is not null
        };
        result.Scrollback.AddRange(snapshot.Scrollback.Select(ToProto));
        result.BackgroundLayers.AddRange(snapshot.BackgroundLayers.Select(ToProto));
        if (snapshot.CurrentPrompt is not null)
            result.CurrentPrompt = ToProto(snapshot.CurrentPrompt);
        return result;
    }

    public static R.ConsoleSnapshot FromProto(W.ConsoleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        R.ConsolePrompt? prompt = snapshot.HasCurrentPrompt
            ? snapshot.CurrentPrompt is null ? throw new InvalidDataException("The snapshot prompt is missing.") : FromProto(snapshot.CurrentPrompt)
            : null;
        return new R.ConsoleSnapshot(
            snapshot.SnapshotSequence,
            snapshot.Scrollback.Select(FromProto),
            snapshot.BackgroundLayers.Select(FromProto),
            snapshot.CanvasScene is null ? new R.CanvasScene() : FromProto(snapshot.CanvasScene),
            snapshot.MediaState is null ? new R.MediaState() : FromProto(snapshot.MediaState),
            prompt,
            snapshot.WindowMetadata is null ? new R.WindowMetadata() : FromProto(snapshot.WindowMetadata),
            snapshot.Truncation is null ? new R.ConsoleTruncationMetadata(false, 0) : FromProto(snapshot.Truncation));
    }

    public static W.ConsoleLine ToProto(R.ConsoleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var result = new W.ConsoleLine
        {
            LineId = line.LineId,
            Alignment = line.Alignment switch
            {
                R.ConsoleLineAlignment.Left => W.LineAlignment.Left,
                R.ConsoleLineAlignment.Center => W.LineAlignment.Center,
                R.ConsoleLineAlignment.Right => W.LineAlignment.Right,
                _ => throw new InvalidDataException("The line alignment is unknown.")
            },
            Temporary = line.Temporary,
            NoWrap = line.NoWrap,
            LayoutWidth = line.LayoutWidth,
            LineHeight = line.LineHeight,
            LogicalLineId = line.LogicalLineId,
            PhysicalIndex = line.PhysicalIndex,
            IsLogicalStart = line.IsLogicalStart
        };
        result.Nodes.AddRange(line.Nodes.Select(ToProto));
        return result;
    }

    public static R.ConsoleLine FromProto(W.ConsoleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        R.ConsoleLineAlignment alignment = line.Alignment switch
        {
            W.LineAlignment.Left => R.ConsoleLineAlignment.Left,
            W.LineAlignment.Center => R.ConsoleLineAlignment.Center,
            W.LineAlignment.Right => R.ConsoleLineAlignment.Right,
            _ => throw new InvalidDataException("The line alignment is unknown.")
        };
        return new R.ConsoleLine(
            line.LineId,
            line.Nodes.Select(FromProto),
            alignment,
            line.Temporary,
            line.NoWrap,
            line.LayoutWidth,
            line.LineHeight,
            string.IsNullOrEmpty(line.LogicalLineId) ? null : line.LogicalLineId,
            line.PhysicalIndex,
            line.IsLogicalStart);
    }

    public static W.ConsoleNode ToProto(R.ConsoleNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var result = new W.ConsoleNode();
        switch (node)
        {
            case R.TextNode text:
                result.Text = new W.TextNode { Text = text.Text, Style = ToProto(text.Style) };
                break;
            case R.LineBreakNode:
                result.LineBreak = new W.LineBreakNode();
                break;
            case R.ButtonNode button:
                result.Button = new W.ButtonNode
                {
                    Value = button.Value,
                    Tooltip = button.Tooltip ?? string.Empty,
                    Enabled = button.Enabled,
                    Generation = button.Generation,
                    PositionX = button.PositionX ?? 0,
                    HasPositionX = button.PositionX is not null
                };
                result.Button.Label.AddRange(button.Children.Select(ToProto));
                break;
            case R.PositionedInlineSegmentNode segment:
                result.PositionedInlineSegment = new W.PositionedInlineSegmentNode
                {
                    PositionX = segment.PositionX,
                    MeasuredWidth = segment.MeasuredWidth,
                    HasAction = segment.Action is not null
                };
                result.PositionedInlineSegment.Children.AddRange(segment.Children.Select(ToProto));
                if (segment.Action is { } action)
                {
                    result.PositionedInlineSegment.Action = new W.ConsoleInlineAction
                    {
                        Value = action.Value,
                        Tooltip = action.Tooltip ?? string.Empty,
                        Enabled = action.Enabled,
                        Generation = action.Generation
                    };
                }
                break;
            case R.ImageNode image:
                result.Image = new W.ImageNode
                {
                    AssetId = image.AssetId.Value,
                    AltText = image.AltText ?? string.Empty,
                    Decorative = image.Decorative,
                    ZIndex = image.ZIndex,
                    HasSourceRect = image.SourceRect is not null,
                    HasDestination = image.Destination is not null
                };
                if (image.SourceRect is { } source)
                    result.Image.SourceRect = ToProto(source);
                if (image.Destination is { } destination)
                    result.Image.Destination = ToProto(destination);
                break;
            case R.SpriteNode sprite:
                result.Sprite = new W.SpriteNode
                {
                    AssetId = sprite.AssetId.Value,
                    SourceRect = ToProto(sprite.SourceRect),
                    Destination = ToProto(sprite.Destination),
                    Frame = sprite.Frame,
                    ZIndex = sprite.ZIndex,
                    Opacity = sprite.Opacity,
                    AltText = sprite.AltText ?? string.Empty,
                    HasHover = sprite.HoverAssetId is not null,
                    HasMapping = sprite.MappingAssetId is not null
                };
                if (sprite.HoverAssetId is { } hoverAsset && sprite.HoverSourceRect is { } hoverRect)
                {
                    result.Sprite.HoverAssetId = hoverAsset.Value;
                    result.Sprite.HoverSourceRect = ToProto(hoverRect);
                }
                if (sprite.MappingAssetId is { } mappingAsset && sprite.MappingSourceRect is { } mappingRect)
                {
                    result.Sprite.MappingAssetId = mappingAsset.Value;
                    result.Sprite.MappingSourceRect = ToProto(mappingRect);
                }
                result.Sprite.AnimationFrames.AddRange(sprite.AnimationFrames.Select(frame => new W.SpriteAnimationFrame
                {
                    AssetId = frame.AssetId.Value,
                    SourceRect = ToProto(frame.SourceRect),
                    Offset = ToProto(frame.Offset),
                    DurationMilliseconds = frame.DurationMilliseconds
                }));
                break;
            case R.ShapeNode shape:
                result.Shape = new W.ShapeNode
                {
                    Shape = ToProto(shape.Shape),
                    Bounds = ToProto(shape.Bounds),
                    ZIndex = shape.ZIndex,
                    HasFill = shape.Fill is not null,
                    HasStroke = shape.Stroke is not null
                };
                if (shape.Fill is { } fill)
                    result.Shape.Fill = ToProto(fill);
                if (shape.Stroke is { } stroke)
                    result.Shape.Stroke = ToProto(stroke);
                result.Shape.HasButtonColor = shape.ButtonColor is not null;
                if (shape.ButtonColor is { } buttonColor)
                    result.Shape.ButtonColor = ToProto(buttonColor);
                result.Shape.Points.AddRange(shape.Points.Select(ToProto));
                break;
            case R.DivNode div:
                result.Div = new W.DivNode
                {
                    Bounds = ToProto(div.Bounds),
                    ZIndex = div.ZIndex,
                    IsRelative = div.IsRelative,
                    HasBackground = div.Background is not null,
                    HasBox = div.Box is not null
                };
                if (div.Background is { } background)
                    result.Div.Background = ToProto(background);
                if (div.Box is { } box)
                    result.Div.Box = ToProto(box);
                result.Div.Children.AddRange(div.Children.Select(ToProto));
                break;
            case R.HtmlIslandNode island:
                result.HtmlIsland = new W.HtmlIslandNode
                {
                    HasLayout = island.Layout is not null,
                    HasStructuredNodes = island.IsStructured
                };
                if (island.Layout is { } layout)
                    result.HtmlIsland.Layout = ToProto(layout);
                if (island.StructuredNodes is { } structuredNodes)
                    result.HtmlIsland.Nodes.AddRange(structuredNodes.Select(ToProto));
                else
                    result.HtmlIsland.Root = ToProtoHtml(island.Root!);
                break;
            default:
                throw new InvalidDataException("The runtime node is outside the structured protocol.");
        }

        return result;
    }

    public static R.ConsoleNode FromProto(W.ConsoleNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.KindCase switch
        {
            W.ConsoleNode.KindOneofCase.Text => new R.TextNode(node.Text.Text, FromProto(node.Text.Style)),
            W.ConsoleNode.KindOneofCase.LineBreak => R.LineBreakNode.Instance,
            W.ConsoleNode.KindOneofCase.Button => new R.ButtonNode(
                node.Button.Label.Select(FromProto),
                node.Button.Value,
                string.IsNullOrEmpty(node.Button.Tooltip) ? null : node.Button.Tooltip,
                node.Button.Enabled,
                node.Button.Generation,
                node.Button.HasPositionX ? node.Button.PositionX : null),
            W.ConsoleNode.KindOneofCase.PositionedInlineSegment => new R.PositionedInlineSegmentNode(
                node.PositionedInlineSegment.PositionX,
                node.PositionedInlineSegment.MeasuredWidth,
                node.PositionedInlineSegment.Children.Select(FromProto),
                node.PositionedInlineSegment.HasAction
                    ? new R.ConsoleInlineAction(
                        node.PositionedInlineSegment.Action.Value,
                        string.IsNullOrEmpty(node.PositionedInlineSegment.Action.Tooltip) ? null : node.PositionedInlineSegment.Action.Tooltip,
                        node.PositionedInlineSegment.Action.Enabled,
                        node.PositionedInlineSegment.Action.Generation)
                    : null),
            W.ConsoleNode.KindOneofCase.Image => new R.ImageNode(
                new R.ConsoleAssetId(node.Image.AssetId),
                node.Image.HasSourceRect ? FromProto(node.Image.SourceRect) : null,
                node.Image.HasDestination ? FromProto(node.Image.Destination) : null,
                string.IsNullOrEmpty(node.Image.AltText) ? null : node.Image.AltText,
                node.Image.Decorative,
                node.Image.ZIndex),
            W.ConsoleNode.KindOneofCase.Sprite => new R.SpriteNode(
                new R.ConsoleAssetId(node.Sprite.AssetId),
                FromProto(node.Sprite.SourceRect),
                FromProto(node.Sprite.Destination),
                node.Sprite.Frame,
                node.Sprite.ZIndex,
                node.Sprite.Opacity,
                string.IsNullOrEmpty(node.Sprite.AltText) ? null : node.Sprite.AltText,
                node.Sprite.HasHover ? new R.ConsoleAssetId(node.Sprite.HoverAssetId) : null,
                node.Sprite.HasHover ? FromProto(node.Sprite.HoverSourceRect) : null,
                node.Sprite.HasMapping ? new R.ConsoleAssetId(node.Sprite.MappingAssetId) : null,
                node.Sprite.HasMapping ? FromProto(node.Sprite.MappingSourceRect) : null,
                node.Sprite.AnimationFrames.Select(frame => new R.SpriteAnimationFrame(
                    new R.ConsoleAssetId(frame.AssetId),
                    FromProto(frame.SourceRect),
                    FromProto(frame.Offset),
                    frame.DurationMilliseconds))),
            W.ConsoleNode.KindOneofCase.Shape => new R.ShapeNode(
                FromProto(node.Shape.Shape),
                FromProto(node.Shape.Bounds),
                node.Shape.HasFill ? FromProto(node.Shape.Fill) : null,
                node.Shape.HasStroke ? FromProto(node.Shape.Stroke) : null,
                node.Shape.ZIndex,
                node.Shape.Points.Select(FromProto),
                node.Shape.HasButtonColor ? FromProto(node.Shape.ButtonColor) : null),
            W.ConsoleNode.KindOneofCase.HtmlIsland => node.HtmlIsland.HasStructuredNodes
                ? new R.HtmlIslandNode(
                    node.HtmlIsland.Nodes.Select(FromProto),
                    node.HtmlIsland.HasLayout ? FromProto(node.HtmlIsland.Layout) : null)
                : new R.HtmlIslandNode(
                    FromProtoHtml(node.HtmlIsland.Root),
                    node.HtmlIsland.HasLayout ? FromProto(node.HtmlIsland.Layout) : null),
            W.ConsoleNode.KindOneofCase.Div => new R.DivNode(
                node.Div.Children.Select(FromProto),
                FromProto(node.Div.Bounds),
                node.Div.ZIndex,
                node.Div.HasBackground ? FromProto(node.Div.Background) : null,
                node.Div.IsRelative,
                node.Div.HasBox ? FromProto(node.Div.Box) : null),
            _ => throw new InvalidDataException("The structured node has no known payload.")
        };
    }

    public static W.CanvasScene ToProto(R.CanvasScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var result = new W.CanvasScene();
        result.Drawables.AddRange(scene.Drawables.Select(ToProto));
        result.HitRegions.AddRange(scene.HitRegions.Select(ToProto));
        return result;
    }

    public static R.CanvasScene FromProto(W.CanvasScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return new R.CanvasScene(scene.Drawables.Select(FromProto), scene.HitRegions.Select(FromProto));
    }

    public static W.CanvasDrawable ToProto(R.CanvasDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        var result = new W.CanvasDrawable();
        switch (drawable)
        {
            case R.SpriteDrawable sprite:
                result.Sprite = new W.SpriteDrawable
                {
                    DrawableId = sprite.DrawableId,
                    AssetId = sprite.AssetId.Value,
                    SourceRect = ToProto(sprite.SourceRect),
                    Bounds = ToProto(sprite.Bounds),
                    ZIndex = sprite.ZIndex,
                    Opacity = sprite.Opacity,
                    Frame = sprite.Frame
                };
                result.Sprite.AnimationFrames.AddRange(sprite.AnimationFrames.Select(frame => new W.SpriteAnimationFrame
                {
                    AssetId = frame.AssetId.Value,
                    SourceRect = ToProto(frame.SourceRect),
                    Offset = ToProto(frame.Offset),
                    DurationMilliseconds = frame.DurationMilliseconds
                }));
                break;
            case R.ShapeDrawable shape:
                result.Shape = new W.ShapeDrawable
                {
                    DrawableId = shape.DrawableId,
                    Shape = ToProto(shape.Shape),
                    Bounds = ToProto(shape.Bounds),
                    ZIndex = shape.ZIndex,
                    Opacity = shape.Opacity,
                    HasFill = shape.Fill is not null,
                    HasStroke = shape.Stroke is not null
                };
                if (shape.Fill is { } fill)
                    result.Shape.Fill = ToProto(fill);
                if (shape.Stroke is { } stroke)
                    result.Shape.Stroke = ToProto(stroke);
                result.Shape.Points.AddRange(shape.Points.Select(ToProto));
                break;
            case R.HtmlIslandDrawable island:
                result.HtmlIsland = new W.HtmlIslandDrawable
                {
                    DrawableId = island.DrawableId,
                    Bounds = ToProto(island.Bounds),
                    ZIndex = island.ZIndex,
                    Opacity = island.Opacity,
                    HasStructuredNodes = island.IsStructured
                };
                if (island.StructuredNodes is { } structuredNodes)
                    result.HtmlIsland.Nodes.AddRange(structuredNodes.Select(ToProto));
                else
                    result.HtmlIsland.Root = ToProtoHtml(island.Root!);
                break;
            case R.RasterDrawable raster:
                result.Raster = new W.RasterDrawable
                {
                    DrawableId = raster.DrawableId,
                    PngData = Google.Protobuf.ByteString.CopyFrom(raster.PngData.ToArray()),
                    Bounds = ToProto(raster.Bounds),
                    ZIndex = raster.ZIndex,
                    Opacity = raster.Opacity,
                    HasHover = raster.HoverPngData is not null,
                    HitTestMap = raster.HitTestMap
                };
                if (raster.HoverPngData is { } hoverPngData)
                    result.Raster.HoverPngData = Google.Protobuf.ByteString.CopyFrom(hoverPngData.ToArray());
                break;
            default:
                throw new InvalidDataException("The runtime drawable is outside the structured protocol.");
        }

        return result;
    }

    public static R.CanvasDrawable FromProto(W.CanvasDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        return drawable.KindCase switch
        {
            W.CanvasDrawable.KindOneofCase.Sprite => new R.SpriteDrawable(
                drawable.Sprite.DrawableId,
                new R.ConsoleAssetId(drawable.Sprite.AssetId),
                FromProto(drawable.Sprite.SourceRect),
                FromProto(drawable.Sprite.Bounds),
                drawable.Sprite.ZIndex,
                drawable.Sprite.Opacity,
                drawable.Sprite.Frame,
                drawable.Sprite.AnimationFrames.Select(frame => new R.SpriteAnimationFrame(
                    new R.ConsoleAssetId(frame.AssetId),
                    FromProto(frame.SourceRect),
                    FromProto(frame.Offset),
                    frame.DurationMilliseconds))),
            W.CanvasDrawable.KindOneofCase.Shape => new R.ShapeDrawable(
                drawable.Shape.DrawableId,
                FromProto(drawable.Shape.Shape),
                FromProto(drawable.Shape.Bounds),
                drawable.Shape.HasFill ? FromProto(drawable.Shape.Fill) : null,
                drawable.Shape.HasStroke ? FromProto(drawable.Shape.Stroke) : null,
                drawable.Shape.ZIndex,
                drawable.Shape.Opacity,
                drawable.Shape.Points.Select(FromProto)),
            W.CanvasDrawable.KindOneofCase.HtmlIsland => drawable.HtmlIsland.HasStructuredNodes
                ? new R.HtmlIslandDrawable(
                    drawable.HtmlIsland.DrawableId,
                    drawable.HtmlIsland.Nodes.Select(FromProto),
                    FromProto(drawable.HtmlIsland.Bounds),
                    drawable.HtmlIsland.ZIndex,
                    drawable.HtmlIsland.Opacity)
                : new R.HtmlIslandDrawable(
                    drawable.HtmlIsland.DrawableId,
                    FromProtoHtml(drawable.HtmlIsland.Root),
                    FromProto(drawable.HtmlIsland.Bounds),
                    drawable.HtmlIsland.ZIndex,
                    drawable.HtmlIsland.Opacity),
            W.CanvasDrawable.KindOneofCase.Raster => new R.RasterDrawable(
                drawable.Raster.DrawableId,
                drawable.Raster.PngData.ToByteArray(),
                FromProto(drawable.Raster.Bounds),
                drawable.Raster.ZIndex,
                drawable.Raster.Opacity,
                drawable.Raster.HasHover ? drawable.Raster.HoverPngData.ToByteArray() : null,
                drawable.Raster.HitTestMap),
            _ => throw new InvalidDataException("The structured drawable has no known payload.")
        };
    }

    public static W.BackgroundLayer ToProto(R.BackgroundLayer layer) => new()
    {
        LayerId = layer.LayerId,
        AssetId = layer.AssetId.Value,
        Mode = layer.Mode switch
        {
            R.ConsoleBackgroundMode.Stretch => W.BackgroundMode.Stretch,
            R.ConsoleBackgroundMode.Contain => W.BackgroundMode.Contain,
            R.ConsoleBackgroundMode.Cover => W.BackgroundMode.Cover,
            R.ConsoleBackgroundMode.Center => W.BackgroundMode.Center,
            R.ConsoleBackgroundMode.Repeat => W.BackgroundMode.Repeat,
            _ => throw new InvalidDataException("The background mode is unknown.")
        },
        Opacity = layer.Opacity,
        Depth = layer.Depth
    };

    public static R.BackgroundLayer FromProto(W.BackgroundLayer layer) => new(
        layer.LayerId,
        new R.ConsoleAssetId(layer.AssetId),
        layer.Mode switch
        {
            W.BackgroundMode.Stretch => R.ConsoleBackgroundMode.Stretch,
            W.BackgroundMode.Contain => R.ConsoleBackgroundMode.Contain,
            W.BackgroundMode.Cover => R.ConsoleBackgroundMode.Cover,
            W.BackgroundMode.Center => R.ConsoleBackgroundMode.Center,
            W.BackgroundMode.Repeat => R.ConsoleBackgroundMode.Repeat,
            _ => throw new InvalidDataException("The background mode is unknown.")
        },
        layer.Opacity,
        layer.Depth);

    public static W.HitRegion ToProto(R.HitRegion region) => new()
    {
        RegionId = region.RegionId,
        Bounds = ToProto(region.Bounds),
        InputValue = region.InputValue,
        Enabled = region.Enabled,
        Tooltip = region.Tooltip ?? string.Empty
    };

    public static R.HitRegion FromProto(W.HitRegion region) => new(
        region.RegionId,
        FromProto(region.Bounds),
        region.InputValue,
        region.Enabled,
        string.IsNullOrEmpty(region.Tooltip) ? null : region.Tooltip);

    public static W.MediaState ToProto(R.MediaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var result = new W.MediaState();
        result.Channels.AddRange(state.Channels.Select(ToProto));
        return result;
    }

    public static R.MediaState FromProto(W.MediaState state) => new(state.Channels.Select(FromProto));

    public static W.MediaChannelState ToProto(R.MediaChannelState channel) => new()
    {
        Channel = channel.Channel,
        AssetId = channel.AssetId?.Value ?? string.Empty,
        HasAssetId = channel.AssetId is not null,
        PlaybackState = channel.PlaybackState switch
        {
            R.ConsoleMediaPlaybackState.Stopped => W.MediaPlaybackState.Stopped,
            R.ConsoleMediaPlaybackState.Requested => W.MediaPlaybackState.Requested,
            _ => throw new InvalidDataException("The media playback state is unknown.")
        },
        Loop = channel.Loop,
        Volume = channel.Volume,
        Revision = channel.Revision,
        StartPolicy = channel.StartPolicy switch
        {
            R.ConsoleMediaStartPolicy.Immediate => W.MediaStartPolicy.Immediate,
            R.ConsoleMediaStartPolicy.OnUserGesture => W.MediaStartPolicy.OnUserGesture,
            _ => throw new InvalidDataException("The media start policy is unknown.")
        }
    };

    public static R.MediaChannelState FromProto(W.MediaChannelState channel) => new(
        channel.Channel,
        channel.HasAssetId ? new R.ConsoleAssetId(channel.AssetId) : null,
        channel.PlaybackState switch
        {
            W.MediaPlaybackState.Stopped => R.ConsoleMediaPlaybackState.Stopped,
            W.MediaPlaybackState.Requested => R.ConsoleMediaPlaybackState.Requested,
            _ => throw new InvalidDataException("The media playback state is unknown.")
        },
        channel.Loop,
        channel.Volume,
        channel.Revision,
        channel.StartPolicy switch
        {
            W.MediaStartPolicy.Immediate => R.ConsoleMediaStartPolicy.Immediate,
            W.MediaStartPolicy.OnUserGesture => R.ConsoleMediaStartPolicy.OnUserGesture,
            _ => throw new InvalidDataException("The media start policy is unknown.")
        });

    public static W.WindowMetadata ToProto(R.WindowMetadata metadata)
    {
        var result = new W.WindowMetadata
        {
            Title = metadata.Title,
            ViewportWidth = metadata.ViewportWidth,
            ViewportHeight = metadata.ViewportHeight,
            DefaultFont = ToProtoFont(metadata.DefaultFont),
            FontFaceId = metadata.FontFaceId,
            WebFontAssetDigest = metadata.WebFontAssetDigest,
            HasDefaultForeground = metadata.DefaultForeground is not null,
            HasDefaultBackground = metadata.DefaultBackground is not null
        };
        if (metadata.DefaultForeground is { } foreground)
            result.DefaultForeground = ToProto(foreground);
        if (metadata.DefaultBackground is { } background)
            result.DefaultBackground = ToProto(background);
        return result;
    }

    public static R.WindowMetadata FromProto(W.WindowMetadata metadata) => new(
        metadata.Title,
        metadata.ViewportWidth,
        metadata.ViewportHeight,
        metadata.HasDefaultForeground ? FromProto(metadata.DefaultForeground) : null,
        metadata.HasDefaultBackground ? FromProto(metadata.DefaultBackground) : null,
        metadata.DefaultFont is null ? null : FromProtoFont(metadata.DefaultFont),
        string.IsNullOrEmpty(metadata.FontFaceId) ? "default" : metadata.FontFaceId,
        metadata.WebFontAssetDigest);

    public static W.TruncationMetadata ToProto(R.ConsoleTruncationMetadata metadata) => new()
    {
        WasTruncated = metadata.WasTruncated,
        DroppedNodeCount = metadata.DroppedNodeCount,
        DroppedLineCount = metadata.DroppedLineCount,
        DroppedTextLength = metadata.DroppedTextLength
    };

    public static R.ConsoleTruncationMetadata FromProto(W.TruncationMetadata metadata) => new(
        metadata.WasTruncated,
        metadata.DroppedNodeCount,
        metadata.DroppedLineCount,
        metadata.DroppedTextLength);

    public static W.ConsolePrompt ToProto(R.ConsolePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var result = new W.ConsolePrompt
        {
            PromptId = prompt.PromptId,
            InputType = ToProto(prompt.InputType),
            PromptText = prompt.PromptText ?? string.Empty,
            DefaultValue = prompt.DefaultValue ?? string.Empty,
            HasDefaultValue = prompt.DefaultValue is not null,
            Constraints = ToProto(prompt.Constraints),
            OneInput = prompt.OneInput,
            SystemInput = prompt.SystemInput,
            StopMessageSkip = prompt.StopMessageSkip,
            AllowedSources = (W.InputSource)(int)prompt.AllowedSources,
            OpenedAtUnixMilliseconds = prompt.OpenedAtUnixMilliseconds,
            DeadlineUnixMilliseconds = prompt.DeadlineUnixMilliseconds,
            HasDeadline = prompt.HasDeadline,
            DisplayTime = prompt.DisplayTime,
            TimeoutMessage = prompt.TimeoutMessage ?? string.Empty,
            TimeoutAction = ToProto(prompt.TimeoutAction)
        };
        return result;
    }

    public static R.ConsolePrompt FromProto(W.ConsolePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        R.ConsolePromptTimeoutAction timeoutAction = prompt.TimeoutAction switch
        {
            W.TimeoutAction.ReturnDefault => R.ConsolePromptTimeoutAction.ReturnDefaultValue,
            W.TimeoutAction.ContinueWithoutValue => R.ConsolePromptTimeoutAction.ContinueWithoutValue,
            W.TimeoutAction.CancelRuntime => R.ConsolePromptTimeoutAction.CancelRuntime,
            _ => throw new InvalidDataException("The prompt timeout action is unknown.")
        };
        long deadline = prompt.HasDeadline ? prompt.DeadlineUnixMilliseconds : 0;
        TimeSpan? timeout = prompt.HasDeadline && deadline >= prompt.OpenedAtUnixMilliseconds && deadline > 0
            ? TimeSpan.FromMilliseconds(deadline - prompt.OpenedAtUnixMilliseconds)
            : null;
        R.ConsolePromptTimeoutBehavior behavior = timeoutAction switch
        {
            R.ConsolePromptTimeoutAction.ReturnDefaultValue => R.ConsolePromptTimeoutBehavior.ReturnDefaultValue,
            R.ConsolePromptTimeoutAction.ContinueWithoutValue => R.ConsolePromptTimeoutBehavior.ContinueWithoutValue,
            _ => R.ConsolePromptTimeoutBehavior.Cancel
        };
        return new R.ConsolePrompt(
            prompt.PromptId,
            FromProto(prompt.InputType),
            string.IsNullOrEmpty(prompt.PromptText) ? null : prompt.PromptText,
            prompt.HasDefaultValue ? prompt.DefaultValue : null,
            FromProto(prompt.Constraints),
            timeout,
            behavior,
            prompt.OneInput,
            prompt.SystemInput,
            prompt.StopMessageSkip,
            prompt.DisplayTime,
            string.IsNullOrEmpty(prompt.TimeoutMessage) ? null : prompt.TimeoutMessage,
            timeoutAction,
            (R.ConsoleInputSource)(int)prompt.AllowedSources,
            prompt.OpenedAtUnixMilliseconds,
            deadline);
    }

    private static W.InputConstraints ToProto(R.ConsoleInputConstraints constraints)
    {
        var result = new W.InputConstraints();
        switch (constraints)
        {
            case R.TextInputConstraints text:
                result.Text = new W.TextInputConstraints
                {
                    HasMaxLength = text.MaxLength is not null,
                    MaxLength = text.MaxLength ?? 0,
                    AllowControlCharacters = text.AllowControlCharacters
                };
                break;
            case R.IntegerInputConstraints integer:
                result.Integer = new W.IntegerInputConstraints
                {
                    HasMinimum = integer.Minimum is not null,
                    Minimum = integer.Minimum ?? 0,
                    HasMaximum = integer.Maximum is not null,
                    Maximum = integer.Maximum ?? 0,
                    AllowSign = integer.AllowSign
                };
                break;
            case R.AnyValueInputConstraints any:
                result.AnyValue = new W.AnyValueInputConstraints
                {
                    HasMaxLength = any.MaxLength is not null,
                    MaxLength = any.MaxLength ?? 0
                };
                break;
            default:
                throw new InvalidDataException("The input constraint is outside the structured protocol.");
        }

        return result;
    }

    private static R.ConsoleInputConstraints FromProto(W.InputConstraints constraints) => constraints.KindCase switch
    {
        W.InputConstraints.KindOneofCase.Text => new R.TextInputConstraints(
            constraints.Text.HasMaxLength ? constraints.Text.MaxLength : null,
            constraints.Text.AllowControlCharacters),
        W.InputConstraints.KindOneofCase.Integer => new R.IntegerInputConstraints(
            constraints.Integer.HasMinimum ? constraints.Integer.Minimum : null,
            constraints.Integer.HasMaximum ? constraints.Integer.Maximum : null,
            constraints.Integer.AllowSign),
        W.InputConstraints.KindOneofCase.AnyValue => new R.AnyValueInputConstraints(
            constraints.AnyValue.HasMaxLength ? constraints.AnyValue.MaxLength : null),
        _ => throw new InvalidDataException("The input constraint has no known payload.")
    };

    private static W.HtmlNode ToProtoHtml(R.ConsoleHtmlNode node) => node switch
    {
        R.ConsoleHtmlTextNode text => new W.HtmlNode { Text = text.Text },
        R.ConsoleHtmlBreakNode => new W.HtmlNode { BreakNode = new W.HtmlBreak() },
        R.ConsoleHtmlElementNode element => ToProtoHtmlElement(element),
        _ => throw new InvalidDataException("The HTML node is outside the structured protocol.")
    };

    private static W.HtmlNode ToProtoHtmlElement(R.ConsoleHtmlElementNode element)
    {
        var result = new W.HtmlNode
        {
            Element = new W.HtmlElement
            {
                Tag = element.Tag,
                Style = ToProto(element.Style),
                AssetId = element.AssetId ?? string.Empty,
                HasAssetId = element.AssetId is not null,
                AltText = element.AltText ?? string.Empty
            }
        };
        result.Element.Children.AddRange(element.Children.Select(ToProtoHtml));
        return result;
    }

    private static R.ConsoleHtmlNode FromProtoHtml(W.HtmlNode node) => node.KindCase switch
    {
        W.HtmlNode.KindOneofCase.Text => new R.ConsoleHtmlTextNode(node.Text),
        W.HtmlNode.KindOneofCase.BreakNode => R.ConsoleHtmlBreakNode.Instance,
        W.HtmlNode.KindOneofCase.Element => new R.ConsoleHtmlElementNode(
            node.Element.Tag,
            node.Element.Children.Select(FromProtoHtml),
            FromProto(node.Element.Style),
            node.Element.HasAssetId ? node.Element.AssetId : null,
            string.IsNullOrEmpty(node.Element.AltText) ? null : node.Element.AltText),
        _ => throw new InvalidDataException("The HTML node has no known payload.")
    };

    private static W.TextStyle ToProto(R.ConsoleTextStyle style)
    {
        var result = new W.TextStyle
        {
            Decorations = (uint)style.Decorations,
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            LineHeight = style.LineHeight,
            HasForeground = style.Foreground is not null,
            HasBackground = style.Background is not null,
            HasButtonColor = style.ButtonColor is not null
        };
        if (style.Foreground is { } foreground)
            result.Foreground = ToProto(foreground);
        if (style.Background is { } background)
            result.Background = ToProto(background);
        if (style.ButtonColor is { } buttonColor)
            result.ButtonColor = ToProto(buttonColor);
        return result;
    }

    private static R.ConsoleTextStyle FromProto(W.TextStyle style) => new(
        style.HasForeground ? FromProto(style.Foreground) : null,
        style.HasBackground ? FromProto(style.Background) : null,
        (R.ConsoleFontStyle)style.Decorations,
        string.IsNullOrEmpty(style.FontFamily) ? "default" : style.FontFamily,
        style.FontSize == 0 ? 16 : style.FontSize,
        style.LineHeight,
        style.HasButtonColor ? FromProto(style.ButtonColor) : null);

    private static W.TextStyle ToProtoFont(R.ConsoleFontSpec font) => new()
    {
        FontFamily = font.Family,
        FontSize = font.Size,
        LineHeight = font.LineHeight
    };

    private static R.ConsoleFontSpec FromProtoFont(W.TextStyle style) => new(
        string.IsNullOrEmpty(style.FontFamily) ? "default" : style.FontFamily,
        style.FontSize == 0 ? 16 : style.FontSize,
        style.LineHeight);

    private static W.ConsoleColor ToProto(R.ConsoleColor color) => new()
    {
        Red = color.Red,
        Green = color.Green,
        Blue = color.Blue,
        Alpha = color.Alpha
    };

    private static R.ConsoleColor FromProto(W.ConsoleColor color) => new(
        checked((byte)color.Red),
        checked((byte)color.Green),
        checked((byte)color.Blue),
        checked((byte)color.Alpha));

    private static W.Rect ToProto(R.ConsoleRect rect) => new()
    {
        X = rect.X,
        Y = rect.Y,
        Width = rect.Width,
        Height = rect.Height
    };

    private static R.ConsoleRect FromProto(W.Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static W.Point ToProto(R.ConsolePoint point) => new() { X = point.X, Y = point.Y };

    private static R.ConsolePoint FromProto(W.Point point) => new(point.X, point.Y);

    private static W.BoxModel ToProto(R.ConsoleBoxModel box)
    {
        var result = new W.BoxModel
        {
            Margin = ToProto(box.Margin),
            Padding = ToProto(box.Padding),
            Border = ToProto(box.Border),
            Radius = ToProto(box.Radius)
        };
        for (int index = 0; index < 4; index++)
        {
            if (box.BorderColors[index] is { } color)
            {
                result.BorderColors.Add(ToProto(color));
                result.BorderColorMask |= 1u << index;
            }
            else
            {
                result.BorderColors.Add(new W.ConsoleColor());
            }
        }
        return result;
    }

    private static R.ConsoleBoxModel FromProto(W.BoxModel box) => new(
        FromProto(box.Margin),
        FromProto(box.Padding),
        FromProto(box.Border),
        FromProto(box.Radius),
        Enumerable.Range(0, 4).Select(index =>
            index < box.BorderColors.Count && (box.BorderColorMask & (1u << index)) != 0
                ? (R.ConsoleColor?)FromProto(box.BorderColors[index])
                : null));

    private static W.Insets ToProto(R.ConsoleInsets insets) => new()
    {
        Top = insets.Top,
        Right = insets.Right,
        Bottom = insets.Bottom,
        Left = insets.Left
    };

    private static R.ConsoleInsets FromProto(W.Insets insets) => new(insets.Top, insets.Right, insets.Bottom, insets.Left);

    private static W.InputType ToProto(R.ConsoleInputType inputType) => inputType switch
    {
        R.ConsoleInputType.EnterKey => W.InputType.EnterKey,
        R.ConsoleInputType.AnyKey => W.InputType.AnyKey,
        R.ConsoleInputType.Integer => W.InputType.Integer,
        R.ConsoleInputType.Text => W.InputType.Text,
        R.ConsoleInputType.AnyValue => W.InputType.AnyValue,
        R.ConsoleInputType.IntegerButton => W.InputType.IntegerButton,
        R.ConsoleInputType.TextButton => W.InputType.TextButton,
        R.ConsoleInputType.PrimitivePointerKey => W.InputType.PrimitivePointerKey,
        R.ConsoleInputType.WaitOnly => W.InputType.WaitOnly,
        _ => throw new InvalidDataException("The input type is unknown.")
    };

    private static R.ConsoleInputType FromProto(W.InputType inputType) => inputType switch
    {
        W.InputType.EnterKey => R.ConsoleInputType.EnterKey,
        W.InputType.AnyKey => R.ConsoleInputType.AnyKey,
        W.InputType.Integer => R.ConsoleInputType.Integer,
        W.InputType.Text => R.ConsoleInputType.Text,
        W.InputType.AnyValue => R.ConsoleInputType.AnyValue,
        W.InputType.IntegerButton => R.ConsoleInputType.IntegerButton,
        W.InputType.TextButton => R.ConsoleInputType.TextButton,
        W.InputType.PrimitivePointerKey => R.ConsoleInputType.PrimitivePointerKey,
        W.InputType.WaitOnly => R.ConsoleInputType.WaitOnly,
        _ => throw new InvalidDataException("The input type is unknown.")
    };

    private static W.TimeoutAction ToProto(R.ConsolePromptTimeoutAction action) => action switch
    {
        R.ConsolePromptTimeoutAction.ReturnDefaultValue => W.TimeoutAction.ReturnDefault,
        R.ConsolePromptTimeoutAction.ContinueWithoutValue => W.TimeoutAction.ContinueWithoutValue,
        R.ConsolePromptTimeoutAction.CancelRuntime => W.TimeoutAction.CancelRuntime,
        _ => throw new InvalidDataException("The timeout action is unknown.")
    };

    private static W.PromptCloseReason ToProto(R.ConsolePromptCloseReason reason) => reason switch
    {
        R.ConsolePromptCloseReason.Completed => W.PromptCloseReason.Completed,
        R.ConsolePromptCloseReason.InputAccepted => W.PromptCloseReason.InputAccepted,
        R.ConsolePromptCloseReason.Cancelled => W.PromptCloseReason.Cancelled,
        R.ConsolePromptCloseReason.TimedOut => W.PromptCloseReason.TimedOut,
        R.ConsolePromptCloseReason.Explicit => W.PromptCloseReason.Explicit,
        _ => throw new InvalidDataException("The prompt close reason is unknown.")
    };

    private static R.ConsolePromptCloseReason FromProto(W.PromptCloseReason reason) => reason switch
    {
        W.PromptCloseReason.Completed => R.ConsolePromptCloseReason.Completed,
        W.PromptCloseReason.InputAccepted => R.ConsolePromptCloseReason.InputAccepted,
        W.PromptCloseReason.Cancelled => R.ConsolePromptCloseReason.Cancelled,
        W.PromptCloseReason.TimedOut => R.ConsolePromptCloseReason.TimedOut,
        W.PromptCloseReason.Explicit => R.ConsolePromptCloseReason.Explicit,
        _ => throw new InvalidDataException("The prompt close reason is unknown.")
    };

    private static W.LineAlignment ToProto(R.ConsoleLineAlignment alignment) => alignment switch
    {
        R.ConsoleLineAlignment.Left => W.LineAlignment.Left,
        R.ConsoleLineAlignment.Center => W.LineAlignment.Center,
        R.ConsoleLineAlignment.Right => W.LineAlignment.Right,
        _ => throw new InvalidDataException("The line alignment is unknown.")
    };

    private static W.BackgroundMode ToProto(R.ConsoleBackgroundMode mode) => mode switch
    {
        R.ConsoleBackgroundMode.Stretch => W.BackgroundMode.Stretch,
        R.ConsoleBackgroundMode.Contain => W.BackgroundMode.Contain,
        R.ConsoleBackgroundMode.Cover => W.BackgroundMode.Cover,
        R.ConsoleBackgroundMode.Center => W.BackgroundMode.Center,
        R.ConsoleBackgroundMode.Repeat => W.BackgroundMode.Repeat,
        _ => throw new InvalidDataException("The background mode is unknown.")
    };

    private static R.ConsoleBackgroundMode FromProto(W.BackgroundMode mode) => mode switch
    {
        W.BackgroundMode.Stretch => R.ConsoleBackgroundMode.Stretch,
        W.BackgroundMode.Contain => R.ConsoleBackgroundMode.Contain,
        W.BackgroundMode.Cover => R.ConsoleBackgroundMode.Cover,
        W.BackgroundMode.Center => R.ConsoleBackgroundMode.Center,
        W.BackgroundMode.Repeat => R.ConsoleBackgroundMode.Repeat,
        _ => throw new InvalidDataException("The background mode is unknown.")
    };

    private static W.ShapeKind ToProto(R.ConsoleShapeKind shape) => shape switch
    {
        R.ConsoleShapeKind.Rectangle => W.ShapeKind.Rectangle,
        R.ConsoleShapeKind.Ellipse => W.ShapeKind.Ellipse,
        R.ConsoleShapeKind.Line => W.ShapeKind.Line,
        R.ConsoleShapeKind.Polygon => W.ShapeKind.Polygon,
        R.ConsoleShapeKind.Space => W.ShapeKind.Space,
        _ => throw new InvalidDataException("The shape kind is unknown.")
    };

    private static R.ConsoleShapeKind FromProto(W.ShapeKind shape) => shape switch
    {
        W.ShapeKind.Rectangle => R.ConsoleShapeKind.Rectangle,
        W.ShapeKind.Ellipse => R.ConsoleShapeKind.Ellipse,
        W.ShapeKind.Line => R.ConsoleShapeKind.Line,
        W.ShapeKind.Polygon => R.ConsoleShapeKind.Polygon,
        W.ShapeKind.Space => R.ConsoleShapeKind.Space,
        _ => throw new InvalidDataException("The shape kind is unknown.")
    };
}

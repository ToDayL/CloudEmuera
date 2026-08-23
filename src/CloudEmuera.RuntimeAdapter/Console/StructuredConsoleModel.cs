namespace CloudEmuera.RuntimeAdapter;

/// <summary>Logical pixels used by the structured runtime contract.</summary>
public readonly record struct ConsolePoint
{
    public ConsolePoint(int x, int y)
    {
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(y, nameof(y));
        X = x;
        Y = y;
    }

    public int X { get; }

    public int Y { get; }

    private static void ValidateCoordinate(int value, string parameterName)
    {
        if (value is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A logical coordinate is outside its limit.", parameterName);
    }
}

public readonly record struct ConsoleSize
{
    public ConsoleSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 8_192 || height > 8_192)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A logical size is outside its limit.");
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

public readonly record struct ConsoleRect
{
    public ConsoleRect(int x, int y, int width, int height)
    {
        _ = new ConsolePoint(x, y);
        if (width <= 0 || height <= 0 || width > 8_192 || height > 8_192)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A rectangle size is outside its limit.");
        if ((long)x + width is < -1_000_000 or > 1_000_000 || (long)y + height is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A rectangle is outside its coordinate limit.");
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }
}

public enum ConsoleLineAlignment
{
    Left,
    Center,
    Right
}

public enum ConsoleBackgroundMode
{
    Stretch,
    Contain,
    Cover,
    Center,
    Repeat
}

public enum ConsoleShapeKind
{
    Rectangle,
    Ellipse,
    Line,
    Polygon,
    Space
}

public enum ConsoleMediaPlaybackState
{
    Stopped,
    Requested
}

public enum ConsoleMediaStartPolicy
{
    Immediate,
    OnUserGesture
}

/// <summary>Closed font information; it is a logical manifest family, never a host font name.</summary>
public sealed record ConsoleFontSpec
{
    public ConsoleFontSpec(string family = "default", int size = 16, int lineHeight = 0)
    {
        ConsoleContractValidation.ValidateLogicalName(family, nameof(family), 128);
        if (size is <= 0 or > 256)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidFont, "Font size is outside its limit.", nameof(size));
        if (lineHeight < 0 || lineHeight > 512)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidFont, "Line height is outside its limit.", nameof(lineHeight));
        Family = family;
        Size = size;
        LineHeight = lineHeight;
    }

    public string Family { get; }

    public int Size { get; }

    public int LineHeight { get; }
}

public sealed class ConsoleLine
{
    public ConsoleLine(
        string lineId,
        IEnumerable<ConsoleNode> nodes,
        ConsoleLineAlignment alignment = ConsoleLineAlignment.Left,
        bool temporary = false,
        bool noWrap = false,
        int layoutWidth = 0,
        int lineHeight = 0,
        string? logicalLineId = null,
        int physicalIndex = 0,
        bool isLogicalStart = true)
    {
        ConsoleContractValidation.ValidateIdentifier(lineId, nameof(lineId), ConsoleContractLimits.Default.MaxLineIdLength);
        ArgumentNullException.ThrowIfNull(nodes);
        ConsoleNode[] copy = nodes.ToArray();
        if (copy.Length > ConsoleContractLimits.Default.MaxNodesPerLine)
            throw new ConsoleContractException(ConsoleContractViolationReason.LineTooLarge, "A console line has too many nodes.");
        ConsoleNodeValidation.ValidateBatchIfNotEmpty(copy, ConsoleContractLimits.Default);
        ValidateAlignment(alignment);
        if (layoutWidth < 0 || layoutWidth > 8_192 || lineHeight < 0 || lineHeight > 512)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A physical line size is outside its limit.");
        if (layoutWidth > 0 && (copy.Length > ConsoleContractLimits.Default.MaxSegmentsPerPhysicalLine ||
            copy.Any(node => node is not PositionedInlineSegmentNode and not LineBreakNode)))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A physical line must contain positioned inline segments.");
        if (physicalIndex < 0 || physicalIndex > ConsoleContractLimits.Default.MaxPhysicalLineIndex)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A physical line index is outside its limit.");
        logicalLineId ??= lineId;
        ConsoleContractValidation.ValidateIdentifier(logicalLineId, nameof(logicalLineId), ConsoleContractLimits.Default.MaxLineIdLength);
        LineId = lineId;
        Nodes = Array.AsReadOnly(copy);
        Alignment = alignment;
        Temporary = temporary;
        NoWrap = noWrap;
        LayoutWidth = layoutWidth;
        LineHeight = lineHeight;
        LogicalLineId = logicalLineId;
        PhysicalIndex = physicalIndex;
        IsLogicalStart = isLogicalStart;
    }

    public string LineId { get; }

    public IReadOnlyList<ConsoleNode> Nodes { get; }

    public ConsoleLineAlignment Alignment { get; }

    public bool Temporary { get; }

    /// <summary>Preserves Emuera's line-head &lt;nobr&gt; layout semantic.</summary>
    public bool NoWrap { get; }

    /// <summary>Authoritative physical layout width in runtime pixels.</summary>
    public int LayoutWidth { get; }

    /// <summary>Authoritative physical line height in runtime pixels.</summary>
    public int LineHeight { get; }

    /// <summary>The logical line group that owns this physical line.</summary>
    public string LogicalLineId { get; }

    public int PhysicalIndex { get; }

    public bool IsLogicalStart { get; }

    public ConsoleLine WithNodes(
        IEnumerable<ConsoleNode> nodes,
        bool? temporary = null,
        ConsoleLineAlignment? alignment = null,
        bool? noWrap = null) =>
        new(LineId, nodes, alignment ?? Alignment, temporary ?? Temporary, noWrap ?? NoWrap,
            LayoutWidth, LineHeight, LogicalLineId, PhysicalIndex, IsLogicalStart);

    private static void ValidateAlignment(ConsoleLineAlignment alignment)
    {
        if (alignment is not ConsoleLineAlignment.Left and not ConsoleLineAlignment.Center and not ConsoleLineAlignment.Right)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidAlignment, "Unknown line alignment.");
    }
}

public sealed class BackgroundLayer
{
    public BackgroundLayer(
        string layerId,
        ConsoleAssetId assetId,
        ConsoleBackgroundMode mode = ConsoleBackgroundMode.Cover,
        float opacity = 1f,
        long depth = 0)
    {
        ConsoleContractValidation.ValidateIdentifier(layerId, nameof(layerId), ConsoleContractLimits.Default.MaxLayerIdLength);
        assetId.Validate(ConsoleContractLimits.Default);
        ValidateOpacity(opacity);
        if (depth is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Background depth is outside its limit.");
        if (mode is not ConsoleBackgroundMode.Stretch and not ConsoleBackgroundMode.Contain and not ConsoleBackgroundMode.Cover and
            not ConsoleBackgroundMode.Center and not ConsoleBackgroundMode.Repeat)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidBackgroundMode, "Unknown background mode.");
        LayerId = layerId;
        AssetId = assetId;
        Mode = mode;
        Opacity = opacity;
        Depth = depth;
    }

    public string LayerId { get; }

    public ConsoleAssetId AssetId { get; }

    public ConsoleBackgroundMode Mode { get; }

    public float Opacity { get; }

    public long Depth { get; }

    internal static void ValidateOpacity(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value is < 0f or > 1f)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidOpacity, "Opacity must be a finite value from 0 to 1.");
    }
}

public abstract class CanvasDrawable
{
    private protected CanvasDrawable(string drawableId, ConsoleRect bounds, int zIndex, float opacity)
    {
        ConsoleContractValidation.ValidateIdentifier(drawableId, nameof(drawableId), ConsoleContractLimits.Default.MaxDrawableIdLength);
        BackgroundLayer.ValidateOpacity(opacity);
        if (zIndex is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Drawable z-index is outside its limit.");
        DrawableId = drawableId;
        Bounds = bounds;
        ZIndex = zIndex;
        Opacity = opacity;
    }

    public string DrawableId { get; }

    public ConsoleRect Bounds { get; }

    public int ZIndex { get; }

    public float Opacity { get; }
}

public sealed class SpriteDrawable : CanvasDrawable
{
    public SpriteDrawable(
        string drawableId,
        ConsoleAssetId assetId,
        ConsoleRect sourceRect,
        ConsoleRect bounds,
        int zIndex = 0,
        float opacity = 1f,
        int frame = 0,
        IEnumerable<SpriteAnimationFrame>? animationFrames = null)
        : base(drawableId, bounds, zIndex, opacity)
    {
        assetId.Validate(ConsoleContractLimits.Default);
        if (frame < 0 || frame > ConsoleContractLimits.Default.MaxSpriteFrames)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite frame is outside its limit.");
        SpriteAnimationFrame[] frameCopy = (animationFrames ?? Array.Empty<SpriteAnimationFrame>()).ToArray();
        if (frameCopy.Length > ConsoleContractLimits.Default.MaxSpriteFrames)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite animation has too many frames.");
        AssetId = assetId;
        SourceRect = sourceRect;
        Frame = frame;
        AnimationFrames = Array.AsReadOnly(frameCopy);
    }

    public ConsoleAssetId AssetId { get; }

    public ConsoleRect SourceRect { get; }

    public int Frame { get; }

    public IReadOnlyList<SpriteAnimationFrame> AnimationFrames { get; }
}

public sealed class ShapeDrawable : CanvasDrawable
{
    public ShapeDrawable(
        string drawableId,
        ConsoleShapeKind shape,
        ConsoleRect bounds,
        ConsoleColor? fill = null,
        ConsoleColor? stroke = null,
        int zIndex = 0,
        float opacity = 1f,
        IEnumerable<ConsolePoint>? points = null)
        : base(drawableId, bounds, zIndex, opacity)
    {
        if (shape is not ConsoleShapeKind.Rectangle and not ConsoleShapeKind.Ellipse and not ConsoleShapeKind.Line and
            not ConsoleShapeKind.Polygon and not ConsoleShapeKind.Space)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidShape, "Unknown shape kind.");
        ConsolePoint[] pointCopy = (points ?? Array.Empty<ConsolePoint>()).ToArray();
        if (pointCopy.Length > ConsoleContractLimits.Default.MaxGeometryPoints)
            throw new ConsoleContractException(ConsoleContractViolationReason.GeometryTooLarge, "A shape has too many points.");
        if (shape == ConsoleShapeKind.Polygon && pointCopy.Length < 3 || shape == ConsoleShapeKind.Line && pointCopy.Length != 2)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidShape, "The shape point count is invalid.");
        Shape = shape;
        Fill = fill;
        Stroke = stroke;
        Points = Array.AsReadOnly(pointCopy);
    }

    public ConsoleShapeKind Shape { get; }

    public ConsoleColor? Fill { get; }

    public ConsoleColor? Stroke { get; }

    public IReadOnlyList<ConsolePoint> Points { get; }
}

public sealed class HtmlIslandDrawable : CanvasDrawable
{
    public HtmlIslandDrawable(string drawableId, ConsoleHtmlNode root, ConsoleRect bounds, int zIndex = 0, float opacity = 1f)
        : base(drawableId, bounds, zIndex, opacity)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Root.Validate(ConsoleContractLimits.Default, 1);
        StructuredNodes = null;
    }

    public HtmlIslandDrawable(
        string drawableId,
        IEnumerable<ConsoleNode> nodes,
        ConsoleRect bounds,
        int zIndex = 0,
        float opacity = 1f)
        : base(drawableId, bounds, zIndex, opacity)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ConsoleNode[] copy = nodes.ToArray();
        ConsoleNodeValidation.ValidateBatchIfNotEmpty(copy, ConsoleContractLimits.Default);
        Root = null!;
        StructuredNodes = Array.AsReadOnly(copy);
    }

    public ConsoleHtmlNode? Root { get; }

    public IReadOnlyList<ConsoleNode>? StructuredNodes { get; }

    public bool IsStructured => StructuredNodes is not null;
}

public sealed class RasterDrawable : CanvasDrawable
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public RasterDrawable(
        string drawableId,
        byte[] pngData,
        ConsoleRect bounds,
        int zIndex = 0,
        float opacity = 1f,
        byte[]? hoverPngData = null,
        bool hitTestMap = false)
        : base(drawableId, bounds, zIndex, opacity)
    {
        ArgumentNullException.ThrowIfNull(pngData);
        if (pngData.Length < PngSignature.Length || pngData.Length > ConsoleContractLimits.Default.MaxInlineRasterBytes)
            throw new ConsoleContractException(ConsoleContractViolationReason.ImageTooLarge, "Inline raster payload is empty or exceeds its limit.");
        if (!pngData.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidImagePayload, "Inline raster payload is not a PNG image.");
        if (hoverPngData is not null && hoverPngData.Length < PngSignature.Length)
            throw new ConsoleContractException(ConsoleContractViolationReason.ImageTooLarge, "Inline hover raster payload is empty or exceeds its limit.");
        if (hoverPngData is not null && !hoverPngData.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidImagePayload, "Inline hover raster payload is not a PNG image.");
        if (checked(pngData.Length + (hoverPngData?.Length ?? 0)) > ConsoleContractLimits.Default.MaxInlineRasterBytes)
            throw new ConsoleContractException(ConsoleContractViolationReason.ImageTooLarge, "Combined inline raster payload exceeds its limit.");
        PngData = Array.AsReadOnly(pngData.ToArray());
        HoverPngData = hoverPngData is null ? null : Array.AsReadOnly(hoverPngData.ToArray());
        HitTestMap = hitTestMap;
    }

    public IReadOnlyList<byte> PngData { get; }
    public IReadOnlyList<byte>? HoverPngData { get; }
    public bool HitTestMap { get; }
}

public sealed class HitRegion
{
    public HitRegion(
        string regionId,
        ConsoleRect bounds,
        string inputValue,
        bool enabled = true,
        string? tooltip = null)
    {
        ConsoleContractValidation.ValidateIdentifier(regionId, nameof(regionId), ConsoleContractLimits.Default.MaxHitRegionIdLength);
        ConsoleContractValidation.ValidateText(inputValue, nameof(inputValue), ConsoleContractLimits.Default.MaxButtonValueLength, ConsoleContractViolationReason.ButtonValueTooLong);
        if (tooltip is not null)
            ConsoleContractValidation.ValidateText(tooltip, nameof(tooltip), ConsoleContractLimits.Default.MaxTooltipLength, ConsoleContractViolationReason.TooltipTooLong);
        RegionId = regionId;
        Bounds = bounds;
        InputValue = inputValue;
        Enabled = enabled;
        Tooltip = tooltip;
    }

    public string RegionId { get; }

    public ConsoleRect Bounds { get; }

    public string InputValue { get; }

    public bool Enabled { get; }

    public string? Tooltip { get; }
}

public sealed class CanvasScene
{
    public CanvasScene(IEnumerable<CanvasDrawable>? drawables = null, IEnumerable<HitRegion>? hitRegions = null)
    {
        CanvasDrawable[] drawableCopy = (drawables ?? Array.Empty<CanvasDrawable>()).ToArray();
        HitRegion[] hitRegionCopy = (hitRegions ?? Array.Empty<HitRegion>()).ToArray();
        if (drawableCopy.Length > ConsoleContractLimits.Default.MaxDrawables || hitRegionCopy.Length > ConsoleContractLimits.Default.MaxHitRegions)
            throw new ConsoleContractException(ConsoleContractViolationReason.SceneTooLarge, "The canvas scene exceeds its limit.");
        if (drawableCopy.GroupBy(item => item.DrawableId, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
            hitRegionCopy.GroupBy(item => item.RegionId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Scene identifiers must be unique.");
        Drawables = Array.AsReadOnly(drawableCopy);
        HitRegions = Array.AsReadOnly(hitRegionCopy);
    }

    public IReadOnlyList<CanvasDrawable> Drawables { get; }

    public IReadOnlyList<HitRegion> HitRegions { get; }
}

public sealed class MediaChannelState
{
    public MediaChannelState(
        string channel,
        ConsoleAssetId? assetId,
        ConsoleMediaPlaybackState playbackState,
        bool loop,
        float volume,
        long revision,
        ConsoleMediaStartPolicy startPolicy = ConsoleMediaStartPolicy.Immediate)
    {
        ConsoleContractValidation.ValidateIdentifier(channel, nameof(channel), ConsoleContractLimits.Default.MaxMediaChannelLength);
        if (assetId is { } value)
            value.Validate(ConsoleContractLimits.Default);
        BackgroundLayer.ValidateOpacity(volume);
        if (revision < 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidMediaRevision, "Media revision cannot be negative.");
        if (playbackState is not ConsoleMediaPlaybackState.Stopped and not ConsoleMediaPlaybackState.Requested)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidMediaState, "Unknown media playback state.");
        if (startPolicy is not ConsoleMediaStartPolicy.Immediate and not ConsoleMediaStartPolicy.OnUserGesture)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidMediaStartPolicy, "Unknown media start policy.");
        Channel = channel;
        AssetId = assetId;
        PlaybackState = playbackState;
        Loop = loop;
        Volume = volume;
        Revision = revision;
        StartPolicy = startPolicy;
    }

    public string Channel { get; }

    public ConsoleAssetId? AssetId { get; }

    public ConsoleMediaPlaybackState PlaybackState { get; }

    public bool Loop { get; }

    public float Volume { get; }

    public long Revision { get; }

    public ConsoleMediaStartPolicy StartPolicy { get; }
}

public sealed class MediaState
{
    public MediaState(IEnumerable<MediaChannelState>? channels = null)
    {
        MediaChannelState[] copy = (channels ?? Array.Empty<MediaChannelState>()).ToArray();
        if (copy.Length > ConsoleContractLimits.Default.MaxMediaChannels)
            throw new ConsoleContractException(ConsoleContractViolationReason.MediaTooLarge, "The media state exceeds its channel limit.");
        if (copy.GroupBy(item => item.Channel, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new ConsoleContractException(ConsoleContractViolationReason.DuplicateIdentifier, "Media channel identifiers must be unique.");
        Channels = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<MediaChannelState> Channels { get; }
}

public sealed class WindowMetadata
{
    public WindowMetadata(
        string title = "",
        int viewportWidth = 0,
        int viewportHeight = 0,
        ConsoleColor? defaultForeground = null,
        ConsoleColor? defaultBackground = null,
        ConsoleFontSpec? defaultFont = null,
        string fontFaceId = "default",
        string webFontAssetDigest = "")
    {
        ConsoleContractValidation.ValidateText(title, nameof(title), ConsoleContractLimits.Default.MaxWindowTitleLength, ConsoleContractViolationReason.WindowMetadataTooLong);
        if (viewportWidth < 0 || viewportHeight < 0 || viewportWidth > 8_192 || viewportHeight > 8_192)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidViewport, "The logical viewport is outside its limit.");
        ConsoleContractValidation.ValidateLogicalName(fontFaceId, nameof(fontFaceId), ConsoleContractLimits.Default.MaxFontFamilyLength);
        if (webFontAssetDigest.Length != 0 &&
            (webFontAssetDigest.Length != 64 || webFontAssetDigest.Any(character => !char.IsAsciiHexDigit(character))))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidFont, "The web font asset digest is invalid.");
        Title = title;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        DefaultForeground = defaultForeground;
        DefaultBackground = defaultBackground;
        DefaultFont = defaultFont ?? new ConsoleFontSpec();
        FontFaceId = fontFaceId;
        WebFontAssetDigest = webFontAssetDigest;
    }

    public string Title { get; }

    public int ViewportWidth { get; }

    public int ViewportHeight { get; }

    public ConsoleColor? DefaultForeground { get; }

    public ConsoleColor? DefaultBackground { get; }

    public ConsoleFontSpec DefaultFont { get; }

    public string FontFaceId { get; }

    public string WebFontAssetDigest { get; }
}

/// <summary>Parsed, executable-free HTML island tree.</summary>
public abstract class ConsoleHtmlNode
{
    private protected ConsoleHtmlNode() { }

    internal abstract void Validate(ConsoleContractLimits limits, int depth);
}

public sealed class ConsoleHtmlTextNode : ConsoleHtmlNode
{
    public ConsoleHtmlTextNode(string text)
    {
        ConsoleContractValidation.ValidateText(text, nameof(text), ConsoleContractLimits.Default.MaxTextLength, ConsoleContractViolationReason.TextTooLong);
        Text = text;
    }

    public string Text { get; }

    internal override void Validate(ConsoleContractLimits limits, int depth) =>
        ConsoleContractValidation.ValidateText(Text, nameof(Text), limits.MaxTextLength, ConsoleContractViolationReason.TextTooLong);
}

public sealed class ConsoleHtmlBreakNode : ConsoleHtmlNode
{
    public static ConsoleHtmlBreakNode Instance { get; } = new();

    internal override void Validate(ConsoleContractLimits limits, int depth) { }
}

public sealed class ConsoleHtmlElementNode : ConsoleHtmlNode
{
    public ConsoleHtmlElementNode(
        string tag,
        IEnumerable<ConsoleHtmlNode> children,
        ConsoleTextStyle? style = null,
        string? assetId = null,
        string? altText = null)
    {
        ConsoleContractValidation.ValidateLogicalName(tag, nameof(tag), ConsoleContractLimits.Default.MaxHtmlTagNameLength);
        ArgumentNullException.ThrowIfNull(children);
        ConsoleHtmlNode[] copy = children.ToArray();
        if (copy.Length > ConsoleContractLimits.Default.MaxHtmlChildren)
            throw new ConsoleContractException(ConsoleContractViolationReason.HtmlNodeLimitExceeded, "An HTML element has too many children.");
        if (assetId is not null)
            new ConsoleAssetId(assetId).Validate(ConsoleContractLimits.Default);
        if (altText is not null)
            ConsoleContractValidation.ValidateText(altText, nameof(altText), ConsoleContractLimits.Default.MaxAltTextLength, ConsoleContractViolationReason.AltTextTooLong);
        Tag = tag.ToLowerInvariant();
        Children = Array.AsReadOnly(copy);
        Style = style ?? ConsoleTextStyle.Default;
        AssetId = assetId;
        AltText = altText;
    }

    public string Tag { get; }

    public IReadOnlyList<ConsoleHtmlNode> Children { get; }

    public ConsoleTextStyle Style { get; }

    public string? AssetId { get; }

    public string? AltText { get; }

    internal override void Validate(ConsoleContractLimits limits, int depth)
    {
        if (depth > limits.MaxHtmlNestingDepth)
            throw new ConsoleContractException(ConsoleContractViolationReason.HtmlNestingLimitExceeded, "The HTML tree is too deep.");
        ConsoleContractValidation.ValidateLogicalName(Tag, nameof(Tag), limits.MaxHtmlTagNameLength);
        if (Tag is not ("span" or "div" or "p" or "b" or "strong" or "i" or "em" or "u" or "s" or "strike" or "img"))
            throw new ConsoleContractException(ConsoleContractViolationReason.UnsupportedHtml, "The HTML element is not allowlisted.");
        if (Children.Count > limits.MaxHtmlChildren)
            throw new ConsoleContractException(ConsoleContractViolationReason.HtmlNodeLimitExceeded, "The HTML tree has too many children.");
        foreach (ConsoleHtmlNode child in Children)
            child.Validate(limits, depth + 1);
    }
}

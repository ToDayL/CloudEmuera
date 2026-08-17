namespace CloudEmuera.RuntimeAdapter;

public sealed class SpriteNode : ConsoleNode
{
    public SpriteNode(
        ConsoleAssetId assetId,
        ConsoleRect sourceRect,
        ConsoleRect destination,
        int frame = 0,
        int zIndex = 0,
        float opacity = 1f,
        string? altText = null,
        ConsoleAssetId? hoverAssetId = null,
        ConsoleRect? hoverSourceRect = null,
        ConsoleAssetId? mappingAssetId = null,
        ConsoleRect? mappingSourceRect = null,
        IEnumerable<SpriteAnimationFrame>? animationFrames = null)
    {
        assetId.Validate(ConsoleContractLimits.Default);
        if (frame < 0 || frame > ConsoleContractLimits.Default.MaxSpriteFrames)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite frame is outside its limit.");
        BackgroundLayer.ValidateOpacity(opacity);
        if (zIndex is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Sprite z-index is outside its limit.");
        if (altText is not null)
            ConsoleContractValidation.ValidateText(altText, nameof(altText), ConsoleContractLimits.Default.MaxAltTextLength, ConsoleContractViolationReason.AltTextTooLong);
        ValidateOptionalSprite(hoverAssetId, hoverSourceRect, nameof(hoverAssetId));
        ValidateOptionalSprite(mappingAssetId, mappingSourceRect, nameof(mappingAssetId));
        SpriteAnimationFrame[] frameCopy = (animationFrames ?? Array.Empty<SpriteAnimationFrame>()).ToArray();
        if (frameCopy.Length > ConsoleContractLimits.Default.MaxSpriteFrames)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite animation has too many frames.");
        AssetId = assetId;
        SourceRect = sourceRect;
        Destination = destination;
        Frame = frame;
        ZIndex = zIndex;
        Opacity = opacity;
        AltText = altText;
        HoverAssetId = hoverAssetId;
        HoverSourceRect = hoverSourceRect;
        MappingAssetId = mappingAssetId;
        MappingSourceRect = mappingSourceRect;
        AnimationFrames = Array.AsReadOnly(frameCopy);
    }

    public SpriteNode(
        string assetId,
        ConsoleRect sourceRect,
        ConsoleRect destination,
        int frame = 0,
        int zIndex = 0,
        float opacity = 1f,
        string? altText = null)
        : this(new ConsoleAssetId(assetId), sourceRect, destination, frame, zIndex, opacity, altText)
    {
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Sprite;

    public ConsoleAssetId AssetId { get; }

    public ConsoleRect SourceRect { get; }

    public ConsoleRect Destination { get; }

    public int Frame { get; }

    public int ZIndex { get; }

    public float Opacity { get; }

    public string? AltText { get; }

    public ConsoleAssetId? HoverAssetId { get; }

    public ConsoleRect? HoverSourceRect { get; }

    public ConsoleAssetId? MappingAssetId { get; }

    public ConsoleRect? MappingSourceRect { get; }

    public IReadOnlyList<SpriteAnimationFrame> AnimationFrames { get; }

    private static void ValidateOptionalSprite(ConsoleAssetId? assetId, ConsoleRect? sourceRect, string parameterName)
    {
        if (assetId is null != sourceRect is null)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Optional Sprite asset and source rectangle must be supplied together.", parameterName);
        if (assetId is { } value)
            value.Validate(ConsoleContractLimits.Default);
    }
}

public sealed record SpriteAnimationFrame
{
    public SpriteAnimationFrame(ConsoleAssetId assetId, ConsoleRect sourceRect, ConsolePoint offset, int durationMilliseconds)
    {
        assetId.Validate(ConsoleContractLimits.Default);
        if (durationMilliseconds is < 1 or > 3_600_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite frame duration is outside its limit.");
        AssetId = assetId;
        SourceRect = sourceRect;
        Offset = offset;
        DurationMilliseconds = durationMilliseconds;
    }

    public ConsoleAssetId AssetId { get; }
    public ConsoleRect SourceRect { get; }
    public ConsolePoint Offset { get; }
    public int DurationMilliseconds { get; }
}

public sealed class ShapeNode : ConsoleNode
{
    public ShapeNode(
        ConsoleShapeKind shape,
        ConsoleRect bounds,
        ConsoleColor? fill = null,
        ConsoleColor? stroke = null,
        int zIndex = 0,
        IEnumerable<ConsolePoint>? points = null,
        ConsoleColor? buttonColor = null)
    {
        if (shape is not ConsoleShapeKind.Rectangle and not ConsoleShapeKind.Ellipse and not ConsoleShapeKind.Line and
            not ConsoleShapeKind.Polygon and not ConsoleShapeKind.Space)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidShape, "Unknown shape kind.");
        ConsolePoint[] copy = (points ?? Array.Empty<ConsolePoint>()).ToArray();
        if (copy.Length > ConsoleContractLimits.Default.MaxGeometryPoints)
            throw new ConsoleContractException(ConsoleContractViolationReason.GeometryTooLarge, "A shape has too many points.");
        if (shape == ConsoleShapeKind.Polygon && copy.Length < 3 || shape == ConsoleShapeKind.Line && copy.Length != 2)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidShape, "The shape point count is invalid.");
        if (zIndex is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Shape z-index is outside its limit.");
        Shape = shape;
        Bounds = bounds;
        Fill = fill;
        Stroke = stroke;
        ButtonColor = buttonColor;
        ZIndex = zIndex;
        Points = Array.AsReadOnly(copy);
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Shape;

    public ConsoleShapeKind Shape { get; }

    public ConsoleRect Bounds { get; }

    public ConsoleColor? Fill { get; }

    public ConsoleColor? Stroke { get; }

    /// <summary>Emuera <c>shape bcolor</c>, used while the shape is selected.</summary>
    public ConsoleColor? ButtonColor { get; }

    public int ZIndex { get; }

    public IReadOnlyList<ConsolePoint> Points { get; }

    internal void Validate(ConsoleContractLimits limits)
    {
        if (Points.Count > limits.MaxGeometryPoints)
            throw new ConsoleContractException(ConsoleContractViolationReason.GeometryTooLarge, "A shape has too many points.");
    }
}

/// <summary>Four-sided CSS-like lengths used by Emuera's safe HTML div extension.</summary>
public readonly record struct ConsoleInsets
{
    public ConsoleInsets(int top, int right, int bottom, int left)
    {
        Validate(top, nameof(top));
        Validate(right, nameof(right));
        Validate(bottom, nameof(bottom));
        Validate(left, nameof(left));
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }
    public int Left { get; }

    public static ConsoleInsets Zero => new(0, 0, 0, 0);

    private static void Validate(int value, string parameterName)
    {
        if (value is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A div inset is outside its limit.", parameterName);
    }
}

/// <summary>Validated box-model data for an Emuera HTML div.</summary>
public sealed class ConsoleBoxModel
{
    public ConsoleBoxModel(
        ConsoleInsets margin,
        ConsoleInsets padding,
        ConsoleInsets border,
        ConsoleInsets radius,
        IEnumerable<ConsoleColor?>? borderColors = null)
    {
        ConsoleColor?[] colors = (borderColors ?? Array.Empty<ConsoleColor?>()).ToArray();
        if (colors.Length != 0 && colors.Length != 4)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A div must have four border colors.");
        Margin = margin;
        Padding = padding;
        Border = border;
        Radius = radius;
        BorderColors = Array.AsReadOnly(colors.Length == 0 ? new ConsoleColor?[4] : colors);
    }

    public ConsoleInsets Margin { get; }
    public ConsoleInsets Padding { get; }
    public ConsoleInsets Border { get; }
    public ConsoleInsets Radius { get; }
    public IReadOnlyList<ConsoleColor?> BorderColors { get; }
}

/// <summary>
/// Structured equivalent of Emuera's private HTML_PRINT div extension. It
/// retains layout and box-model semantics without exposing arbitrary CSS.
/// </summary>
public sealed class DivNode : ConsoleNode
{
    public DivNode(
        IEnumerable<ConsoleNode> children,
        ConsoleRect bounds,
        int zIndex = 0,
        ConsoleColor? background = null,
        bool isRelative = true,
        ConsoleBoxModel? box = null)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (zIndex is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "A div z-index is outside its limit.");
        ConsoleNode[] copy = children.ToArray();
        if (copy.Length > ConsoleContractLimits.Default.MaxBatchNodeCount)
            throw new ConsoleContractException(ConsoleContractViolationReason.BatchTooLarge, "A div has too many child nodes.");
        Bounds = bounds;
        ZIndex = zIndex;
        Background = background;
        IsRelative = isRelative;
        Box = box;
        Children = Array.AsReadOnly(copy);
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Div;

    public IReadOnlyList<ConsoleNode> Children { get; }
    public ConsoleRect Bounds { get; }
    public int ZIndex { get; }
    public ConsoleColor? Background { get; }
    public bool IsRelative { get; }
    public ConsoleBoxModel? Box { get; }
}

public sealed class HtmlIslandNode : ConsoleNode
{
    public HtmlIslandNode(ConsoleHtmlNode root, ConsoleRect? layout = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Root.Validate(ConsoleContractLimits.Default, 1);
        Layout = layout;
        StructuredNodes = null;
    }

    /// <summary>
    /// Creates an HTML island from the structured result of the pinned
    /// upstream HtmlManager.  The legacy <see cref="Root"/> representation is
    /// retained only for wire compatibility with older peers; new runtime
    /// output must use this constructor so buttons, shapes, divs and line
    /// breaks cannot be flattened into an unsafe HTML subset.
    /// </summary>
    public HtmlIslandNode(IEnumerable<ConsoleNode> nodes, ConsoleRect? layout = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ConsoleNode[] copy = nodes.ToArray();
        ConsoleNodeValidation.ValidateBatchIfNotEmpty(copy, ConsoleContractLimits.Default);
        Root = null!;
        Layout = layout;
        StructuredNodes = Array.AsReadOnly(copy);
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.HtmlIsland;

    public ConsoleHtmlNode? Root { get; }

    /// <summary>Structured nodes emitted by the upstream parser, if present.</summary>
    public IReadOnlyList<ConsoleNode>? StructuredNodes { get; }

    public bool IsStructured => StructuredNodes is not null;

    public ConsoleRect? Layout { get; }

    internal void Validate(ConsoleContractLimits limits)
    {
        if (StructuredNodes is { } nodes)
            ConsoleNodeValidation.ValidateBatchIfNotEmpty(nodes, limits);
        else
            Root!.Validate(limits, 1);
    }
}

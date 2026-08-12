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
        string? altText = null)
    {
        assetId.Validate(ConsoleContractLimits.Default);
        if (frame < 0 || frame > ConsoleContractLimits.Default.MaxSpriteFrames)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite frame is outside its limit.");
        BackgroundLayer.ValidateOpacity(opacity);
        if (zIndex is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Sprite z-index is outside its limit.");
        if (altText is not null)
            ConsoleContractValidation.ValidateText(altText, nameof(altText), ConsoleContractLimits.Default.MaxAltTextLength, ConsoleContractViolationReason.AltTextTooLong);
        AssetId = assetId;
        SourceRect = sourceRect;
        Destination = destination;
        Frame = frame;
        ZIndex = zIndex;
        Opacity = opacity;
        AltText = altText;
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
}

public sealed class ShapeNode : ConsoleNode
{
    public ShapeNode(
        ConsoleShapeKind shape,
        ConsoleRect bounds,
        ConsoleColor? fill = null,
        ConsoleColor? stroke = null,
        int zIndex = 0,
        IEnumerable<ConsolePoint>? points = null)
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
        ZIndex = zIndex;
        Points = Array.AsReadOnly(copy);
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Shape;

    public ConsoleShapeKind Shape { get; }

    public ConsoleRect Bounds { get; }

    public ConsoleColor? Fill { get; }

    public ConsoleColor? Stroke { get; }

    public int ZIndex { get; }

    public IReadOnlyList<ConsolePoint> Points { get; }

    internal void Validate(ConsoleContractLimits limits)
    {
        if (Points.Count > limits.MaxGeometryPoints)
            throw new ConsoleContractException(ConsoleContractViolationReason.GeometryTooLarge, "A shape has too many points.");
    }
}

public sealed class HtmlIslandNode : ConsoleNode
{
    public HtmlIslandNode(ConsoleHtmlNode root, ConsoleRect? layout = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Root.Validate(ConsoleContractLimits.Default, 1);
        Layout = layout;
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.HtmlIsland;

    public ConsoleHtmlNode Root { get; }

    public ConsoleRect? Layout { get; }

    internal void Validate(ConsoleContractLimits limits)
    {
        Root.Validate(limits, 1);
    }
}

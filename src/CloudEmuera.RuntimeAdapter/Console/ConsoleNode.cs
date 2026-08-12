namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleNodeKind
{
    Text,
    LineBreak,
    Button,
    Image,
    Sprite,
    Shape,
    HtmlIsland
}

/// <summary>
/// Closed, platform-neutral display node hierarchy. The restricted
/// constructor prevents consumers from adding an unvalidated node kind.
/// </summary>
public abstract class ConsoleNode
{
    private protected ConsoleNode()
    {
    }

    public abstract ConsoleNodeKind Kind { get; }
}

public sealed class TextNode : ConsoleNode
{
    public TextNode(string text, ConsoleTextStyle? style = null)
    {
        ConsoleContractValidation.ValidateText(
            text,
            nameof(text),
            ConsoleContractLimits.Default.MaxTextLength,
            ConsoleContractViolationReason.TextTooLong,
            allowControlCharacters: false);

        Text = text;
        Style = style ?? ConsoleTextStyle.Default;
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Text;

    public string Text { get; }

    public ConsoleTextStyle Style { get; }
}

public sealed class LineBreakNode : ConsoleNode
{
    public static LineBreakNode Instance { get; } = new();

    public override ConsoleNodeKind Kind => ConsoleNodeKind.LineBreak;
}

public sealed class ButtonNode : ConsoleNode
{
    public ButtonNode(
        IEnumerable<ConsoleNode> children,
        string value,
        string? tooltip = null,
        bool enabled = true,
        long generation = 0)
    {
        ArgumentNullException.ThrowIfNull(children);
        ConsoleContractValidation.ValidateText(
            value,
            nameof(value),
            ConsoleContractLimits.Default.MaxButtonValueLength,
            ConsoleContractViolationReason.ButtonValueTooLong);
        if (value.Length == 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.EmptyValue,
                "A button value is required.",
                nameof(value));
        }

        if (tooltip is not null)
        {
            ConsoleContractValidation.ValidateText(
                tooltip,
                nameof(tooltip),
                ConsoleContractLimits.Default.MaxTooltipLength,
                ConsoleContractViolationReason.TooltipTooLong);
        }

        ConsoleNode[] copy = children.ToArray();
        if (copy.Length == 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.EmptyBatch,
                "A button must have at least one label node.",
                nameof(children));
        }

        if (copy.Any(child => child is not TextNode))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidNodeType,
                "Button labels may contain TextNode values only.",
                nameof(children));
        }

        if (copy.Length > ConsoleContractLimits.Default.MaxButtonLabelNodeCount)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.TooManyButtonLabelNodes,
                "A button has too many label nodes.",
                nameof(children));
        }

        Children = Array.AsReadOnly(copy);
        Value = value;
        Tooltip = tooltip;
        Enabled = enabled;
        if (generation < 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Button generation cannot be negative.");
        Generation = generation;
    }

    public ButtonNode(
        string value,
        IEnumerable<ConsoleNode> children,
        string? tooltip = null,
        bool enabled = true,
        long generation = 0)
        : this(children, value, tooltip, enabled, generation)
    {
    }

    public ButtonNode(string label, string value, string? tooltip = null, bool enabled = true, long generation = 0)
        : this([new TextNode(label)], value, tooltip, enabled, generation)
    {
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Button;

    public IReadOnlyList<ConsoleNode> Children { get; }

    public string Value { get; }

    public string? Tooltip { get; }

    public bool Enabled { get; }

    public long Generation { get; }
}

public sealed class ImageNode : ConsoleNode
{
    public ImageNode(
        ConsoleAssetId assetId,
        int? width = null,
        int? height = null,
        string? altText = null)
    {
        assetId.Validate(ConsoleContractLimits.Default);
        ValidateDimension(width, nameof(width), ConsoleContractLimits.Default.MaxImageWidth);
        ValidateDimension(height, nameof(height), ConsoleContractLimits.Default.MaxImageHeight);
        if (altText is not null)
        {
            ConsoleContractValidation.ValidateText(
                altText,
                nameof(altText),
                ConsoleContractLimits.Default.MaxAltTextLength,
                ConsoleContractViolationReason.AltTextTooLong);
        }

        AssetId = assetId;
        Width = width;
        Height = height;
        AltText = altText;
        Destination = width is { } w && height is { } h ? new ConsoleRect(0, 0, w, h) : null;
    }

    public ImageNode(
        ConsoleAssetId assetId,
        ConsoleRect? sourceRect,
        ConsoleRect? destination,
        string? altText = null,
        bool decorative = false,
        int zIndex = 0)
    {
        assetId.Validate(ConsoleContractLimits.Default);
        if (sourceRect is { } source && (source.Width <= 0 || source.Height <= 0))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "The image source rectangle is invalid.");
        if (destination is { } target && (target.Width <= 0 || target.Height <= 0))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "The image destination rectangle is invalid.");
        if (altText is not null)
            ConsoleContractValidation.ValidateText(altText, nameof(altText), ConsoleContractLimits.Default.MaxAltTextLength, ConsoleContractViolationReason.AltTextTooLong);
        if (zIndex is < -1_000_000 or > 1_000_000)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Image z-index is outside its limit.");
        AssetId = assetId;
        Width = destination?.Width;
        Height = destination?.Height;
        AltText = altText;
        SourceRect = sourceRect;
        Destination = destination;
        Decorative = decorative;
        ZIndex = zIndex;
    }

    public ImageNode(
        string assetId,
        int? width = null,
        int? height = null,
        string? altText = null)
        : this(new ConsoleAssetId(assetId), width, height, altText)
    {
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Image;

    public ConsoleAssetId AssetId { get; }

    public int? Width { get; }

    public int? Height { get; }

    public string? AltText { get; }

    public ConsoleRect? SourceRect { get; }

    public ConsoleRect? Destination { get; }

    public bool Decorative { get; }

    public int ZIndex { get; }

    private static void ValidateDimension(int? dimension, string parameterName, int maximum)
    {
        if (dimension is <= 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidImageDimension,
                $"{parameterName} must be positive when specified.",
                parameterName);
        }

        if (dimension > maximum)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.ImageTooLarge,
                $"{parameterName} exceeds the image dimension limit.",
                parameterName);
        }
    }
}

internal static class ConsoleNodeValidation
{
    public static void ValidateBatchIfNotEmpty(IEnumerable<ConsoleNode> nodes, ConsoleContractLimits limits)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ConsoleNode[] copy = nodes.ToArray();
        if (copy.Length != 0)
        {
            ValidateBatch(copy, limits);
        }
    }

    public static void ValidateBatch(IEnumerable<ConsoleNode> nodes, ConsoleContractLimits limits)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        limits.Validate();

        ConsoleNode[] copy = nodes.ToArray();
        if (copy.Length == 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.EmptyBatch,
                "An append operation must contain at least one node.",
                nameof(nodes));
        }

        if (copy.Length > limits.MaxBatchNodeCount)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.BatchTooLarge,
                "The append operation contains too many nodes.",
                nameof(nodes));
        }

        foreach (ConsoleNode node in copy)
        {
            ValidateNode(node, limits, depth: 1);
        }
    }

    public static void ValidateNode(ConsoleNode? node, ConsoleContractLimits limits, int depth)
    {
        if (node is null)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.NullValue, "A console node is required.");
        }

        if (depth > limits.MaxNodeDepth)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.NodeTooDeep, "The console node is too deep.");
        }

        switch (node)
        {
            case TextNode text:
                ConsoleContractValidation.ValidateText(
                    text.Text,
                    nameof(TextNode.Text),
                    limits.MaxTextLength,
                    ConsoleContractViolationReason.TextTooLong);
                ConsoleContractValidation.ValidateFontStyle(text.Style.Decorations);
                break;
            case LineBreakNode:
                break;
            case ButtonNode button:
                ConsoleContractValidation.ValidateText(
                    button.Value,
                    nameof(ButtonNode.Value),
                    limits.MaxButtonValueLength,
                    ConsoleContractViolationReason.ButtonValueTooLong);
                if (button.Value.Length == 0)
                {
                    throw new ConsoleContractException(ConsoleContractViolationReason.EmptyValue, "A button value is required.");
                }

                if (button.Generation < 0)
                    throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Button generation cannot be negative.");

                if (button.Tooltip is not null)
                {
                    ConsoleContractValidation.ValidateText(
                        button.Tooltip,
                        nameof(ButtonNode.Tooltip),
                        limits.MaxTooltipLength,
                        ConsoleContractViolationReason.TooltipTooLong);
                }

                if (button.Children.Count == 0 || button.Children.Count > limits.MaxButtonLabelNodeCount)
                {
                    throw new ConsoleContractException(
                        ConsoleContractViolationReason.TooManyButtonLabelNodes,
                        "The button label node count is outside its limit.");
                }

                foreach (ConsoleNode child in button.Children)
                {
                    if (child is not TextNode)
                    {
                        throw new ConsoleContractException(
                            ConsoleContractViolationReason.InvalidNodeType,
                            "Button labels may contain TextNode values only.");
                    }

                    ValidateNode(child, limits, depth + 1);
                }

                break;
            case ImageNode image:
                image.AssetId.Validate(limits);
                ValidateImageDimension(image.Width, limits.MaxImageWidth, nameof(ImageNode.Width));
                ValidateImageDimension(image.Height, limits.MaxImageHeight, nameof(ImageNode.Height));
                if (image.AltText is not null)
                {
                    ConsoleContractValidation.ValidateText(
                        image.AltText,
                        nameof(ImageNode.AltText),
                        limits.MaxAltTextLength,
                        ConsoleContractViolationReason.AltTextTooLong);
                }

                if (image.SourceRect is { } source && (source.Width <= 0 || source.Height <= 0) ||
                    image.Destination is { } destination && (destination.Width <= 0 || destination.Height <= 0))
                    throw new ConsoleContractException(ConsoleContractViolationReason.InvalidGeometry, "Image rectangles must be positive.");

                break;
            case SpriteNode sprite:
                sprite.AssetId.Validate(limits);
                if (sprite.HoverAssetId is { } hover)
                    hover.Validate(limits);
                if (sprite.MappingAssetId is { } mapping)
                    mapping.Validate(limits);
                if (sprite.Frame < 0 || sprite.Frame > limits.MaxSpriteFrames)
                    throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite frame is outside its limit.");
                if (sprite.AnimationFrames.Count > limits.MaxSpriteFrames)
                    throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite animation has too many frames.");
                foreach (SpriteAnimationFrame animationFrame in sprite.AnimationFrames)
                {
                    animationFrame.AssetId.Validate(limits);
                    if (animationFrame.DurationMilliseconds is < 1 or > 3_600_000)
                        throw new ConsoleContractException(ConsoleContractViolationReason.InvalidSpriteFrame, "Sprite frame duration is outside its limit.");
                }
                break;
            case ShapeNode shape:
                shape.Validate(limits);
                break;
            case HtmlIslandNode island:
                island.Validate(limits);
                break;
            default:
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidNodeType,
                    "The console node type is not part of the contract.");
        }
    }

    private static void ValidateImageDimension(int? dimension, int maximum, string parameterName)
    {
        if (dimension is <= 0)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidImageDimension, "Image dimensions must be positive.", parameterName);
        }

        if (dimension > maximum)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.ImageTooLarge, "Image dimensions exceed their limit.", parameterName);
        }
    }
}

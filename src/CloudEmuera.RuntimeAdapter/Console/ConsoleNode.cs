namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleNodeKind
{
    Text,
    LineBreak,
    Button,
    Image
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
        bool enabled = true)
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
    }

    public ButtonNode(
        string value,
        IEnumerable<ConsoleNode> children,
        string? tooltip = null,
        bool enabled = true)
        : this(children, value, tooltip, enabled)
    {
    }

    public ButtonNode(string label, string value, string? tooltip = null, bool enabled = true)
        : this([new TextNode(label)], value, tooltip, enabled)
    {
    }

    public override ConsoleNodeKind Kind => ConsoleNodeKind.Button;

    public IReadOnlyList<ConsoleNode> Children { get; }

    public string Value { get; }

    public string? Tooltip { get; }

    public bool Enabled { get; }
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

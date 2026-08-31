// CloudEmuera modification: the only projection from the pinned upstream
// HtmlManager semantic result into the platform-neutral console contract.
// This file contains no HTML lexer/parser and never emits raw HTML or URLs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using MinorShift.Emuera.UI.Game;
using ConsoleColor = CloudEmuera.RuntimeAdapter.ConsoleColor;

namespace MinorShift.Emuera.GameView;

internal sealed class UpstreamHtmlTranslationContext
{
    public UpstreamHtmlTranslationContext(
        ConsoleContractLimits limits,
        int fontSize,
        int lineHeight,
        ConsoleColor? defaultForeground,
        ConsoleColor? defaultButtonColor,
        long buttonGeneration,
        Func<string, RuntimeSpriteDefinition> imageResolver,
        UpstreamHtmlParseMode mode,
        bool convertBackslashToYen = true,
        Action<ButtonNode> integerButtonMarker = null)
    {
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        Limits.Validate();
        if (fontSize <= 0 || fontSize > 256)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (lineHeight < 0 || lineHeight > 512)
            throw new ArgumentOutOfRangeException(nameof(lineHeight));
        if (buttonGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(buttonGeneration));
        FontSize = fontSize;
        LineHeight = lineHeight;
        DefaultForeground = defaultForeground;
        DefaultButtonColor = defaultButtonColor;
        ButtonGeneration = buttonGeneration;
        ImageResolver = imageResolver;
        Mode = mode;
        ConvertBackslashToYen = convertBackslashToYen;
        IntegerButtonMarker = integerButtonMarker;
    }

    public ConsoleContractLimits Limits { get; }
    public int FontSize { get; }
    public int LineHeight { get; }
    public ConsoleColor? DefaultForeground { get; }
    public ConsoleColor? DefaultButtonColor { get; }
    public long ButtonGeneration { get; }
    public Func<string, RuntimeSpriteDefinition> ImageResolver { get; }
    public UpstreamHtmlParseMode Mode { get; }
    public bool ConvertBackslashToYen { get; }
    public Action<ButtonNode> IntegerButtonMarker { get; }

    public string DisplayText(string value) => HeadlessDisplayText.Project(value, ConvertBackslashToYen);

    public string DisplayTooltip(string value) => HeadlessDisplayText.ProjectTooltip(value, ConvertBackslashToYen);
}

internal sealed class UpstreamHtmlTranslationResult
{
    public UpstreamHtmlTranslationResult(
        IReadOnlyList<ConsoleNode> nodes,
        ConsoleLineAlignment alignment,
        bool noWrap)
    {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        Alignment = alignment;
        NoWrap = noWrap;
    }

    public IReadOnlyList<ConsoleNode> Nodes { get; }
    public ConsoleLineAlignment Alignment { get; }
    public bool NoWrap { get; }
}

internal sealed class UpstreamHtmlTranslationException : Exception
{
    public UpstreamHtmlTranslationException(string reasonCode, string message, Exception innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

internal static class UpstreamHtmlTranslator
{
    public static UpstreamHtmlTranslationResult Translate(
        UpstreamHtmlFragment fragment,
        UpstreamHtmlTranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(context);

        var counter = new TranslationCounter(context.Limits);
        var nodes = TranslateSequence(fragment.Sequence, context, counter);
        return new UpstreamHtmlTranslationResult(
            nodes.AsReadOnly(),
            ToAlignment(fragment.Alignment),
            fragment.NoWrap);
    }

    private static List<ConsoleNode> TranslateSequence(
        UpstreamHtmlSequence sequence,
        UpstreamHtmlTranslationContext context,
        TranslationCounter counter)
    {
        if (sequence == null)
            throw Unsupported("The upstream HTML fragment has no sequence.");

        var result = new List<ConsoleNode>();
        foreach (UpstreamHtmlSequenceItem item in sequence.Items)
        {
            if (item == null)
                throw Unsupported("The upstream HTML sequence contains a null item.");
            if (item.IsBreak)
            {
                counter.Node();
                result.Add(LineBreakNode.Instance);
                continue;
            }

            ConsoleNode[] segmentNodes = TranslateSegment(item.Segment, context, counter);
            result.AddRange(segmentNodes);
        }
        return result;
    }

    private static ConsoleNode[] TranslateSegment(
        UpstreamHtmlSegment segment,
        UpstreamHtmlTranslationContext context,
        TranslationCounter counter)
    {
        if (segment == null || segment.Parts == null || segment.Parts.Count == 0)
            throw Unsupported("The upstream HTML parser produced an empty segment.");

        var children = new List<ConsoleNode>(segment.Parts.Count);
        foreach (UpstreamHtmlPart part in segment.Parts)
            children.Add(TranslatePart(part, context, counter));

        if (segment.IsInteractive)
        {
            string value = segment.Value ?? string.Empty;
            counter.Node();
            ButtonNode button = new(
                children,
                value,
                segment.Title is null ? null : context.DisplayTooltip(segment.Title),
                enabled: true,
                generation: context.ButtonGeneration,
                positionX: segment.PositionX);
            if (segment.ValueKind == UpstreamHtmlButtonValueKind.Integer)
                context.IntegerButtonMarker?.Invoke(button);
            return [button];
        }

        // A nonbutton/clearbutton segment can still carry title/pos in the
        // pinned parser's output. Keep those fields in a disabled closed node
        // rather than inventing an interactive input or dropping metadata.
        if (segment.Title != null || segment.PositionX != null)
        {
            counter.Node();
            return
            [
                new ButtonNode(
                    children,
                    string.Empty,
                    segment.Title is null ? null : context.DisplayTooltip(segment.Title),
                    enabled: false,
                    generation: context.ButtonGeneration,
                    positionX: segment.PositionX)
            ];
        }

        return children.ToArray();
    }

    private static ConsoleNode TranslatePart(
        UpstreamHtmlPart part,
        UpstreamHtmlTranslationContext context,
        TranslationCounter counter)
    {
        if (part == null)
            throw Unsupported("The upstream HTML parser produced a null part.");

        ConsoleNode node = part switch
        {
            UpstreamHtmlTextPart text => TranslateText(text, context, counter),
            UpstreamHtmlImagePart image => TranslateImage(image, context, counter),
            UpstreamHtmlShapePart shape => TranslateShape(shape, context, counter),
            UpstreamHtmlDivPart div => TranslateDiv(div, context, counter),
            _ => throw Unsupported("The upstream HTML parser produced an unknown part.")
        };
        return node;
    }

    private static ConsoleNode TranslateText(
        UpstreamHtmlTextPart text,
        UpstreamHtmlTranslationContext context,
        TranslationCounter counter)
    {
        counter.Text(text.Text);
        counter.Node();
        return new TextNode(context.DisplayText(text.Text), ToTextStyle(text.Style, context));
    }

    private static ConsoleNode TranslateImage(
        UpstreamHtmlImagePart image,
        UpstreamHtmlTranslationContext context,
        TranslationCounter counter)
    {
        if (string.IsNullOrEmpty(image.Source))
            throw Unsupported("The upstream HTML image has no logical source name.");

        RuntimeSpriteDefinition resolved = ResolveSprite(context.ImageResolver, image.Source);
        if (!IsUsableSprite(resolved, context.Limits))
        {
            // The desktop ConsoleImagePart displays its generated alt text if
            // the main image is absent. A bounded literal text node is the
            // equivalent safe behavior; it is never interpreted by the Web UI.
            counter.Text(image.Source);
            counter.Node();
            return new TextNode(context.DisplayText(SafeImageFallback(image.Source, context.Limits)), ConsoleTextStyle.Default);
        }

        int height = ResolveLength(image.Height, context.FontSize, context.FontSize);
        int width = image.Width is null || image.Width.Value.Value == 0
            ? ResolveAspectWidth(resolved, height)
            : ResolveLength(image.Width, context.FontSize, context.FontSize);
        int y = ResolveLength(image.YPosition, context.FontSize, 0);
        RuntimeSpriteDefinition hover = ResolveOptionalSprite(context.ImageResolver, image.ButtonSource, context.Limits);
        RuntimeSpriteDefinition mapping = ResolveOptionalSprite(context.ImageResolver, image.MappingSource, context.Limits);

        counter.Node();
        return CreateSpriteNode(image.Source, resolved, width, height, y, hover, mapping, context.Limits);
    }

    private static ConsoleNode TranslateShape(
        UpstreamHtmlShapePart shape,
        UpstreamHtmlTranslationContext context,
        TranslationCounter counter)
    {
        if (shape.ErrorText != null)
        {
            counter.Text(shape.ErrorText);
            counter.Node();
            return new TextNode(context.DisplayText(shape.ErrorText));
        }

        string type = shape.Type.ToLowerInvariant();
        int[] pixels = shape.Parameters.Select(value => ToPixels(value, context.FontSize)).ToArray();
        ConsoleShapeKind kind;
        ConsoleRect bounds;
        switch (type)
        {
            case "space" when pixels.Length == 1:
                kind = ConsoleShapeKind.Space;
                bounds = new ConsoleRect(0, 0, Math.Max(1, Math.Abs(pixels[0])), Math.Max(1, context.LineHeight));
                break;
            case "rect" when pixels.Length == 1 && shape.Parameters[0].Value > 0:
                kind = ConsoleShapeKind.Rectangle;
                bounds = new ConsoleRect(0, 0, Math.Max(1, Math.Abs(pixels[0])), Math.Max(1, context.LineHeight));
                break;
            case "rect" when pixels.Length == 4 && pixels[0] >= 0 && pixels[2] > 0 && pixels[3] > 0:
                kind = ConsoleShapeKind.Rectangle;
                bounds = new ConsoleRect(pixels[0], pixels[1], Math.Max(1, pixels[2]), Math.Max(1, pixels[3]));
                break;
            default:
                throw Unsupported($"The upstream HTML shape '{shape.Type}' is not representable.");
        }

        counter.Node();
        return new ShapeNode(
            kind,
            bounds,
            fill: ToColor(shape.ForegroundRgb),
            buttonColor: ToColor(shape.ButtonRgb));
    }

    private static ConsoleNode TranslateDiv(
        UpstreamHtmlDivPart div,
        UpstreamHtmlTranslationContext context,
        TranslationCounter counter)
    {
        counter.Depth(div.Depth);
        // A div is the layout frame for line-oriented HTML output. Keep the
        // two axes in their native units: horizontal coordinates/sizes use the
        // glyph width (FontSize), while vertical coordinates/sizes use the
        // physical row advance (LineHeight). Explicit px values are preserved
        // by ToPixels on either axis.
        int x = div.X is { } xValue ? ToHorizontalPixels(xValue, context) : 0;
        int y = div.Y is { } yValue ? ToVerticalPixels(yValue, context) : 0;
        int width = Math.Abs(ToHorizontalPixels(div.Width, context));
        int height = Math.Abs(ToVerticalPixels(div.Height, context));
        if (width <= 0 || height <= 0)
            throw Unsupported("The upstream HTML div has a non-positive rectangle.");

        List<ConsoleNode> children = TranslateSequence(div.Children, context, counter);
        ConsoleBoxModel box = ToBox(div.Box, context);
        counter.Node();
        return new DivNode(
            children,
            new ConsoleRect(x, y, width, height),
            zIndex: div.Depth,
            background: ToColor(div.BackgroundRgb),
            isRelative: div.IsRelative,
            box: box);
    }

    private static ConsoleTextStyle ToTextStyle(
        UpstreamHtmlTextStyle style,
        UpstreamHtmlTranslationContext context)
    {
        if (style == null)
            throw Unsupported("The upstream HTML text has no style snapshot.");

        ConsoleFontStyle decorations = ConsoleFontStyle.None;
        if ((style.FontStyle & UpstreamHtmlFontStyle.Bold) != 0)
            decorations |= ConsoleFontStyle.Bold;
        if ((style.FontStyle & UpstreamHtmlFontStyle.Italic) != 0)
            decorations |= ConsoleFontStyle.Italic;
        if ((style.FontStyle & UpstreamHtmlFontStyle.Underline) != 0)
            decorations |= ConsoleFontStyle.Underline;
        if ((style.FontStyle & UpstreamHtmlFontStyle.Strike) != 0)
            decorations |= ConsoleFontStyle.Strike;

        return new ConsoleTextStyle(
            style.ColorChanged ? ToColor(style.ForegroundRgb) : context.DefaultForeground,
            decorations: decorations,
            fontFamily: ResolveFontName(style.FontName, context.Limits),
            fontSize: context.FontSize,
            lineHeight: context.LineHeight,
            buttonColor: ToColor(style.ButtonRgb) ?? context.DefaultButtonColor);
    }

    private static ConsoleBoxModel ToBox(
        UpstreamHtmlBoxModel box,
        UpstreamHtmlTranslationContext context)
    {
        if (box == null)
            return null;

        ConsoleInsets margin = ToInsets(box.Margin, context);
        ConsoleInsets padding = ToInsets(box.Padding, context);
        ConsoleInsets border = ToInsets(box.Border, context);
        ConsoleInsets radius = ToInsets(box.Radius, context);
        ConsoleColor?[] colors = new ConsoleColor?[4];
        if (box.BorderColorsRgb != null)
        {
            if (box.BorderColorsRgb.Length != 4)
                throw Unsupported("The upstream HTML div has an invalid border color array.");
            for (int index = 0; index < colors.Length; index++)
                colors[index] = ToColor(box.BorderColorsRgb[index]);
        }
        return new ConsoleBoxModel(margin, padding, border, radius, colors);
    }

    private static ConsoleInsets ToInsets(
        UpstreamHtmlLength[] values,
        UpstreamHtmlTranslationContext context)
    {
        if (values == null)
            return ConsoleInsets.Zero;
        if (values.Length != 4)
            throw Unsupported("The upstream HTML div has an invalid box-model array.");
        return new ConsoleInsets(
            ToVerticalPixels(values[0], context),
            ToHorizontalPixels(values[1], context),
            ToVerticalPixels(values[2], context),
            ToHorizontalPixels(values[3], context));
    }

    private static RuntimeSpriteDefinition ResolveSprite(
        Func<string, RuntimeSpriteDefinition> resolver,
        string source)
    {
        if (resolver == null)
            return null;
        try
        {
            return resolver(source);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static RuntimeSpriteDefinition ResolveOptionalSprite(
        Func<string, RuntimeSpriteDefinition> resolver,
        string source,
        ConsoleContractLimits limits) =>
        string.IsNullOrEmpty(source) || source.Length > limits.MaxAssetIdLength
            ? null
            : ResolveSprite(resolver, source) is { } value && IsUsableSprite(value, limits)
                ? value
                : null;

    private static bool IsUsableSprite(RuntimeSpriteDefinition value, ConsoleContractLimits limits) =>
        value != null &&
        !string.IsNullOrEmpty(value.AssetId) &&
        value.AssetId.Length <= limits.MaxAssetIdLength &&
        value.SourceWidth > 0 && value.SourceHeight > 0 &&
        value.DestinationWidth > 0 && value.DestinationHeight > 0;

    private static SpriteNode CreateSpriteNode(
        string source,
        RuntimeSpriteDefinition resolved,
        int targetWidth,
        int targetHeight,
        int y,
        RuntimeSpriteDefinition hover,
        RuntimeSpriteDefinition mapping,
        ConsoleContractLimits limits)
    {
        if (targetWidth == 0 || targetHeight == 0)
            throw Unsupported("The upstream HTML image has a zero-sized destination.");

        int positiveWidth = AbsChecked(targetWidth);
        int positiveHeight = AbsChecked(targetHeight);
        int destinationX = targetWidth < 0 ? positiveWidth : 0;
        int destinationY = checked(y + (targetHeight < 0 ? positiveHeight : 0));
        destinationX = checked(destinationX + resolved.DestinationOffsetX * positiveWidth / resolved.DestinationWidth);
        destinationY = checked(destinationY + resolved.DestinationOffsetY * positiveHeight / resolved.DestinationHeight);
        return new SpriteNode(
            new ConsoleAssetId(resolved.AssetId),
            new ConsoleRect(resolved.SourceX, resolved.SourceY, resolved.SourceWidth, resolved.SourceHeight),
            new ConsoleRect(destinationX, destinationY, positiveWidth, positiveHeight),
            altText: SafeImageFallback(source, limits),
            hoverAssetId: hover is null ? null : new ConsoleAssetId(hover.AssetId),
            hoverSourceRect: hover is null ? null : new ConsoleRect(hover.SourceX, hover.SourceY, hover.SourceWidth, hover.SourceHeight),
            mappingAssetId: mapping is null ? null : new ConsoleAssetId(mapping.AssetId),
            mappingSourceRect: mapping is null ? null : new ConsoleRect(mapping.SourceX, mapping.SourceY, mapping.SourceWidth, mapping.SourceHeight),
            animationFrames: (resolved.AnimationFrames ?? Array.Empty<RuntimeSpriteFrame>()).Select(frame => new SpriteAnimationFrame(
                new ConsoleAssetId(frame.AssetId),
                new ConsoleRect(frame.SourceX, frame.SourceY, frame.SourceWidth, frame.SourceHeight),
                new ConsolePoint(frame.OffsetX, frame.OffsetY),
                frame.DurationMilliseconds)));
    }

    private static int ResolveLength(UpstreamHtmlLength? value, int fontSize, int defaultValue) =>
        value is null || value.Value.Value == 0
            ? defaultValue
            : ToPixels(value.Value, fontSize);

    private static int ResolveAspectWidth(RuntimeSpriteDefinition sprite, int height)
    {
        int positiveHeight = AbsChecked(height);
        int width = checked((int)((long)sprite.DestinationWidth * positiveHeight / sprite.DestinationHeight));
        return height < 0 ? -width : width;
    }

    private static int ToPixels(UpstreamHtmlLength value, int fontSize)
    {
        long result = value.IsPixels ? value.Value : (long)fontSize * value.Value / 100;
        if (result < int.MinValue || result > int.MaxValue)
            throw Unsupported("The upstream HTML length is outside the geometry range.");
        return (int)result;
    }

    private static int ToHorizontalPixels(
        UpstreamHtmlLength value,
        UpstreamHtmlTranslationContext context) =>
        ToPixels(value, context.FontSize);

    private static int ToVerticalPixels(
        UpstreamHtmlLength value,
        UpstreamHtmlTranslationContext context) =>
        ToPixels(value, context.LineHeight);

    private static int AbsChecked(int value) => value == int.MinValue
        ? throw Unsupported("The upstream HTML geometry is outside the positive range.")
        : Math.Abs(value);

    private static ConsoleColor? ToColor(int rgb) => rgb < 0
        ? null
        : ConsoleColor.FromRgb((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));

    private static string ResolveFontName(string value, ConsoleContractLimits limits)
    {
        // CloudEmuera S04: preserve the upstream font-face scope but do not
        // expose a game/package family to the structured contract. The
        // headless FontFactory measures every scope with the bound Session
        // face, and the browser must use the same family.
        return "session-default";
    }

    private static string SafeImageFallback(string value, ConsoleContractLimits limits)
    {
        if (value.Length > limits.MaxAltTextLength || value.Any(char.IsControl))
            return "image";
        return value;
    }

    private static ConsoleLineAlignment ToAlignment(UpstreamHtmlAlignment alignment) => alignment switch
    {
        UpstreamHtmlAlignment.Center => ConsoleLineAlignment.Center,
        UpstreamHtmlAlignment.Right => ConsoleLineAlignment.Right,
        _ => ConsoleLineAlignment.Left
    };

    private static UpstreamHtmlTranslationException Unsupported(string message) =>
        new("EMUERA_HTML_TRANSLATION_UNSUPPORTED", message);

    private sealed class TranslationCounter
    {
        private readonly ConsoleContractLimits limits;
        private int nodeCount;
        private int textLength;

        public TranslationCounter(ConsoleContractLimits limits) => this.limits = limits;

        public void Node()
        {
            nodeCount = checked(nodeCount + 1);
            if (nodeCount > limits.MaxBatchNodeCount)
                throw new UpstreamHtmlTranslationException("EMUERA_HTML_OUTPUT_LIMIT", "The translated HTML node count exceeds its limit.");
        }

        public void Text(string value)
        {
            if (value == null)
                return;
            textLength = checked(textLength + value.Length);
            if (textLength > limits.MaxHtmlTextLength)
                throw new UpstreamHtmlTranslationException("EMUERA_HTML_OUTPUT_LIMIT", "The translated HTML text exceeds its limit.");
        }

        public void Depth(int depth)
        {
            if (depth > limits.MaxNodeDepth)
                throw new UpstreamHtmlTranslationException("EMUERA_HTML_DEPTH_LIMIT", "The translated HTML node depth exceeds its limit.");
        }
    }
}

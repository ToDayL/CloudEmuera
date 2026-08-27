namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleTooltipHorizontalAlignment
{
    Left,
    Center,
    Right
}

public enum ConsoleTooltipVerticalAlignment
{
    Top,
    Center,
    Bottom
}

public enum ConsoleTooltipTrimming
{
    None,
    CharacterEllipsis,
    WordEllipsis,
    PathEllipsis
}

/// <summary>Browser-neutral projection of the supported WinForms TextFormatFlags semantics.</summary>
public sealed record ConsoleTooltipTextFormat(
    ConsoleTooltipHorizontalAlignment Horizontal = ConsoleTooltipHorizontalAlignment.Left,
    ConsoleTooltipVerticalAlignment Vertical = ConsoleTooltipVerticalAlignment.Top,
    bool Wrap = false,
    ConsoleTooltipTrimming Trimming = ConsoleTooltipTrimming.None,
    bool ExpandTabs = false,
    bool RightToLeft = false)
{
    // Values are frozen from System.Windows.Forms.TextFormatFlags. Bits that
    // only affect GDI internals are accepted at this boundary but never cross
    // the protocol as a desktop-framework enum.
    private const long HorizontalCenter = 0x00000001;
    private const long Right = 0x00000002;
    private const long VerticalCenter = 0x00000004;
    private const long Bottom = 0x00000008;
    private const long WordBreak = 0x00000010;
    private const long SingleLine = 0x00000020;
    private const long ExpandTabsFlag = 0x00000040;
    private const long EndEllipsis = 0x00008000;
    private const long PathEllipsis = 0x00004000;
    private const long RightToLeftFlag = 0x00020000;
    private const long WordEllipsis = 0x00040000;
    private const long KnownGdiOnly = 0x00000100 | 0x00000200 | 0x00000800 | 0x00002000 |
        0x00010000 | 0x00080000 | 0x00100000 | 0x01000000 | 0x02000000 |
        0x10000000 | 0x20000000;
    private const long SupportedMask = HorizontalCenter | Right | VerticalCenter | Bottom | WordBreak |
        SingleLine | ExpandTabsFlag | EndEllipsis | PathEllipsis | RightToLeftFlag | WordEllipsis | KnownGdiOnly;

    public static ConsoleTooltipTextFormat FromTextFormatFlags(long flags)
    {
        if (flags < 0 || (flags & ~SupportedMask) != 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidTooltipFormat, "The tooltip format contains unknown flags.");
        if ((flags & HorizontalCenter) != 0 && (flags & Right) != 0 ||
            (flags & VerticalCenter) != 0 && (flags & Bottom) != 0)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidTooltipFormat, "The tooltip format contains conflicting alignment flags.");

        ConsoleTooltipTrimming trimming = (flags & PathEllipsis) != 0
            ? ConsoleTooltipTrimming.PathEllipsis
            : (flags & WordEllipsis) != 0
                ? ConsoleTooltipTrimming.WordEllipsis
                : (flags & EndEllipsis) != 0
                    ? ConsoleTooltipTrimming.CharacterEllipsis
                    : ConsoleTooltipTrimming.None;
        return new ConsoleTooltipTextFormat(
            (flags & HorizontalCenter) != 0 ? ConsoleTooltipHorizontalAlignment.Center :
                (flags & Right) != 0 ? ConsoleTooltipHorizontalAlignment.Right : ConsoleTooltipHorizontalAlignment.Left,
            (flags & VerticalCenter) != 0 ? ConsoleTooltipVerticalAlignment.Center :
                (flags & Bottom) != 0 ? ConsoleTooltipVerticalAlignment.Bottom : ConsoleTooltipVerticalAlignment.Top,
            (flags & WordBreak) != 0 && (flags & SingleLine) == 0,
            trimming,
            (flags & ExpandTabsFlag) != 0,
            (flags & RightToLeftFlag) != 0);
    }
}

public sealed record ConsoleTooltipPresentation
{
    public ConsoleTooltipPresentation(
        bool customEnabled = false,
        ConsoleColor? foreground = null,
        ConsoleColor? background = null,
        int delayMilliseconds = 500,
        int durationMilliseconds = 0,
        string fontFamily = "session-default",
        int fontSize = 16,
        ConsoleTooltipTextFormat? textFormat = null,
        bool imageMode = false,
        long revision = 0)
    {
        if (delayMilliseconds is < 0 or > ConsoleContractLimits.MaxTooltipDelayMilliseconds)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidTooltipTiming, "The tooltip delay is outside the characterized range.");
        if (durationMilliseconds is < 0 or > ConsoleContractLimits.MaxTooltipDurationMilliseconds)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidTooltipTiming, "The tooltip duration is outside the characterized range.");
        ConsoleContractValidation.ValidateLogicalName(fontFamily, nameof(fontFamily), ConsoleContractLimits.Default.MaxFontFamilyLength);
        if (fontSize is < 1 or > ConsoleContractLimits.MaxTooltipFontSize)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidFont, "The tooltip font size is outside its limit.");
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        CustomEnabled = customEnabled;
        Foreground = foreground ?? new ConsoleColor(0, 0, 0);
        Background = background ?? new ConsoleColor(255, 255, 225);
        DelayMilliseconds = delayMilliseconds;
        DurationMilliseconds = durationMilliseconds;
        FontFamily = fontFamily;
        FontSize = fontSize;
        TextFormat = textFormat ?? new ConsoleTooltipTextFormat();
        ImageMode = imageMode;
        Revision = revision;
    }

    public bool CustomEnabled { get; }
    public ConsoleColor Foreground { get; }
    public ConsoleColor Background { get; }
    public int DelayMilliseconds { get; }
    public int DurationMilliseconds { get; }
    public string FontFamily { get; }
    public int FontSize { get; }
    public ConsoleTooltipTextFormat TextFormat { get; }
    public bool ImageMode { get; }
    public long Revision { get; }

    public ConsoleTooltipPresentation Next(
        bool? customEnabled = null,
        ConsoleColor? foreground = null,
        ConsoleColor? background = null,
        int? delayMilliseconds = null,
        int? durationMilliseconds = null,
        int? fontSize = null,
        ConsoleTooltipTextFormat? textFormat = null,
        bool? imageMode = null) => new(
            customEnabled ?? CustomEnabled,
            foreground ?? Foreground,
            background ?? Background,
            delayMilliseconds ?? DelayMilliseconds,
            durationMilliseconds ?? DurationMilliseconds,
            FontFamily,
            fontSize ?? FontSize,
            textFormat ?? TextFormat,
            imageMode ?? ImageMode,
            checked(Revision + 1));
}

public sealed record ConsoleTooltipResource
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public ConsoleTooltipResource(int graphicsId, IEnumerable<byte> pngData, int width, int height, long revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(graphicsId);
        ArgumentNullException.ThrowIfNull(pngData);
        byte[] copy = pngData.ToArray();
        if (width is < 1 or > ConsoleContractLimits.MaxTooltipImageDimension ||
            height is < 1 or > ConsoleContractLimits.MaxTooltipImageDimension)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidImageDimension, "The tooltip image dimensions are outside their limit.");
        if (copy.Length is < 8 or > ConsoleContractLimits.MaxTooltipResourceBytes || !copy.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidImagePayload, "The tooltip image is not a bounded PNG.");
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        GraphicsId = graphicsId;
        PngData = Array.AsReadOnly(copy);
        Width = width;
        Height = height;
        Revision = revision;
    }

    public int GraphicsId { get; }
    public IReadOnlyList<byte> PngData { get; }
    public int Width { get; }
    public int Height { get; }
    public long Revision { get; }
}

/// <summary>Thin runtime seam used by the vendored/headless Emuera console.</summary>
public interface ITooltipStateSink
{
    ConsoleTooltipPresentation TooltipPresentation { get; }
    void SetTooltipCustom(bool enabled);
    void SetTooltipColor(ConsoleColor foreground, ConsoleColor background);
    void SetTooltipDelay(int milliseconds);
    void SetTooltipDuration(int milliseconds);
    void SetTooltipFont(string requestedName);
    void SetTooltipFontSize(long size);
    void SetTooltipFormat(long textFormatFlags);
    void SetTooltipImageMode(bool enabled);
}

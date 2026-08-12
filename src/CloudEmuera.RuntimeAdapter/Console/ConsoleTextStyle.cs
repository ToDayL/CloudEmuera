namespace CloudEmuera.RuntimeAdapter;

[Flags]
public enum ConsoleFontStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8
}

public sealed record ConsoleTextStyle
{
    public ConsoleTextStyle(
        ConsoleColor? foreground = null,
        ConsoleColor? background = null,
        ConsoleFontStyle decorations = ConsoleFontStyle.None,
        string? fontFamily = null,
        int fontSize = 16,
        int lineHeight = 0)
    {
        ConsoleContractValidation.ValidateFontStyle(decorations);
        string family = string.IsNullOrEmpty(fontFamily) ? "default" : fontFamily;
        ConsoleContractValidation.ValidateLogicalName(family, nameof(fontFamily), ConsoleContractLimits.Default.MaxFontFamilyLength);
        if (fontSize <= 0 || fontSize > 256 || lineHeight < 0 || lineHeight > 512)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidFont, "Font metrics are outside their limits.");
        Foreground = foreground;
        Background = background;
        Decorations = decorations;
        FontFamily = family;
        FontSize = fontSize;
        LineHeight = lineHeight;
    }

    public static ConsoleTextStyle Default { get; } = new();

    public ConsoleColor? Foreground { get; }

    public ConsoleColor? Background { get; }

    public ConsoleFontStyle Decorations { get; }

    public string FontFamily { get; }

    public int FontSize { get; }

    public int LineHeight { get; }

    public ConsoleFontSpec Font => new(FontFamily, FontSize, LineHeight);

    public bool IsDefault => Foreground is null && Background is null && Decorations == ConsoleFontStyle.None &&
        FontFamily == "default" && FontSize == 16 && LineHeight == 0;

    public ConsoleTextStyle WithDecorations(ConsoleFontStyle decorations) =>
        new(Foreground, Background, decorations, FontFamily, FontSize, LineHeight);
}

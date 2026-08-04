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
        ConsoleFontStyle decorations = ConsoleFontStyle.None)
    {
        ConsoleContractValidation.ValidateFontStyle(decorations);
        Foreground = foreground;
        Background = background;
        Decorations = decorations;
    }

    public static ConsoleTextStyle Default { get; } = new();

    public ConsoleColor? Foreground { get; }

    public ConsoleColor? Background { get; }

    public ConsoleFontStyle Decorations { get; }

    public bool IsDefault => Foreground is null && Background is null && Decorations == ConsoleFontStyle.None;

    public ConsoleTextStyle WithDecorations(ConsoleFontStyle decorations) =>
        new(Foreground, Background, decorations);
}

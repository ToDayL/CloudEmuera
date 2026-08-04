namespace CloudEmuera.RuntimeAdapter;

/// <summary>Platform-neutral normalized RGBA color.</summary>
public readonly record struct ConsoleColor
{
    public ConsoleColor(byte red, byte green, byte blue, byte alpha = byte.MaxValue)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    public byte Alpha { get; }

    public byte R => Red;

    public byte G => Green;

    public byte B => Blue;

    public byte A => Alpha;

    public static ConsoleColor FromRgb(byte red, byte green, byte blue) => new(red, green, blue);

    public static ConsoleColor FromRgba(byte red, byte green, byte blue, byte alpha) => new(red, green, blue, alpha);

    public override string ToString() => $"#{Red:X2}{Green:X2}{Blue:X2}{Alpha:X2}";
}

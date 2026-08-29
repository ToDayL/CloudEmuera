namespace CloudEmuera.RuntimeAdapter;

/// <summary>Controls how a Session's authoritative runtime layout width is selected at startup.</summary>
public enum RuntimeWidthMode
{
    /// <summary>Use the current browser width, bounded by the game's WindowX.</summary>
    Adaptive = 0,
    Max = 1,
    Custom = 2,
    /// <summary>Use the game's WindowX exactly, without a browser-width cap.</summary>
    Original = 3,

    /// <summary>Legacy ORIGIN name for the adaptive mode.</summary>
    Origin = Adaptive,
}

public static class RuntimeWidthPolicy
{
    public const int MinimumWidth = 240;
    public const int MaximumBrowserWidth = 16_384;
    public const int MaxModeWidth = 2_000;

    public static bool IsValid(RuntimeWidthMode mode, int? customWidth) =>
        Enum.IsDefined(mode) &&
        (mode == RuntimeWidthMode.Custom
            ? customWidth is >= MinimumWidth and <= MaximumBrowserWidth
            : customWidth is null);

    public static int Resolve(int configuredWidth, int browserWidth, RuntimeWidthMode mode, int? customWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredWidth);
        if (browserWidth != 0 && (browserWidth is < MinimumWidth or > MaximumBrowserWidth))
            throw new ArgumentOutOfRangeException(nameof(browserWidth));
        if (!IsValid(mode, customWidth))
            throw new ArgumentException("The runtime width configuration is invalid.", nameof(customWidth));

        // A zero browser width is used by non-browser validation hosts. Treat it
        // as an unbounded viewport so each mode still has its own semantics.
        int availableWidth = browserWidth == 0 ? int.MaxValue : browserWidth;
        return mode switch
        {
            RuntimeWidthMode.Original => configuredWidth,
            RuntimeWidthMode.Max => Math.Min(MaxModeWidth, availableWidth),
            RuntimeWidthMode.Adaptive => Math.Min(configuredWidth, availableWidth),
            RuntimeWidthMode.Custom => customWidth!.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }
}

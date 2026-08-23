namespace CloudEmuera.RuntimeAdapter;

/// <summary>Controls how a Session's authoritative runtime layout width is selected at startup.</summary>
public enum RuntimeWidthMode
{
    Origin = 0,
    Max = 1,
    Custom = 2,
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
        if (browserWidth is < MinimumWidth or > MaximumBrowserWidth)
            throw new ArgumentOutOfRangeException(nameof(browserWidth));
        if (!IsValid(mode, customWidth))
            throw new ArgumentException("The runtime width configuration is invalid.", nameof(customWidth));

        return mode switch
        {
            RuntimeWidthMode.Origin => Math.Min(configuredWidth, browserWidth),
            RuntimeWidthMode.Max => Math.Min(MaxModeWidth, browserWidth),
            RuntimeWidthMode.Custom => Math.Min(customWidth!.Value, browserWidth),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }
}

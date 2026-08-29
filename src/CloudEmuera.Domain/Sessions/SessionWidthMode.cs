namespace CloudEmuera.Domain.Sessions;

public enum SessionWidthMode
{
    /// <summary>Use the current browser width, bounded by the game's WindowX.</summary>
    Adaptive = 0,
    Max = 1,
    Custom = 2,
    /// <summary>Use the game's WindowX exactly, without a browser-width cap.</summary>
    Original = 3,

    /// <summary>
    /// Legacy name accepted for persisted records and older clients. It has the
    /// same meaning as <see cref="Adaptive"/>; new responses use ADAPTIVE.
    /// </summary>
    Origin = Adaptive,
}

public static class SessionWidthConfiguration
{
    public const int MinimumWidth = 240;
    public const int MaximumWidth = 16_384;
    public const int MaxModeWidth = 2_000;

    public static bool IsValid(SessionWidthMode mode, int? customWidth) =>
        Enum.IsDefined(mode) &&
        (mode == SessionWidthMode.Custom
            ? customWidth is >= MinimumWidth and <= MaximumWidth
            : customWidth is null);
}

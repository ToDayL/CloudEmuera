namespace CloudEmuera.Domain.Sessions;

public enum SessionWidthMode
{
    Origin = 0,
    Max = 1,
    Custom = 2,
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

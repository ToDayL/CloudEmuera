namespace CloudEmuera.Infrastructure.Games;

internal static class GameContentScanLimits
{
    public const int MaxEntryCount = 1_000_000;
    public const int MaxDirectoryDepth = 32;
    public const long MaxSingleFileBytes = 1L * 1024 * 1024 * 1024;
    public const long MaxTotalBytes = 16L * 1024 * 1024 * 1024;
}

internal sealed class GameContentLimitException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

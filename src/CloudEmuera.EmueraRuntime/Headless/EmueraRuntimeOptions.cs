using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.EmueraRuntime.Headless;

public static class EmueraCompatibilityProfiles
{
    public const string V18Compatible = "v18-compatible";
    public const string EmEeCurrent = "em-ee-current";

    public static bool IsSupported(string value) =>
        value is V18Compatible or EmEeCurrent;
}

public sealed record EmueraRuntimeOptions
{
    public EmueraRuntimeOptions(
        RuntimePaths paths,
        IGameConsole console,
        IRuntimeFileSystem fileSystem,
        IRuntimeClock clock,
        IRuntimeImagePort imagePort,
        IRuntimeAudioPort audioPort,
        string compatibilityProfile,
        TimeSpan initializationDeadline,
        TimeSpan runDeadline,
        Action<EmueraRuntimeDiagnostic>? diagnosticSink = null,
        int browserWidth = 0, int fontSize = 18, int lineHeight = 19, double halfWidthPx = 0, double fullWidthPx = 0)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Console = console ?? throw new ArgumentNullException(nameof(console));
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ImagePort = imagePort ?? throw new ArgumentNullException(nameof(imagePort));
        AudioPort = audioPort ?? throw new ArgumentNullException(nameof(audioPort));
        if (!EmueraCompatibilityProfiles.IsSupported(compatibilityProfile))
        {
            throw new ArgumentException("The compatibility profile is not supported.", nameof(compatibilityProfile));
        }

        ValidateDeadline(initializationDeadline, nameof(initializationDeadline));
        ValidateDeadline(runDeadline, nameof(runDeadline));
        CompatibilityProfile = compatibilityProfile;
        InitializationDeadline = initializationDeadline;
        RunDeadline = runDeadline;
        DiagnosticSink = diagnosticSink;
        if (browserWidth < 0 || browserWidth > 16_384)
            throw new ArgumentOutOfRangeException(nameof(browserWidth));
        BrowserWidth = browserWidth;
        if (fontSize is < 8 or > 72 || lineHeight < fontSize || lineHeight > 128 || !double.IsFinite(halfWidthPx) || !double.IsFinite(fullWidthPx) || halfWidthPx < 0 || fullWidthPx < 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        FontSize = fontSize; LineHeight = lineHeight; HalfWidthPx = halfWidthPx; FullWidthPx = fullWidthPx;
    }

    public RuntimePaths Paths { get; }
    public IGameConsole Console { get; }
    public IRuntimeFileSystem FileSystem { get; }
    public IRuntimeClock Clock { get; }
    public IRuntimeImagePort ImagePort { get; }
    public IRuntimeAudioPort AudioPort { get; }
    public string CompatibilityProfile { get; }
    public TimeSpan InitializationDeadline { get; }
    public TimeSpan RunDeadline { get; }
    public Action<EmueraRuntimeDiagnostic>? DiagnosticSink { get; }
    public int BrowserWidth { get; }
    public int FontSize { get; }
    public int LineHeight { get; }
    public double HalfWidthPx { get; }
    public double FullWidthPx { get; }
    internal Action? UpstreamGateAcquired { get; init; }

    private static void ValidateDeadline(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A runtime deadline must be positive or infinite.");
        }
    }
}

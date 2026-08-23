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
        int browserWidth = 0, int fontSize = 18, int lineHeight = 19,
        string fontFaceId = "sarasa-fixed-sc-1.0.40-regular", string fontCatalogDigest = "",
        string runtimeFontPath = "", string runtimeFontFamilyName = "", string webFontAssetDigest = "")
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
        if (fontSize is < 8 or > 72 || lineHeight < fontSize || lineHeight > 128)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        FontSize = fontSize; LineHeight = lineHeight;
        if (string.IsNullOrWhiteSpace(fontFaceId) || fontFaceId.Length > 128 || fontFaceId.Any(char.IsWhiteSpace) || fontFaceId.Contains('\0'))
            throw new ArgumentException("The runtime font face ID is invalid.", nameof(fontFaceId));
        if (!string.IsNullOrEmpty(fontCatalogDigest) && (fontCatalogDigest.Length != 64 || fontCatalogDigest.Any(character => character is < '0' or > '9' and < 'a' or > 'f')))
            throw new ArgumentException("The runtime font catalog digest is invalid.", nameof(fontCatalogDigest));
        if (!string.IsNullOrWhiteSpace(runtimeFontPath) && !Path.IsPathFullyQualified(runtimeFontPath))
            throw new ArgumentException("The runtime font path must be absolute.", nameof(runtimeFontPath));
        FontFaceId = fontFaceId;
        FontCatalogDigest = fontCatalogDigest;
        RuntimeFontPath = runtimeFontPath ?? string.Empty;
        RuntimeFontFamilyName = runtimeFontFamilyName ?? string.Empty;
        if (!string.IsNullOrEmpty(webFontAssetDigest) && (webFontAssetDigest.Length != 64 || webFontAssetDigest.Any(character => character is < '0' or > '9' and < 'a' or > 'f')))
            throw new ArgumentException("The runtime web font asset digest is invalid.", nameof(webFontAssetDigest));
        WebFontAssetDigest = webFontAssetDigest ?? string.Empty;
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
    public string FontFaceId { get; }
    public string FontCatalogDigest { get; }
    public string RuntimeFontPath { get; }
    public string RuntimeFontFamilyName { get; }
    public string WebFontAssetDigest { get; }
    internal Action? UpstreamGateAcquired { get; init; }

    private static void ValidateDeadline(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A runtime deadline must be positive or infinite.");
        }
    }
}

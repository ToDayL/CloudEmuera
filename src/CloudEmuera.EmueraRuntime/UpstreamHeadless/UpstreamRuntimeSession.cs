// CloudEmuera headless entry point for the pinned upstream loader/interpreter.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.UI.Game.Image;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

public sealed record RuntimeSpriteDefinition(
    string AssetId,
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight,
    int DestinationOffsetX,
    int DestinationOffsetY,
    int DestinationWidth,
    int DestinationHeight,
    IReadOnlyList<RuntimeSpriteFrame> AnimationFrames = null);

public sealed record RuntimeSpriteFrame(
    string AssetId,
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight,
    int OffsetX,
    int OffsetY,
    int DurationMilliseconds);

public sealed class UpstreamRuntimeSession : IDisposable
{
    private static readonly SemaphoreSlim RuntimeGate = new(1, 1);
    private readonly IGameConsole adapter;
    private readonly IRuntimeClock clock;
    private readonly IRuntimeAudioPort audioPort;
    private readonly CancellationToken cancellationToken;
    private readonly Func<string, RuntimeSpriteDefinition> imageResolver;
    private readonly Action runtimeGateAcquired;
    private readonly int browserWidth;
    private readonly int fontSize;
    private readonly int lineHeight;
    private readonly string fontFaceId;
    private readonly string fontCatalogDigest;
    private readonly string runtimeFontPath;
    private readonly string runtimeFontFamilyName;
    private readonly string webFontAssetDigest;
    private RuntimeDebugTrace debugTrace;
    private EmueraConsole console;
    private Process process;
    private bool ownsGate;
    private int initializationMessageCount;

    public UpstreamRuntimeSession(
        IGameConsole adapter,
        IRuntimeClock clock,
        IRuntimeAudioPort audioPort,
        CancellationToken cancellationToken,
        Func<string, RuntimeSpriteDefinition> imageResolver,
        Action runtimeGateAcquired = null,
        int browserWidth = 0, int fontSize = 16, int lineHeight = 16,
        string fontFaceId = "sarasa-fixed-sc-1.0.40-regular", string fontCatalogDigest = "",
        string runtimeFontPath = "", string runtimeFontFamilyName = "", string webFontAssetDigest = "")
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.audioPort = audioPort ?? throw new ArgumentNullException(nameof(audioPort));
        this.cancellationToken = cancellationToken;
        this.imageResolver = imageResolver;
        this.runtimeGateAcquired = runtimeGateAcquired;
        if (browserWidth < 0 || browserWidth > 16_384)
            throw new ArgumentOutOfRangeException(nameof(browserWidth));
        this.browserWidth = browserWidth;
        this.fontSize = fontSize; this.lineHeight = lineHeight;
        this.fontFaceId = fontFaceId ?? string.Empty;
        this.fontCatalogDigest = fontCatalogDigest ?? string.Empty;
        this.runtimeFontPath = runtimeFontPath ?? string.Empty;
        this.runtimeFontFamilyName = runtimeFontFamilyName ?? string.Empty;
        this.webFontAssetDigest = webFontAssetDigest ?? string.Empty;
    }

    public async Task<bool> InitializeAsync(RuntimePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.ValidateSessionRoot();
        if (paths.SaveLayout == RuntimeSaveLayout.SavDirectory && HasRootNativeSave(paths.SessionRoot))
        {
            throw new UpstreamSaveLayoutConflictException();
        }

        await RuntimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ownsGate = true;
        runtimeGateAcquired?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        HeadlessPathResolver.Configure(paths.SessionRoot);
        MinorShift.Emuera.Program.ConfigureHeadless(
            paths.SessionRoot,
            paths.CsvRoot,
            paths.ErbRoot,
            paths.TemporaryRoot,
            paths.ResourceRoot ?? Path.Combine(paths.SessionRoot, "resources"),
            paths.SoundRoot ?? Path.Combine(paths.SessionRoot, "sound"),
            paths.FontRoot ?? Path.Combine(paths.SessionRoot, "font"));
        MinorShift.Emuera.GlobalStatic.Reset();
        FontFactory.ClearFont();
        HeadlessFontMetrics.Clear();
        if (!string.IsNullOrWhiteSpace(runtimeFontPath))
        {
            if (!Path.IsPathFullyQualified(runtimeFontPath) || !File.Exists(runtimeFontPath))
                throw new InvalidDataException($"The runtime font asset for '{fontFaceId}' is unavailable.");
            GlobalStatic.Pfc = new PrivateFontCollection();
            GlobalStatic.Pfc.AddFontFile(runtimeFontPath);
            if (string.IsNullOrWhiteSpace(runtimeFontFamilyName) ||
                !Array.Exists(GlobalStatic.Pfc.Families, family => string.Equals(family.Name, runtimeFontFamilyName, StringComparison.Ordinal)))
                throw new InvalidDataException($"The runtime font family for '{fontFaceId}' is unavailable (catalog='{runtimeFontFamilyName}', loaded='{string.Join(",", GlobalStatic.Pfc.Families.Select(family => family.Name))}').");
            HeadlessFontMetrics.Configure(runtimeFontPath, runtimeFontFamilyName);
        }
        ConfigData.ResetHeadless();
        ConfigData.Instance.LoadConfig(applyConfig: false);
        if (!string.IsNullOrWhiteSpace(runtimeFontFamilyName))
            ConfigData.Instance.GetConfigItem(ConfigCode.FontName).SetValue(runtimeFontFamilyName);
        AConfigItem windowWidth = ConfigData.Instance.GetConfigItem(ConfigCode.WindowX);
        AConfigItem configuredFontSize = ConfigData.Instance.GetConfigItem(ConfigCode.FontSize);
        AConfigItem configuredLineHeight = ConfigData.Instance.GetConfigItem(ConfigCode.LineHeight);
        int configuredWidth = windowWidth.GetValue<int>();
        int effectiveWidth = browserWidth > 0 ? Math.Min(configuredWidth, browserWidth) : configuredWidth;
        windowWidth.SetValue(effectiveWidth);
        configuredFontSize.SetValue(fontSize);
        configuredLineHeight.SetValue(lineHeight);
        Config.SetConfig(ConfigData.Instance);
        if (!string.IsNullOrWhiteSpace(runtimeFontPath))
        {
            Font defaultFont = Config.DefaultFont ?? throw new InvalidDataException("The selected runtime font could not create Config.DefaultFont.");
            if (!string.Equals(defaultFont.FontFamily.Name, runtimeFontFamilyName, StringComparison.Ordinal) ||
                defaultFont.Unit != GraphicsUnit.Pixel || Math.Abs(defaultFont.Size - Config.FontSize) > 0.01f)
                throw new InvalidDataException($"The selected runtime font '{fontFaceId}' did not bind Config.DefaultFont.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeSaveLayout actualLayout = Config.UseSaveFolder
            ? RuntimeSaveLayout.SavDirectory
            : RuntimeSaveLayout.Root;
        if (actualLayout != paths.SaveLayout)
        {
            throw new UpstreamSaveLayoutMismatchException(paths.SaveLayout, actualLayout);
        }

        JSONConfig.Data = new JSONConfigData();
        HeadlessAudioBridge.Configure(audioPort, cancellationToken);
        debugTrace = RuntimeDebugTrace.CreateWhenEnabled(paths.SessionRoot);
        debugTrace?.Activate();
        debugTrace?.RecordRuntimeWidth(configuredWidth, browserWidth, Config.WindowX, Config.DrawableWidth);
        Preload.Clear();
        await Preload.Load(MinorShift.Emuera.Program.ErbDir, cancellationToken).ConfigureAwait(false);
        await Preload.Load(MinorShift.Emuera.Program.CsvDir, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        console = new EmueraConsole(adapter, clock, cancellationToken, imageResolver, Config.WindowX, Config.WindowY,
            fontFaceId, fontCatalogDigest, webFontAssetDigest);
        process = new Process(console);
        process.SetHeadlessCancellationToken(cancellationToken);
        MinorShift.Emuera.GlobalStatic.Process = process;
        bool initialized = await process.Initialize(null).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!initialized)
        {
            string details = string.Join(" | ", console.RuntimeMessages.Take(8));
            throw new InvalidDataException($"The pinned upstream Emuera loader rejected the controlled game content. {details}");
        }

        if (console.RuntimeMessages.Count > 0)
        {
            string details = string.Join(" | ", console.RuntimeMessages.Take(8));
            throw new InvalidDataException($"The pinned upstream Emuera loader reported script diagnostics. {details}");
        }

        // Stock Emuera treats @SYSTEM_TITLE as optional: when the label is absent it
        // falls back to the GAMEBASE-derived title screen. The headless session must
        // not reject games that legitimately omit it (P1-04 GAME-007 compatibility).
        initializationMessageCount = console.RuntimeMessages.Count;
        console.BeginExecutionOutput();
        return initialized;
    }

    public void Run(CancellationToken runCancellationToken)
    {
        console.SetCancellationToken(runCancellationToken);
        HeadlessAudioBridge.SetCancellationToken(runCancellationToken);
        Process runtimeProcess = process ?? throw new InvalidOperationException("The upstream runtime is not initialized.");
        runtimeProcess.SetHeadlessCancellationToken(runCancellationToken);
        runtimeProcess.DoScript();
        console.PrintFlush(force: false);
        runCancellationToken.ThrowIfCancellationRequested();
        if (console.HasFatalError)
        {
            string details = string.Join(" | ", console.RuntimeMessages.Skip(initializationMessageCount));
            throw new InvalidDataException($"The upstream interpreter reported an execution error. {details}");
        }
        if (HeadlessAudioBridge.UnsupportedRequested)
            throw new NotSupportedException("The configured audio port reported playback as unsupported.");
    }

    public IReadOnlyList<string> InitializationMessages => console?.RuntimeMessages ?? Array.Empty<string>();
    public IReadOnlyList<string> InitializationWarnings => console?.RuntimeWarnings ?? Array.Empty<string>();

    public void Dispose()
    {
        try
        {
            // CloudEmuera ADR-0019: each headless session must release the
            // upstream static Sprite/Graphics registry before the next fixture
            // or Worker session can acquire the runtime gate.
            AppContents.UnloadContents();
            console?.Dispose();
            console = null;
            FontFactory.ClearFont();
            HeadlessFontMetrics.Clear();
            if (GlobalStatic.Pfc is not null)
            {
                GlobalStatic.Pfc.Dispose();
                GlobalStatic.Pfc = null;
            }
            MinorShift.Emuera.GlobalStatic.Reset();
            Preload.Clear();
            HeadlessPathResolver.Reset();
        }
        finally
        {
            debugTrace?.Dispose();
            debugTrace = null;
            if (ownsGate)
            {
                ownsGate = false;
                RuntimeGate.Release();
            }
        }
    }

    private static bool HasRootNativeSave(string sessionRoot)
    {
        if (File.Exists(Path.Combine(sessionRoot, "global.sav")))
        {
            return true;
        }

        return Directory.EnumerateFiles(sessionRoot, "save*.sav", SearchOption.TopDirectoryOnly).Any();
    }
}

public sealed class UpstreamSaveLayoutMismatchException : Exception
{
    public UpstreamSaveLayoutMismatchException(RuntimeSaveLayout expected, RuntimeSaveLayout actual)
        : base($"The runtime save layout does not match the loaded emuera.config (expected {expected}, actual {actual}).")
    {
        Expected = expected;
        Actual = actual;
    }

    public RuntimeSaveLayout Expected { get; }

    public RuntimeSaveLayout Actual { get; }
}

public sealed class UpstreamSaveLayoutConflictException : Exception
{
    public UpstreamSaveLayoutConflictException()
        : base("The sav-directory layout found native save files at the SessionRoot level.")
    {
    }
}

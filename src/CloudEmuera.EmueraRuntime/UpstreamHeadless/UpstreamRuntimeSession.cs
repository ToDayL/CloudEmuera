// CloudEmuera headless entry point for the pinned upstream loader/interpreter.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Utils;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

public sealed class UpstreamRuntimeSession : IDisposable
{
    private static readonly SemaphoreSlim RuntimeGate = new(1, 1);
    private readonly IGameConsole adapter;
    private readonly IRuntimeClock clock;
    private readonly IRuntimeAudioPort audioPort;
    private readonly CancellationToken cancellationToken;
    private readonly Func<string, (string AssetId, int Width, int Height)?> imageResolver;
    private readonly Action runtimeGateAcquired;
    private EmueraConsole console;
    private Process process;
    private bool ownsGate;
    private int initializationMessageCount;

    public UpstreamRuntimeSession(
        IGameConsole adapter,
        IRuntimeClock clock,
        IRuntimeAudioPort audioPort,
        CancellationToken cancellationToken,
        Func<string, (string AssetId, int Width, int Height)?> imageResolver,
        Action runtimeGateAcquired = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.audioPort = audioPort ?? throw new ArgumentNullException(nameof(audioPort));
        this.cancellationToken = cancellationToken;
        this.imageResolver = imageResolver;
        this.runtimeGateAcquired = runtimeGateAcquired;
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
        MinorShift.Emuera.Program.ConfigureHeadless(
            paths.SessionRoot,
            paths.CsvRoot,
            paths.ErbRoot,
            paths.TemporaryRoot,
            paths.ResourceRoot ?? Path.Combine(paths.SessionRoot, "resources"),
            paths.SoundRoot ?? Path.Combine(paths.SessionRoot, "sound"),
            paths.FontRoot ?? Path.Combine(paths.SessionRoot, "font"));
        MinorShift.Emuera.GlobalStatic.Reset();
        ConfigData.ResetHeadless();
        ConfigData.Instance.LoadConfig();
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
        Preload.Clear();
        await Preload.Load(MinorShift.Emuera.Program.ErbDir, cancellationToken).ConfigureAwait(false);
        await Preload.Load(MinorShift.Emuera.Program.CsvDir, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        console = new EmueraConsole(adapter, clock, cancellationToken, imageResolver);
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
        if (console.RuntimeMessages.Count > initializationMessageCount)
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
            MinorShift.Emuera.GlobalStatic.Reset();
            Preload.Clear();
        }
        finally
        {
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

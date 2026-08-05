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

    public async Task<bool> InitializeAsync(
        string configurationRoot,
        string csvRoot,
        string erbRoot,
        string temporaryRoot,
        string resourceRoot,
        string soundRoot,
        string fontRoot)
    {
        await RuntimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ownsGate = true;
        runtimeGateAcquired?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePrivateViewRoots(
            configurationRoot, csvRoot, erbRoot, temporaryRoot, resourceRoot, soundRoot, fontRoot);
        MinorShift.Emuera.Program.ConfigureHeadless(
            configurationRoot, csvRoot, erbRoot, temporaryRoot, resourceRoot, soundRoot, fontRoot);
        MinorShift.Emuera.GlobalStatic.Reset();
        ConfigData.ResetHeadless();
        ConfigData.Instance.LoadConfig();
        cancellationToken.ThrowIfCancellationRequested();
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
        if (initialized && process.LabelDictionary.GetNonEventLabel("SYSTEM_TITLE") is null)
        {
            string details = string.Join(" | ", console.RuntimeMessages);
            throw new InvalidDataException($"The upstream ERB loader did not produce SYSTEM_TITLE. {details}");
        }
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

    private static void ValidatePrivateViewRoots(string configurationRoot, params string[] roots)
    {
        string normalizedConfiguration = Path.GetFullPath(configurationRoot);
        string viewRoot = Directory.GetParent(normalizedConfiguration)?.FullName
            ?? throw new ArgumentException("The headless configuration root must have a private parent directory.", nameof(configurationRoot));
        foreach (string root in roots.Prepend(configurationRoot))
        {
            string relative = Path.GetRelativePath(viewRoot, Path.GetFullPath(root));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new ArgumentException("Every upstream runtime root must be contained by one session-private file view.", nameof(roots));
        }
    }
}

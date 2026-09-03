using CloudEmuera.Debugging.Contracts;
using CloudEmuera.Contracts.Realtime;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;
using System.Text.Json;

namespace CloudEmuera.Worker;

internal sealed class WorkerDebugTraceRecorder : IConsoleInputTraceObserver, IDisposable
{
    private readonly DebugTraceWriter writer;
    private readonly object sync = new();
    private readonly Dictionary<string, (long Ordinal, long OpenedElapsed)> prompts = new(StringComparer.Ordinal);
    private long ordinal;
    private readonly long started = Environment.TickCount64;

    public WorkerDebugTraceRecorder(WorkerBootstrapDocument bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        writer = new DebugTraceWriter(bootstrap.DebugInputTracePath, new DebugTraceHeader
        {
            CaptureId = bootstrap.DebugCaptureId,
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = bootstrap.SessionId,
            OriginalWorkerEpoch = bootstrap.WorkerEpoch,
            CloudEmueraVersion = typeof(WorkerDebugTraceRecorder).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            RuntimeIntegrationVersion = RuntimeBaseline.CloudEmueraIntegrationVersion,
            UpstreamCommit = RuntimeBaseline.UpstreamCommit,
            StructuredIpcVersion = StructuredIpcProtocol.CurrentVersion,
            RealtimeVersion = RealtimeProtocol.Version,
            CompatibilityProfile = bootstrap.CompatibilityProfile,
            SaveLayout = bootstrap.SaveLayout == 0 ? "root" : "sav",
            SessionRootManifestDigest = bootstrap.SessionRootManifestDigest,
            BrowserWidth = bootstrap.BrowserWidth,
            WidthMode = bootstrap.WidthMode,
            CustomWidth = bootstrap.CustomWidth,
            FontFaceId = bootstrap.FontFaceId,
            FontCatalogDigest = bootstrap.FontCatalogDigest,
            FontSize = bootstrap.FontSize,
            LineHeight = bootstrap.LineHeight,
            FontSizeLineHeightMode = bootstrap.FontSizeLineHeightMode,
            ConvertBackslashToYen = bootstrap.ConvertBackslashToYen,
            RuntimeInitializationTimeoutMilliseconds = bootstrap.RuntimeInitializationTimeoutMilliseconds,
            RuntimeExecutionTimeoutMilliseconds = bootstrap.RuntimeExecutionTimeoutMilliseconds,
            RandomAlgorithm = "SFMT",
            RandomSeed = bootstrap.RandomSeed,
            Timezone = TimeZoneInfo.Local.Id,
            Locale = System.Globalization.CultureInfo.CurrentCulture.Name,
            StartupWallClock = DateTimeOffset.Now,
            SaveSnapshotComplete = true,
        }, bootstrap.DebugTraceMaxBytes);
    }

    public void RuntimeConfigured(WindowMetadata metadata, RuntimeSaveLayout saveLayout, string compatibilityProfile) =>
        writer.Write(DebugTraceEventTypes.RuntimeConfig, new
        {
            effectiveWidth = metadata.ViewportWidth,
            fontFamily = metadata.DefaultFont.Family,
            fontSize = metadata.DefaultFont.Size,
            lineHeight = metadata.DefaultFont.LineHeight,
            saveLayout = saveLayout == RuntimeSaveLayout.Root ? "root" : "sav",
            compatibilityProfile,
        });

    public void PromptOpened(ConsolePrompt prompt)
    {
        lock (sync)
        {
            long next = ++ordinal;
            long now = Elapsed();
            prompts.Add(prompt.PromptId, (next, now));
            writer.Write(DebugTraceEventTypes.PromptOpen, new DebugPromptOpen
            {
                Ordinal = next,
                PromptId = prompt.PromptId,
                InputType = prompt.InputType.ToString(),
                AllowedSources = Sources(prompt.AllowedSources),
                DefaultValue = prompt.DefaultValue,
                OneInput = prompt.OneInput,
                TimeoutMilliseconds = prompt.Timeout is null || prompt.Timeout == Timeout.InfiniteTimeSpan
                    ? null : checked((long)prompt.Timeout.Value.TotalMilliseconds),
                ButtonGeneration = prompt.ButtonGeneration,
            });
        }
    }

    public void PromptResolved(ConsolePrompt prompt, ConsoleInputResult result, ConsoleInputAttempt? attempt)
    {
        lock (sync)
        {
            if (!prompts.Remove(prompt.PromptId, out (long Ordinal, long OpenedElapsed) opened)) return;
            string resolution = result.Kind switch
            {
                ConsoleInputResultKind.Accepted when result.Input?.IsDefaultValue == true => DebugPromptResolutionKinds.Defaulted,
                ConsoleInputResultKind.Accepted => DebugPromptResolutionKinds.Accepted,
                ConsoleInputResultKind.TimedOut when prompt.InputType == ConsoleInputType.WaitOnly => DebugPromptResolutionKinds.WaitCompleted,
                ConsoleInputResultKind.TimedOut when result.Input?.IsDefaultValue == true => DebugPromptResolutionKinds.Defaulted,
                ConsoleInputResultKind.TimedOut => DebugPromptResolutionKinds.Timeout,
                _ => DebugPromptResolutionKinds.Cancelled,
            };
            writer.Write(DebugTraceEventTypes.PromptResponse, new DebugPromptResponse
            {
                Ordinal = opened.Ordinal,
                Result = resolution,
                Reason = resolution.ToLowerInvariant(),
                Value = attempt?.Value ?? result.Value,
                NormalizedValue = result.Value,
                Source = attempt?.Source.ToString().ToUpperInvariant() ?? (resolution == DebugPromptResolutionKinds.Timeout ? "TIMEOUT" : "RUNTIME"),
                ResponseDelayMilliseconds = Math.Max(0, Elapsed() - opened.OpenedElapsed),
                PointerData = attempt?.Pointer is { } pointer
                    ? JsonSerializer.SerializeToElement(new
                    {
                        x = pointer.Position.X,
                        y = pointer.Position.Y,
                        pointer.Button,
                        pointer.Pressed,
                    }, DebugTraceJson.CompactOptions)
                    : null,
                KeyData = attempt?.Key is { } key
                    ? JsonSerializer.SerializeToElement(new
                    {
                        key.KeyCode,
                        key.Control,
                        key.Alt,
                        key.Shift,
                    }, DebugTraceJson.CompactOptions)
                    : null,
            }, flush: true);
        }
    }

    public void RuntimeFailure(string stableCode, string phase, string message, long sequence) =>
        writer.Write(DebugTraceEventTypes.RuntimeFailure, new DebugRuntimeFailure
        {
            StableCode = stableCode,
            Phase = phase,
            Message = message,
            LastOutputSequence = sequence,
            LastPromptOrdinal = ordinal,
        }, flush: true, terminal: true);

    public void Terminal(string status, long sequence) =>
        writer.Write(DebugTraceEventTypes.Terminal, new { status, lastOutputSequence = sequence, lastPromptOrdinal = ordinal }, flush: true, terminal: true);

    public void Dispose() => writer.Dispose();

    private long Elapsed() => Math.Max(0, Environment.TickCount64 - started);

    private static string[] Sources(ConsoleInputSource sources) => Enum.GetValues<ConsoleInputSource>()
        .Where(value => value is not ConsoleInputSource.None and not ConsoleInputSource.All && sources.HasFlag(value))
        .Select(value => value.ToString().ToUpperInvariant()).ToArray();
}

using System.Text.Json;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Workers;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Debugging.Contracts;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V9;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Debugger;

internal sealed record ReplayOptions(
    string TracePath,
    string SaveSnapshotPath,
    string SessionRoot,
    string Workspace,
    string? Output,
    string WorkerAssembly,
    string? RuntimeFontRoot,
    string Target,
    string Match,
    bool ResetWorkspace,
    bool AllowCaptureMismatch,
    bool AllowTruncated,
    TimeSpan Timeout);

internal static class ReplayEngine
{
    public static async Task<(int ExitCode, DebugReplayResult Result)> RunAsync(ReplayOptions options, CancellationToken cancellationToken)
    {
        DebugTraceDocument trace = DebugTraceReader.Read(options.TracePath, options.Target, options.AllowTruncated);
        PreparedDebugWorkspace workspace = DebugWorkspaceManager.Prepare(
            options.Workspace, options.SessionRoot, options.SaveSnapshotPath, trace.Header, options.Output,
            options.ResetWorkspace, options.AllowCaptureMismatch);
        string resultPath = Path.Combine(workspace.OutputPath, "result.json");
        string htmlPath = Path.Combine(workspace.OutputPath, "console.html");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        DebugReplayResult? result = null;
        CloudEmuera.RuntimeAdapter.ConsoleSnapshot? finalSnapshot = null;
        string controlRoot = Path.Combine(Path.GetTempPath(), "ced" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(controlRoot);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(controlRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
        await using WorkerManagerHost host = await WorkerManagerHost.StartAsync(new WorkerManagerOptions(
            controlRoot, options.WorkerAssembly, options.RuntimeFontRoot)
        {
            RegistrationTimeout = TimeSpan.FromSeconds(15),
            RuntimeReadyTimeout = TimeSpan.FromSeconds(60),
            WorkerShutdownTimeout = TimeSpan.FromSeconds(5),
        }, deadline.Token).ConfigureAwait(false);
        string workerId = "dbg_" + Guid.NewGuid().ToString("N");
        ulong epoch = trace.Header.OriginalWorkerEpoch == ulong.MaxValue ? 1 : trace.Header.OriginalWorkerEpoch + 1;
        var binding = new WorkerBinding(trace.Header.SessionId, workerId, epoch);
        var request = new WorkerLaunchRequest(
            binding,
            workspace.RootPath,
            trace.Header.CompatibilityProfile,
            trace.Header.SaveLayout == "root" ? RuntimeSaveLayout.Root : RuntimeSaveLayout.SavDirectory,
            trace.Header.SessionRootManifestDigest ?? string.Empty,
            browserWidth: trace.Header.BrowserWidth,
            fontSize: trace.Header.FontSize,
            lineHeight: trace.Header.LineHeight,
            fontFaceId: trace.Header.FontFaceId,
            fontCatalogDigest: trace.Header.FontCatalogDigest ?? string.Empty,
            widthMode: ParseWidthMode(trace.Header.WidthMode),
            customWidth: trace.Header.CustomWidth,
            convertBackslashToYen: trace.Header.ConvertBackslashToYen,
            fontSizeLineHeightMode: ParseFontMode(trace.Header.FontSizeLineHeightMode),
            randomSeed: trace.Header.RandomSeed,
            debugReplayMode: true,
            replayStartupWallClock: trace.Header.StartupWallClock);
        ApiWorkerSession worker = await host.LaunchWorkerAsync(request, deadline.Token).ConfigureAwait(false);
        try
        {
            await worker.SendStartRuntimeAsync(TimeSpan.FromSeconds(30), deadline.Token).ConfigureAwait(false);
            _ = await worker.WaitForAsync(value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready,
                TimeSpan.FromSeconds(60), deadline.Token).ConfigureAwait(false);
            Task<WorkerEnvelope> terminalTask = worker.WaitForAsync(
                value => value.PayloadCase is WorkerEnvelope.PayloadOneofCase.RuntimeCompleted or WorkerEnvelope.PayloadOneofCase.RuntimeFailed,
                options.Timeout, deadline.Token);
            IReadOnlyList<DebugPromptStep> steps = trace.Prompts
                .Where(step => trace.DefaultTarget.Event is null || step.ResponseEvent.Index < trace.DefaultTarget.Event.Index)
                .ToArray();
            long matched = 0;
            foreach (DebugPromptStep step in steps)
            {
                CloudEmuera.RuntimeAdapter.ConsolePrompt? actual = await WaitForPromptAsync(worker, terminalTask, deadline.Token).ConfigureAwait(false);
                if (actual is null)
                {
                    WorkerEnvelope earlyTerminal = await terminalTask.ConfigureAwait(false);
                    result = FailureBeforePrompt(trace, workspace.SourceModified, step, earlyTerminal, worker, matched);
                    break;
                }
                DebugFirstDivergence? mismatch = MatchPrompt(step, actual, options.Match, workspace.SourceModified, worker);
                if (mismatch is not null)
                {
                    result = Base(DebugReplayStatuses.PromptMismatch, trace, workspace.SourceModified, worker, matched) with { FirstDivergence = mismatch };
                    break;
                }
                string actualPromptId = actual.PromptId;
                DebugPromptResponse response = step.Response;
                if (response.ResponseDelayMilliseconds > 0)
                    await worker.AdvanceReplayClockAsync(response.ResponseDelayMilliseconds, TimeSpan.FromSeconds(15), deadline.Token).ConfigureAwait(false);
                if (response.Result is DebugPromptResolutionKinds.Accepted or DebugPromptResolutionKinds.Defaulted)
                {
                    SessionInputResult receipt = await worker.SubmitInputAsync(ToInput(trace.Header.SessionId, epoch, response),
                        TimeSpan.FromSeconds(15), deadline.Token).ConfigureAwait(false);
                    bool accepted = receipt.Status is SessionInputResultCodes.Accepted or SessionInputResultCodes.Duplicate;
                    bool normalizedMatches = response.NormalizedValue is null || string.Equals(receipt.NormalizedValue, response.NormalizedValue, StringComparison.Ordinal);
                    if (!accepted || !normalizedMatches)
                    {
                        result = Base(DebugReplayStatuses.InputResultMismatch, trace, workspace.SourceModified, worker, matched) with
                        {
                            FirstDivergence = Divergence(DebugReplayStatuses.InputResultMismatch, step.ResponseEvent.Index, step.Open.Ordinal,
                                new { response.Result, response.NormalizedValue }, new { receipt.Status, receipt.NormalizedValue }, workspace.SourceModified, worker)
                        };
                        break;
                    }
                    SessionPromptResolution? resolution = await WaitForPromptCloseAsync(worker, actualPromptId, terminalTask, deadline.Token).ConfigureAwait(false);
                    if (resolution?.Reason != ConsolePromptCloseReason.InputAccepted)
                    {
                        result = ResolutionMismatch(trace, workspace.SourceModified, worker, matched, step, resolution);
                        break;
                    }
                }
                else if (response.Result == DebugPromptResolutionKinds.Cancelled)
                {
                    await worker.CancelReplayPromptAsync(TimeSpan.FromSeconds(15), deadline.Token).ConfigureAwait(false);
                    SessionPromptResolution? resolution = await WaitForPromptCloseAsync(worker, actualPromptId, terminalTask, deadline.Token).ConfigureAwait(false);
                    if (resolution?.Reason != ConsolePromptCloseReason.Cancelled)
                    {
                        result = ResolutionMismatch(trace, workspace.SourceModified, worker, matched, step, resolution);
                        break;
                    }
                }
                else
                {
                    SessionPromptResolution? resolution = await WaitForPromptCloseAsync(worker, actualPromptId, terminalTask, deadline.Token).ConfigureAwait(false);
                    if (resolution?.Reason != ConsolePromptCloseReason.TimedOut)
                    {
                        result = ResolutionMismatch(trace, workspace.SourceModified, worker, matched, step, resolution);
                        break;
                    }
                }
                matched = step.Open.Ordinal;
            }

            if (result is null)
                result = await CompleteTargetAsync(trace, workspace.SourceModified, worker, terminalTask, matched, deadline.Token).ConfigureAwait(false);
            finalSnapshot = worker.OutputHub.CurrentSnapshot;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            result = Base(DebugReplayStatuses.ReplayTimeout, trace, workspace.SourceModified, worker, 0) with { Diagnostic = "Replay exceeded its bounded deadline." };
            finalSnapshot = worker.OutputHub.CurrentSnapshot;
        }
        finally
        {
            try { await worker.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
        }
        result ??= Base(DebugReplayStatuses.DebuggerFailed, trace, workspace.SourceModified, worker, 0);
        DebugReplayResult.Write(resultPath, result);
        TerminalHtmlWriter.Write(htmlPath, finalSnapshot, result);
        return (IsSuccess(result.Status) ? 0 : 1, result);
        }
        finally
        {
            try { if (Directory.Exists(controlRoot)) Directory.Delete(controlRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<CloudEmuera.RuntimeAdapter.ConsolePrompt?> WaitForPromptAsync(ApiWorkerSession worker, Task<WorkerEnvelope> terminal, CancellationToken cancellationToken)
    {
        while (true)
        {
            CloudEmuera.RuntimeAdapter.ConsolePrompt? prompt = worker.OutputHub.CurrentSnapshot?.CurrentPrompt;
            if (prompt is not null) return prompt;
            if (terminal.IsCompleted) return null;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<SessionPromptResolution?> WaitForPromptCloseAsync(ApiWorkerSession worker, string promptId, Task<WorkerEnvelope> terminal, CancellationToken cancellationToken)
    {
        while (true)
        {
            SessionPromptResolution? resolution = worker.OutputHub.LastPromptResolution;
            if (string.Equals(resolution?.PromptId, promptId, StringComparison.Ordinal)) return resolution;
            string? current = worker.OutputHub.CurrentSnapshot?.CurrentPrompt?.PromptId;
            if (!string.Equals(current, promptId, StringComparison.Ordinal)) return resolution;
            if (terminal.IsCompleted) return resolution;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DebugReplayResult ResolutionMismatch(DebugTraceDocument trace, bool modified, ApiWorkerSession worker,
        long matched, DebugPromptStep step, SessionPromptResolution? resolution) =>
        Base(DebugReplayStatuses.InputResultMismatch, trace, modified, worker, matched) with
        {
            FirstDivergence = Divergence(DebugReplayStatuses.InputResultMismatch, step.ResponseEvent.Index, step.Open.Ordinal,
                new { step.Response.Result }, resolution, modified, worker),
        };

    private static DebugFirstDivergence? MatchPrompt(DebugPromptStep step, CloudEmuera.RuntimeAdapter.ConsolePrompt actual, string mode, bool sourceModified, ApiWorkerSession worker)
    {
        string[] actualSources = Enum.GetValues<ConsoleInputSource>()
            .Where(value => value is not ConsoleInputSource.None and not ConsoleInputSource.All && actual.AllowedSources.HasFlag(value))
            .Select(value => value.ToString().ToUpperInvariant()).Order().ToArray();
        string[] expectedSources = step.Open.AllowedSources.Select(value => value.ToUpperInvariant()).Order().ToArray();
        long? actualTimeout = actual.Timeout is null || actual.Timeout == Timeout.InfiniteTimeSpan ? null : checked((long)actual.Timeout.Value.TotalMilliseconds);
        bool matches = string.Equals(step.Open.InputType, actual.InputType.ToString(), StringComparison.OrdinalIgnoreCase) &&
            expectedSources.SequenceEqual(actualSources, StringComparer.Ordinal) &&
            string.Equals(step.Open.DefaultValue, actual.DefaultValue, StringComparison.Ordinal) &&
            step.Open.OneInput == actual.OneInput && step.Open.TimeoutMilliseconds == actualTimeout;
        if (matches) return null;
        return Divergence(DebugReplayStatuses.PromptMismatch, step.OpenEvent.Index, step.Open.Ordinal,
            new { step.Open.InputType, allowedSources = expectedSources, step.Open.DefaultValue, step.Open.OneInput, step.Open.TimeoutMilliseconds, step.Open.SourceFile, match = mode },
            new { inputType = actual.InputType.ToString(), allowedSources = actualSources, actual.DefaultValue, actual.OneInput, timeoutMilliseconds = actualTimeout },
            sourceModified, worker);
    }

    internal static SessionInputCommand ToInput(string sessionId, ulong epoch, DebugPromptResponse response)
    {
        SessionInputSource source = response.Source.ToUpperInvariant() switch
        {
            "BUTTON" => SessionInputSource.Button,
            "POINTER" => SessionInputSource.PointerDevice,
            "SYSTEM" => SessionInputSource.System,
            _ => SessionInputSource.Keyboard,
        };
        SessionPointerInput? pointer = null;
        if (response.PointerData is { } pointerJson && pointerJson.ValueKind == JsonValueKind.Object)
            pointer = new(pointerJson.TryGetProperty("x", out JsonElement x) ? x.GetInt32() : 0,
                pointerJson.TryGetProperty("y", out JsonElement y) ? y.GetInt32() : 0,
                pointerJson.TryGetProperty("button", out JsonElement button) ? button.GetInt32() : 0,
                !pointerJson.TryGetProperty("pressed", out JsonElement pressed) || pressed.GetBoolean());
        SessionKeyInput? key = null;
        if (response.KeyData is { } keyJson && keyJson.ValueKind == JsonValueKind.Object)
            key = new(keyJson.TryGetProperty("keyCode", out JsonElement code) ? code.GetInt32() : 0,
                keyJson.TryGetProperty("control", out JsonElement control) && control.GetBoolean(),
                keyJson.TryGetProperty("alt", out JsonElement alt) && alt.GetBoolean(),
                keyJson.TryGetProperty("shift", out JsonElement shift) && shift.GetBoolean());
        return new SessionInputCommand(sessionId, epoch, "replay_" + Guid.NewGuid().ToString("N"), response.Value ?? string.Empty, source, pointer, key);
    }

    private static async Task<DebugReplayResult> CompleteTargetAsync(DebugTraceDocument trace, bool modified, ApiWorkerSession worker,
        Task<WorkerEnvelope> terminalTask, long matched, CancellationToken cancellationToken)
    {
        if (trace.DefaultTarget.Type == "marker")
            return Base(DebugReplayStatuses.FailureMarkerReached, trace, modified, worker, matched);
        WorkerEnvelope terminal = await terminalTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (trace.DefaultTarget.Type == "terminal")
            return terminal.PayloadCase == WorkerEnvelope.PayloadOneofCase.RuntimeCompleted
                ? Base(DebugReplayStatuses.TerminalReached, trace, modified, worker, matched)
                : Base(DebugReplayStatuses.FailureMismatch, trace, modified, worker, matched) with
                {
                    Failure = ActualFailure(terminal),
                    FirstDivergence = Divergence(DebugReplayStatuses.FailureMismatch, trace.DefaultTarget.Event?.Index, null,
                        new { type = "terminal" }, ActualFailure(terminal), modified, worker)
                };
        DebugRuntimeFailure expected = DebugTraceJson.ReadData<DebugRuntimeFailure>(trace.DefaultTarget.Event!);
        if (terminal.PayloadCase == WorkerEnvelope.PayloadOneofCase.RuntimeCompleted)
            return Base(DebugReplayStatuses.ExpectedFailureNotReproduced, trace, modified, worker, matched);
        DebugRuntimeFailure actual = ActualFailure(terminal)!;
        bool same = string.Equals(expected.StableCode, actual.StableCode, StringComparison.Ordinal) &&
            string.Equals(expected.Phase, actual.Phase, StringComparison.Ordinal) &&
            (expected.ExceptionType is null || string.Equals(expected.ExceptionType, actual.ExceptionType, StringComparison.Ordinal)) &&
            (expected.SourceFile is null || string.Equals(expected.SourceFile, actual.SourceFile, StringComparison.Ordinal));
        return same
            ? Base(DebugReplayStatuses.RuntimeFailureReproduced, trace, modified, worker, matched) with { Failure = actual }
            : Base(DebugReplayStatuses.FailureMismatch, trace, modified, worker, matched) with
            {
                Failure = actual,
                FirstDivergence = Divergence(DebugReplayStatuses.FailureMismatch, trace.DefaultTarget.Event?.Index, null, expected, actual, modified, worker)
            };
    }

    private static DebugReplayResult FailureBeforePrompt(DebugTraceDocument trace, bool modified, DebugPromptStep step,
        WorkerEnvelope terminal, ApiWorkerSession worker, long matched) =>
        Base(terminal.PayloadCase == WorkerEnvelope.PayloadOneofCase.RuntimeCompleted
                ? DebugReplayStatuses.TraceExhausted : DebugReplayStatuses.FailureMismatch,
            trace, modified, worker, matched) with
        {
            Failure = ActualFailure(terminal),
            FirstDivergence = Divergence(DebugReplayStatuses.PromptMismatch, step.OpenEvent.Index, step.Open.Ordinal,
                step.Open, terminal.PayloadCase.ToString(), modified, worker)
        };

    private static DebugRuntimeFailure? ActualFailure(WorkerEnvelope terminal) =>
        terminal.PayloadCase != WorkerEnvelope.PayloadOneofCase.RuntimeFailed ? null : new DebugRuntimeFailure
        {
            StableCode = terminal.RuntimeFailed.StableCode,
            Phase = terminal.RuntimeFailed.Phase,
            Message = terminal.RuntimeFailed.SafeMessage,
            LastOutputSequence = terminal.RuntimeFailed.LastOutputSequence,
        };

    private static DebugReplayResult Base(string status, DebugTraceDocument trace, bool modified, ApiWorkerSession worker, long matched) => new()
    {
        Status = status,
        Target = trace.DefaultTarget,
        SourceModified = modified,
        LastMatchedPromptOrdinal = matched,
        LastFrameId = worker.OutputHub.CurrentCommittedFrameId,
        LastSequence = worker.OutputHub.SnapshotSequence,
    };

    private static DebugFirstDivergence Divergence(string kind, long? index, long? ordinal, object? expected, object? actual,
        bool modified, ApiWorkerSession worker) => new(kind, index, ordinal, expected, actual, modified,
            worker.OutputHub.CurrentCommittedFrameId, worker.OutputHub.SnapshotSequence);

    private static SessionWidthMode ParseWidthMode(string value) => value.ToUpperInvariant() switch
    {
        "ORIGINAL" or "ORIGIN" => SessionWidthMode.Original,
        "MAX" => SessionWidthMode.Max,
        "CUSTOM" => SessionWidthMode.Custom,
        _ => SessionWidthMode.Adaptive,
    };

    private static SessionFontSizeLineHeightMode ParseFontMode(string value) =>
        value.Equals("CONFIG", StringComparison.OrdinalIgnoreCase) ? SessionFontSizeLineHeightMode.Config : SessionFontSizeLineHeightMode.Override;

    private static bool IsSuccess(string status) => status is DebugReplayStatuses.RuntimeFailureReproduced or
        DebugReplayStatuses.FailureMarkerReached or DebugReplayStatuses.TerminalReached;
}

using CloudEmuera.Debugger;
using CloudEmuera.Debugging.Contracts;

return await DebuggerProgram.RunAsync(args).ConfigureAwait(false);

internal static class DebuggerProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length >= 3 && args[0] == "workspace" && args[1] == "delete")
        {
            string? deleteWorkspace = Value(args, "--workspace");
            if (deleteWorkspace is null) return Usage("workspace delete requires --workspace.");
            try { DebugWorkspaceManager.Delete(deleteWorkspace); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
        }
        if (args.Length == 0 || args[0] != "replay") return Usage();

        string? trace = Value(args, "--trace");
        string? snapshot = Value(args, "--save-snapshot");
        string? sessionRoot = Value(args, "--session-root");
        if (trace is null || snapshot is null || sessionRoot is null)
            return Usage("replay requires --trace, --save-snapshot, and --session-root.");
        string workspace = Value(args, "--workspace") ??
            Path.Combine(Directory.GetParent(Path.GetFullPath(sessionRoot))?.FullName ?? Path.GetDirectoryName(Path.GetFullPath(sessionRoot))!, "metadata", "debug-workspace");
        string worker = Value(args, "--worker") ?? DefaultWorkerAssembly();
        int timeoutSeconds = int.TryParse(Value(args, "--timeout-seconds"), out int parsed) && parsed is >= 1 and <= 86_400 ? parsed : 300;
        var options = new ReplayOptions(
            Path.GetFullPath(trace), Path.GetFullPath(snapshot), Path.GetFullPath(sessionRoot), Path.GetFullPath(workspace),
            Value(args, "--output") is { } output ? Path.GetFullPath(output) : null,
            Path.GetFullPath(worker), Value(args, "--runtime-font-root"), Value(args, "--target") ?? "auto",
            Value(args, "--match") ?? "strict", Has(args, "--reset-workspace"), Has(args, "--allow-capture-mismatch"),
            Has(args, "--allow-truncated"), TimeSpan.FromSeconds(timeoutSeconds));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            (int exitCode, DebugReplayResult result) = await ReplayEngine.RunAsync(options, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine(result.Status);
            return exitCode;
        }
        catch (Exception exception) when (exception is DebugTraceException or InvalidDataException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            string status = exception is DebugTraceException traceException ? traceException.Code : DebugReplayStatuses.DebuggerFailed;
            string errorOutput = options.Output ?? Path.Combine(options.Workspace, "output");
            try
            {
                DebugReplayResult.Write(Path.Combine(errorOutput, "result.json"), new DebugReplayResult { Status = status, Diagnostic = exception.Message });
            }
            catch (Exception) { }
            Console.Error.WriteLine($"{status}: {exception.Message}");
            return 1;
        }
    }

    private static string? Value(string[] args, string name)
    {
        for (int index = 0; index < args.Length; index++)
            if (args[index] == name && index + 1 < args.Length) return args[index + 1];
        return null;
    }

    private static bool Has(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);

    private static string DefaultWorkerAssembly()
    {
        string colocated = Path.Combine(AppContext.BaseDirectory, "CloudEmuera.Worker.dll");
        if (File.Exists(colocated)) return colocated;
        string? appRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        return Path.Combine(appRoot ?? AppContext.BaseDirectory, "worker", "CloudEmuera.Worker.dll");
    }

    private static int Usage(string? error = null)
    {
        if (error is not null) Console.Error.WriteLine(error);
        Console.Error.WriteLine("Usage: cloudemuera-debugger replay --trace PATH --save-snapshot PATH --session-root PATH [--workspace PATH] [--output PATH] [--worker PATH] [--target auto|runtime-failure|terminal|marker:N] [--match strict|adaptive] [--reset-workspace]");
        Console.Error.WriteLine("       cloudemuera-debugger workspace delete --workspace PATH");
        return 2;
    }
}

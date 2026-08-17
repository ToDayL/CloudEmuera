using System.Diagnostics;

namespace CloudEmuera.Api.Workers;

/// <summary>
/// Keeps API-side development/runtime orchestration out of Worker processes.
/// The API may itself be launched by <c>dotnet watch</c>; a Worker is a
/// separately managed child and must not attach to the watch Hot Reload agent.
/// </summary>
internal static class WorkerProcessEnvironment
{
    private static readonly string[] hostOrchestratorVariables =
    [
        "ASPNETCORE_AUTO_RELOAD_VDIR",
        "ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT",
        "ASPNETCORE_AUTO_RELOAD_WS_KEY",
        "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES",
        "DOTNET_MODIFIABLE_ASSEMBLIES",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_USE_POLLING_FILE_WATCHER",
        "DOTNET_WATCH",
        "DOTNET_WATCH_SUPPRESS_MSBUILD_INCREMENTALISM",
        "DOTNET_HOTRELOAD_NAMEDPIPE_NAME",
        "DOTNET_WATCH_HOTRELOAD_NAMEDPIPE_NAME",
        "DOTNET_WATCH_ITERATION",
        "DOTNET_DiagnosticPorts",
        "COMPlus_EnableDiagnostics",
    ];

    internal static IReadOnlyList<string> HostOrchestratorVariableNames => hostOrchestratorVariables;

    internal static void RemoveHostOrchestratorVariables(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (string variable in hostOrchestratorVariables)
            startInfo.Environment.Remove(variable);
    }
}

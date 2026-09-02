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

    private static readonly string[] controlPlaneSecretVariables =
    [
        "CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME",
        "CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL",
        "CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD",
        "CLOUDEMUERA_DATA_PATH",
        "CloudEmuera__DataPath",
        "CloudEmuera__DatabasePath",
        "CloudEmuera__WorkerAssemblyPath",
        "CloudEmuera__ValidatorAssembly",
        "CloudEmuera__ConnectionStrings__Default",
        "ConnectionStrings__Default",
    ];

    internal static IReadOnlyList<string> HostOrchestratorVariableNames => hostOrchestratorVariables;

    internal static IReadOnlyList<string> ControlPlaneSecretVariableNames => controlPlaneSecretVariables;

    internal static void RemoveHostOrchestratorVariables(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (string variable in hostOrchestratorVariables)
            startInfo.Environment.Remove(variable);
        foreach (string variable in controlPlaneSecretVariables)
            startInfo.Environment.Remove(variable);

        // Configuration environment variables are for the API composition
        // root. The Worker receives its private projection through the
        // bootstrap file instead of inheriting DataRoot/database/capacity or
        // bootstrap-admin settings. Keep the explicit runtime debug switch,
        // which is a non-secret test/diagnostic input consumed by the runtime.
        foreach (string variable in startInfo.Environment.Keys.ToArray())
        {
            if (variable.StartsWith("CloudEmuera__", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(variable, "CloudEmuera__RuntimeDebugTrace", StringComparison.OrdinalIgnoreCase))
                startInfo.Environment.Remove(variable);
            else if (variable.StartsWith("CLOUDEMUERA_BOOTSTRAP_ADMIN_", StringComparison.OrdinalIgnoreCase))
                startInfo.Environment.Remove(variable);
        }
    }
}

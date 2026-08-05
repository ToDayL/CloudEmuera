using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Supervisor;

public sealed record SupervisorOptions
{
    public SupervisorOptions(string runtimeDirectory, string workerAssemblyPath)
    {
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory ?? throw new ArgumentNullException(nameof(runtimeDirectory)));
        WorkerAssemblyPath = Path.GetFullPath(workerAssemblyPath ?? throw new ArgumentNullException(nameof(workerAssemblyPath)));
        SocketPath = Path.Combine(RuntimeDirectory, "supervisor.sock");
        BootstrapDirectory = Path.Combine(RuntimeDirectory, "bootstrap");
    }

    public string RuntimeDirectory { get; }

    public string SocketPath { get; init; }

    public string BootstrapDirectory { get; init; }

    public string WorkerAssemblyPath { get; }

    public string DotnetPath { get; init; } = "dotnet";

    public TimeSpan RegistrationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan WorkerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxConcurrentWorkers { get; init; } = 32;

    // Test-only seam for exercising registration failures before Runtime startup.
    // It is internal and unavailable to production callers.
    internal Func<WorkerBootstrapDocument, WorkerBootstrapDocument>? BootstrapTransformForTest { get; init; }

    public void Validate()
    {
        IpcValidator.ValidateAbsolutePath(RuntimeDirectory, nameof(RuntimeDirectory));
        IpcValidator.ValidateAbsolutePath(SocketPath, nameof(SocketPath));
        IpcValidator.ValidateAbsolutePath(BootstrapDirectory, nameof(BootstrapDirectory));
        IpcValidator.ValidateAbsolutePath(WorkerAssemblyPath, nameof(WorkerAssemblyPath));
        if (SocketPath.Length > 100 || RegistrationTimeout <= TimeSpan.Zero ||
            WorkerShutdownTimeout <= TimeSpan.Zero || MaxConcurrentWorkers <= 0)
        {
            throw new ArgumentException("Supervisor options are outside their supported bounds.");
        }

        if (!SamePath(SocketPath, Path.Combine(RuntimeDirectory, "supervisor.sock")) ||
            !SamePath(BootstrapDirectory, Path.Combine(RuntimeDirectory, "bootstrap")))
        {
            throw new ArgumentException("Supervisor IPC paths must remain inside its private runtime directory.");
        }
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record WorkerLaunchRequest
{
    public WorkerLaunchRequest(
        WorkerBinding binding,
        string sessionRoot,
        string compatibilityProfile,
        RuntimeSaveLayout saveLayout,
        string sessionRootManifestDigest = "")
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        SessionRoot = Path.GetFullPath(sessionRoot ?? throw new ArgumentNullException(nameof(sessionRoot)));
        CompatibilityProfile = compatibilityProfile ?? throw new ArgumentNullException(nameof(compatibilityProfile));
        if (CompatibilityProfile is not ("v18-compatible" or "em-ee-current"))
            throw new ArgumentException("The compatibility profile is not supported.", nameof(compatibilityProfile));
        if (!Enum.IsDefined(saveLayout))
            throw new ArgumentOutOfRangeException(nameof(saveLayout));
        SaveLayout = saveLayout;
        SessionRootManifestDigest = sessionRootManifestDigest ?? string.Empty;
    }

    public WorkerBinding Binding { get; }

    public string SessionRoot { get; }

    public string CompatibilityProfile { get; }

    public RuntimeSaveLayout SaveLayout { get; }

    public string SessionRootManifestDigest { get; }
}

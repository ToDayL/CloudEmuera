using CloudEmuera.Ipc;
using CloudEmuera.Api.Realtime;

namespace CloudEmuera.Api.Workers;

public sealed record WorkerManagerOptions
{
    public WorkerManagerOptions(string dataRoot, string workerAssemblyPath)
    {
        DataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        WorkerAssemblyPath = Path.GetFullPath(workerAssemblyPath ?? throw new ArgumentNullException(nameof(workerAssemblyPath)));
        ControlPlaneInstanceId = $"ctl_{Guid.CreateVersion7():N}";
        RuntimeDirectory = Path.Combine(DataRoot, "runtime", ControlPlaneInstanceId);
        ControlSocketPath = Path.Combine(RuntimeDirectory, "worker-control.sock");
        BootstrapDirectory = Path.Combine(RuntimeDirectory, "bootstrap");
    }

    public string DataRoot { get; }

    public string RuntimeDirectory { get; init; }

    public string BootstrapDirectory { get; init; }

    public string ControlSocketPath { get; init; }

    public string ControlPlaneInstanceId { get; }

    public string WorkerAssemblyPath { get; }

    public string DotnetPath { get; init; } = "dotnet";

    public TimeSpan RegistrationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RuntimeReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan WorkerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(5);

    public Func<WorkerBootstrapDocument, WorkerBootstrapDocument>? BootstrapTransformForTest { get; init; }

    public RealtimeOutputOptions RealtimeOutput { get; init; } = RealtimeOutputOptions.Default;

    // The API hub reducer options must stay identical to the Worker console
    // options, otherwise a legally produced Worker batch could be rejected by
    // the API mirror. Both sides currently use RuntimeAdapter's
    // ConsoleHistoryOptions.Default and there is deliberately no one-sided
    // configuration entry yet; introduce one only when the Worker bootstrap
    // can carry the same value.

    // This is only a bounded lifecycle/event probe for control-plane waits;
    // DisplayBatch payloads are deliberately never retained here. Realtime
    // output is owned by SessionOutputHub.
    public int PendingEventMaxMessages { get; init; } = 256;

    public int PendingEventMaxBytes { get; init; } = 1 * 1024 * 1024;

    public void Validate()
    {
        IpcValidator.ValidateAbsolutePath(DataRoot, nameof(DataRoot));
        IpcValidator.ValidateAbsolutePath(RuntimeDirectory, nameof(RuntimeDirectory));
        IpcValidator.ValidateAbsolutePath(BootstrapDirectory, nameof(BootstrapDirectory));
        IpcValidator.ValidateAbsolutePath(ControlSocketPath, nameof(ControlSocketPath));
        IpcValidator.ValidateAbsolutePath(WorkerAssemblyPath, nameof(WorkerAssemblyPath));
        IpcValidator.ValidateIdentifier(ControlPlaneInstanceId, nameof(ControlPlaneInstanceId));
        RealtimeOutput.Validate();
        if (ControlSocketPath.Length > 107 || RegistrationTimeout <= TimeSpan.Zero || RuntimeReadyTimeout <= TimeSpan.Zero ||
            WorkerShutdownTimeout <= TimeSpan.Zero || HeartbeatInterval <= TimeSpan.Zero || LeaseDuration <= HeartbeatInterval)
            throw new ArgumentException("Worker Manager options are outside their supported bounds.");

        if (PendingEventMaxMessages <= 0 || PendingEventMaxMessages > 4096 ||
            PendingEventMaxBytes <= 0 || PendingEventMaxBytes > CloudEmuera.Ipc.StructuredIpcLimits.MaxEnvelopeBytes)
            throw new ArgumentException("Pending Worker event history is outside its supported bounds.");

        if (!SamePath(RuntimeDirectory, Path.Combine(DataRoot, "runtime", ControlPlaneInstanceId)) ||
            !SamePath(BootstrapDirectory, Path.Combine(RuntimeDirectory, "bootstrap")) ||
            !SamePath(ControlSocketPath, Path.Combine(RuntimeDirectory, "worker-control.sock")))
            throw new ArgumentException("Worker control paths must remain inside the private API runtime directory.");
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
        CloudEmuera.RuntimeAdapter.RuntimeSaveLayout saveLayout,
        string sessionRootManifestDigest = "",
        long initialOutputSequence = 0)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        SessionRoot = Path.GetFullPath(sessionRoot ?? throw new ArgumentNullException(nameof(sessionRoot)));
        CompatibilityProfile = compatibilityProfile ?? throw new ArgumentNullException(nameof(compatibilityProfile));
        if (CompatibilityProfile is not ("v18-compatible" or "em-ee-current"))
            throw new ArgumentException("The compatibility profile is not supported.", nameof(compatibilityProfile));
        if (!Enum.IsDefined(saveLayout))
            throw new ArgumentOutOfRangeException(nameof(saveLayout));
        ArgumentOutOfRangeException.ThrowIfNegative(initialOutputSequence);
        SaveLayout = saveLayout;
        SessionRootManifestDigest = sessionRootManifestDigest ?? string.Empty;
        InitialOutputSequence = initialOutputSequence;
    }

    public WorkerBinding Binding { get; }

    public string SessionRoot { get; }

    public string CompatibilityProfile { get; }

    public CloudEmuera.RuntimeAdapter.RuntimeSaveLayout SaveLayout { get; }

    public string SessionRootManifestDigest { get; }

    public long InitialOutputSequence { get; }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using CloudEmuera.Ipc.V2;

namespace CloudEmuera.Ipc;

/// <summary>
/// Wire-level constants for the API-owned Worker protocol. Keeping the
/// values here makes protocol decisions auditable and prevents magic limits
/// from being spread across the two processes.
/// </summary>
public static class IpcProtocol
{
    public const uint CurrentVersion = 2;
    public const int BootstrapSchemaVersion = 2;

    public static string NewMessageId(string prefix = "msg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return $"{prefix}_{Guid.NewGuid():N}";
    }

    public static string CreateBootstrapToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public static class IpcLimits
{
    public const int MaxEnvelopeBytes = 256 * 1024;
    public const int MaxIdentifierLength = 128;
    public const int MaxTokenLength = 256;
    public const int MaxStringLength = 32 * 1024;
    public const int MaxDisplayOperations = 512;
    public const int MaxDisplayNodes = 512;
    public const int MaxBootstrapBytes = 16 * 1024;
    public const int MaxProtocolErrorMessageLength = 512;
    public const long MaxHeartbeatResidentBytes = 1L << 50;
}

public static class IpcReasonCodes
{
    public const string Accepted = "accepted";
    public const string AlreadyStarted = "already_started";
    public const string BindingMismatch = "binding_mismatch";
    public const string BootstrapInvalid = "bootstrap_invalid";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string InvalidCommand = "invalid_command";
    public const string InvalidEnvelope = "invalid_envelope";
    public const string InvalidToken = "invalid_token";
    public const string OutputResumeGap = "output_resume_gap";
    public const string RuntimeVersionMismatch = "runtime_version_mismatch";
    public const string StalePrompt = "stale_prompt";
    public const string UnsupportedMessage = "unsupported_message";
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";
    public const string WorkerStopping = "worker_stopping";
    public const string ControlPlaneMismatch = "control_plane_mismatch";
}

public sealed record WorkerBinding
{
    public WorkerBinding(string sessionId, string workerId, ulong workerEpoch)
    {
        IpcValidator.ValidateIdentifier(sessionId, nameof(sessionId));
        IpcValidator.ValidateIdentifier(workerId, nameof(workerId));
        if (workerEpoch == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerEpoch), "Worker epoch must be positive.");
        }

        SessionId = sessionId;
        WorkerId = workerId;
        WorkerEpoch = workerEpoch;
    }

    public string SessionId { get; }

    public string WorkerId { get; }

    public ulong WorkerEpoch { get; }

    public bool Matches(string sessionId, string workerId, ulong workerEpoch) =>
        string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
        string.Equals(WorkerId, workerId, StringComparison.Ordinal) &&
        WorkerEpoch == workerEpoch;

    public override string ToString() => $"{SessionId}/{WorkerId}/{WorkerEpoch}";
}

public readonly record struct IpcValidationResult(bool IsValid, string ReasonCode)
{
    public static IpcValidationResult Valid() => new(true, IpcReasonCodes.Accepted);

    public static IpcValidationResult Invalid(string reasonCode) => new(false, reasonCode);
}

/// <summary>Small DTO used by the API control plane to pass one-time Worker state.</summary>
public sealed record WorkerBootstrapDocument
{
    public int SchemaVersion { get; init; } = IpcProtocol.BootstrapSchemaVersion;

    public uint ProtocolVersion { get; init; } = IpcProtocol.CurrentVersion;

    public string SessionId { get; init; } = string.Empty;

    public string WorkerId { get; init; } = string.Empty;

    public ulong WorkerEpoch { get; init; }

    public string SessionRoot { get; init; } = string.Empty;

    public string CompatibilityProfile { get; init; } = string.Empty;

    public string ControlSocketPath { get; init; } = string.Empty;

    public string ControlPlaneInstanceId { get; init; } = string.Empty;

    public long ExpectedParentProcessId { get; init; }

    public string BootstrapToken { get; init; } = string.Empty;

    public long ConnectDeadlineUnixMilliseconds { get; init; }

    public int HeartbeatIntervalMilliseconds { get; init; }

    public int ShutdownGracePeriodMilliseconds { get; init; }

    public int DisconnectGracePeriodMilliseconds { get; init; } = 2_000;

    public long InitialOutputSequence { get; init; }

    public int SaveLayout { get; init; }

    public string SessionRootManifestDigest { get; init; } = string.Empty;

    public int RuntimeInitializationTimeoutMilliseconds { get; init; } = 30_000;

    public int RuntimeExecutionTimeoutMilliseconds { get; init; } = -1;

    public WorkerBinding Binding => new(SessionId, WorkerId, WorkerEpoch);

    public void Validate()
    {
        if (SchemaVersion != IpcProtocol.BootstrapSchemaVersion)
        {
            throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
        }

        if (ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            throw new InvalidDataException(IpcReasonCodes.UnsupportedProtocolVersion);
        }

        IpcValidator.ValidateIdentifier(SessionId, nameof(SessionId));
        IpcValidator.ValidateIdentifier(WorkerId, nameof(WorkerId));
        IpcValidator.ValidateIdentifier(CompatibilityProfile, nameof(CompatibilityProfile));
        IpcValidator.ValidateToken(BootstrapToken, nameof(BootstrapToken));
        IpcValidator.ValidateAbsolutePath(SessionRoot, nameof(SessionRoot));
        IpcValidator.ValidateAbsolutePath(ControlSocketPath, nameof(ControlSocketPath));
        IpcValidator.ValidateIdentifier(ControlPlaneInstanceId, nameof(ControlPlaneInstanceId));
        if (WorkerEpoch == 0 || SaveLayout is < 0 or > 1 || ExpectedParentProcessId <= 0 || InitialOutputSequence < 0)
        {
            throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
        }

        ValidatePositive(HeartbeatIntervalMilliseconds, nameof(HeartbeatIntervalMilliseconds), 10, 60_000);
        ValidatePositive(ShutdownGracePeriodMilliseconds, nameof(ShutdownGracePeriodMilliseconds), 100, 120_000);
        ValidatePositive(DisconnectGracePeriodMilliseconds, nameof(DisconnectGracePeriodMilliseconds), 100, 120_000);
        ValidatePositive(RuntimeInitializationTimeoutMilliseconds, nameof(RuntimeInitializationTimeoutMilliseconds), 100, 300_000);
        if (RuntimeExecutionTimeoutMilliseconds == 0 || RuntimeExecutionTimeoutMilliseconds < -1 || RuntimeExecutionTimeoutMilliseconds > 86_400_000)
        {
            throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
        }

        if (ConnectDeadlineUnixMilliseconds <= 0 || SessionRootManifestDigest.Length > IpcLimits.MaxStringLength)
        {
            throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
        }
    }

    private static void ValidatePositive(int value, string name, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"{IpcReasonCodes.BootstrapInvalid}:{name}");
        }
    }
}

public static class WorkerBootstrapFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(string path, WorkerBootstrapDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        string fullPath = ValidateAbsolutePath(path, nameof(path));
        string parent = Directory.GetParent(fullPath)?.FullName
            ?? throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
        EnsureDirectory(parent);
        if (File.Exists(fullPath) || Directory.Exists(fullPath) || IsLink(fullPath))
        {
            throw new IOException("The bootstrap file target already exists.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > IpcLimits.MaxBootstrapBytes)
        {
            throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
        }

        using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        SetMode(fullPath, userRead: true, userWrite: true);
        EnsureRegularPrivateFile(fullPath);
    }

    public static WorkerBootstrapDocument Read(string path)
    {
        string fullPath = ValidateAbsolutePath(path, nameof(path));
        EnsureRegularPrivateFile(fullPath);
        byte[] bytes;
        using (FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            if (stream.Length <= 0 || stream.Length > IpcLimits.MaxBootstrapBytes)
            {
                throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        WorkerBootstrapDocument? document = JsonSerializer.Deserialize<WorkerBootstrapDocument>(bytes, JsonOptions);
        if (document is null)
        {
            throw new InvalidDataException(IpcReasonCodes.BootstrapInvalid);
        }

        document.Validate();
        return document;
    }

    public static void DeleteIfOwned(string path)
    {
        string fullPath;
        try
        {
            fullPath = ValidateAbsolutePath(path, nameof(path));
            EnsureRegularPrivateFile(fullPath);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        File.Delete(fullPath);
    }

    private static string ValidateAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"{IpcReasonCodes.BootstrapInvalid}:{parameterName}");
        }

        return Path.GetFullPath(path);
    }

    private static void EnsureDirectory(string path)
    {
        if (IsLink(path))
        {
            throw new IOException("The bootstrap directory cannot be a symbolic link.");
        }

        Directory.CreateDirectory(path);
        SetMode(path, userRead: true, userWrite: true, userExecute: true);
        FileSystemInfo info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null)
        {
            throw new IOException("The bootstrap directory is not a normal directory.");
        }
    }

    private static void EnsureRegularPrivateFile(string path)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("The bootstrap file is not a regular file.", path);
        }

        EnsureLinuxRegularOwnedFile(path);

        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                throw new UnauthorizedAccessException("The bootstrap file permissions are too broad.");
            }
        }
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo file = new FileInfo(path);
        return file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static void SetMode(string path, bool userRead, bool userWrite, bool userExecute = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        UnixFileMode mode = UnixFileMode.None;
        if (userRead) mode |= UnixFileMode.UserRead;
        if (userWrite) mode |= UnixFileMode.UserWrite;
        if (userExecute) mode |= UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, mode);
    }

    private static void EnsureLinuxRegularOwnedFile(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException("Bootstrap validation requires 64-bit Linux metadata.");
        }

        try
        {
            if (LStat(path, out UnixStat stat) != 0)
            {
                throw new FileNotFoundException("The bootstrap file could not be inspected.", path);
            }

            if ((stat.Mode & UnixFileTypeMask) != UnixRegularFile ||
                stat.LinkCount != 1 ||
                stat.UserId != GetEffectiveUserId())
            {
                throw new UnauthorizedAccessException("The bootstrap file is not a private, owned regular file.");
            }
        }
        catch (DllNotFoundException exception)
        {
            throw new PlatformNotSupportedException("Bootstrap validation requires libc file metadata.", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException("Bootstrap validation requires libc file metadata.", exception);
        }
    }

    [SuppressMessage("Security", "CA2101", Justification = "lstat receives an explicit UTF-8 marshaled path.")]
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixStat stat);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public uint Padding;
        public ulong SpecialDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public UnixTimespec AccessTime;
        public UnixTimespec ModifyTime;
        public UnixTimespec ChangeTime;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
}

public static class IpcValidator
{
    public static IpcValidationResult ValidateWorkerEnvelope(
        WorkerEnvelope envelope,
        bool registered,
        WorkerBinding? binding = null,
        string? controlPlaneInstanceId = null)
    {
        if (envelope.CalculateSize() > IpcLimits.MaxEnvelopeBytes)
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }

        if (envelope.ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedProtocolVersion);
        }

        if (!IsIdentifier(envelope.MessageId) ||
            (!string.IsNullOrEmpty(envelope.CorrelationId) && !IsIdentifier(envelope.CorrelationId)) ||
            envelope.WorkerEpoch == 0 ||
            !IsIdentifier(envelope.SessionId) || !IsIdentifier(envelope.WorkerId))
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }

        if (registered && (binding is null || !binding.Matches(envelope.SessionId, envelope.WorkerId, envelope.WorkerEpoch)))
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.BindingMismatch);
        }

        if (registered && controlPlaneInstanceId is not null &&
            !string.Equals(envelope.ControlPlaneInstanceId, controlPlaneInstanceId, StringComparison.Ordinal))
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.ControlPlaneMismatch);
        }

        if (!string.IsNullOrEmpty(envelope.ControlPlaneInstanceId) && !IsIdentifier(envelope.ControlPlaneInstanceId))
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }

        if (!registered && envelope.PayloadCase != WorkerEnvelope.PayloadOneofCase.Registration)
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedMessage);
        }

        return envelope.PayloadCase switch
        {
            WorkerEnvelope.PayloadOneofCase.Registration => ValidateRegistration(envelope.Registration, registered),
            WorkerEnvelope.PayloadOneofCase.Heartbeat => ValidateHeartbeat(envelope.Heartbeat),
            WorkerEnvelope.PayloadOneofCase.Ready => ValidateReady(envelope.Ready),
            WorkerEnvelope.PayloadOneofCase.DisplayBatch => ValidateDisplayBatch(envelope.DisplayBatch),
            WorkerEnvelope.PayloadOneofCase.InputResult => ValidateInputResult(envelope.InputResult),
            WorkerEnvelope.PayloadOneofCase.RuntimeCompleted => ValidateRuntimeCompleted(envelope.RuntimeCompleted),
            WorkerEnvelope.PayloadOneofCase.RuntimeFailed => ValidateRuntimeFailed(envelope.RuntimeFailed),
            WorkerEnvelope.PayloadOneofCase.WorkerStopped => ValidateWorkerStopped(envelope.WorkerStopped),
            WorkerEnvelope.PayloadOneofCase.CommandResult => ValidateCommandResult(envelope.CommandResult),
            _ => IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedMessage)
        };
    }

    public static IpcValidationResult ValidateWorkerCommandEnvelope(
        WorkerCommandEnvelope envelope,
        WorkerBinding binding,
        string controlPlaneInstanceId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (envelope.CalculateSize() > IpcLimits.MaxEnvelopeBytes)
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }

        if (envelope.ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedProtocolVersion);
        }

        if (!IsIdentifier(envelope.MessageId) ||
            (!string.IsNullOrEmpty(envelope.CorrelationId) && !IsIdentifier(envelope.CorrelationId)) ||
            !binding.Matches(envelope.SessionId, envelope.WorkerId, envelope.WorkerEpoch))
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.BindingMismatch);
        }

        if (!string.Equals(envelope.ControlPlaneInstanceId, controlPlaneInstanceId, StringComparison.Ordinal))
            return IpcValidationResult.Invalid(IpcReasonCodes.ControlPlaneMismatch);

        return envelope.PayloadCase switch
        {
            WorkerCommandEnvelope.PayloadOneofCase.RegistrationResult => ValidateRegistrationResult(envelope.RegistrationResult),
            WorkerCommandEnvelope.PayloadOneofCase.StartRuntime => ValidateStartRuntime(envelope.StartRuntime),
            WorkerCommandEnvelope.PayloadOneofCase.SubmitInput => ValidateSubmitInput(envelope.SubmitInput),
            WorkerCommandEnvelope.PayloadOneofCase.Stop => ValidateStop(envelope.Stop),
            _ => IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedMessage)
        };
    }

    public static void ValidateIdentifier(string value, string parameterName)
    {
        if (!IsIdentifier(value))
        {
            throw new ArgumentException($"{parameterName} is not an ASCII protocol identifier.", parameterName);
        }
    }

    public static void ValidateToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > IpcLimits.MaxTokenLength ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException($"{parameterName} is not a valid bootstrap token.", parameterName);
        }
    }

    public static void ValidateAbsolutePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"{parameterName} must be an absolute path.", parameterName);
        }
    }

    private static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= IpcLimits.MaxIdentifierLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~');

    private static IpcValidationResult ValidateRegistration(WorkerRegistration value, bool registered) =>
        (registered ? string.IsNullOrEmpty(value.StartupToken) || IsToken(value.StartupToken) : IsToken(value.StartupToken)) &&
        IsText(value.RuntimeIntegrationVersion) &&
        IsText(value.UpstreamCommit) &&
        value.ProcessId > 0 && value.LastOutputSequence >= 0 &&
        IsText(value.ProcessBootId) && value.ProcessStartTicks > 0
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateReady(WorkerReady value) =>
        IsText(value.RuntimeIntegrationVersion) && IsText(value.UpstreamCommit) &&
        value.SaveLayout is SaveLayout.Root or SaveLayout.SavDirectory &&
        value.LastOutputSequence >= 0 && IsText(value.CompatibilityProfile) &&
        value.SessionRootManifestDigest.Length <= IpcLimits.MaxStringLength
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateHeartbeat(WorkerHeartbeat value) =>
        value.MonotonicTimestampTicks >= 0 && value.OutputSequence >= 0 &&
        value.ResidentMemoryBytes >= 0 && value.ResidentMemoryBytes <= IpcLimits.MaxHeartbeatResidentBytes
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateDisplayBatch(DisplayBatch value)
    {
        if (value.FirstSequence <= 0 || value.LastSequence < value.FirstSequence ||
            value.Operations.Count == 0 || value.Operations.Count > IpcLimits.MaxDisplayOperations ||
            value.SnapshotSequence < 0 || value.DroppedNodeCount < 0)
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }

        int nodeCount = 0;
        foreach (ConsoleOperation operation in value.Operations)
        {
            if (!ValidateOperation(operation, ref nodeCount))
            {
                return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
            }
        }

        return nodeCount <= IpcLimits.MaxDisplayNodes
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
    }

    private static IpcValidationResult ValidateInputResult(InputResult value) =>
        IsIdentifier(value.PromptId) && IsIdentifier(value.ClientMessageId) &&
        value.Kind is not InputResultKind.InputResultUnspecified && value.Value.Length <= IpcLimits.MaxStringLength
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateRuntimeCompleted(RuntimeCompleted value) =>
        IsText(value.Status) && value.LastOutputSequence >= 0
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateRuntimeFailed(RuntimeFailed value) =>
        IsIdentifier(value.StableCode) && IsIdentifier(value.Phase) &&
        value.SafeMessage.Length <= IpcLimits.MaxProtocolErrorMessageLength && value.LastOutputSequence >= 0
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateWorkerStopped(WorkerStopped value) =>
        IsIdentifier(value.ReasonCode) && value.LastOutputSequence >= 0
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateCommandResult(WorkerCommandResult value) =>
        IsIdentifier(value.CommandType) && IsIdentifier(value.ReasonCode)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateRegistrationResult(RegistrationResult value) =>
        IsIdentifier(value.ReasonCode) &&
        value.NegotiatedProtocolVersion <= IpcProtocol.CurrentVersion &&
        value.RuntimeIntegrationVersion.Length <= IpcLimits.MaxStringLength &&
        value.UpstreamCommit.Length <= IpcLimits.MaxStringLength &&
        IsIdentifier(value.ControlPlaneInstanceId)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateStartRuntime(StartRuntime value) =>
        IsIdentifier(value.ExpectedSessionId) && IsIdentifier(value.ExpectedWorkerId) &&
        value.ExpectedWorkerEpoch > 0 && IsIdentifier(value.ExpectedCompatibilityProfile) &&
        value.DeadlineUnixMilliseconds > 0
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateSubmitInput(SubmitInput value) =>
        IsIdentifier(value.PromptId) && IsIdentifier(value.ClientMessageId) &&
        value.Value.Length <= IpcLimits.MaxStringLength && value.DeadlineUnixMilliseconds > 0
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateStop(StopWorker value) =>
        value.DeadlineUnixMilliseconds > 0 && IsIdentifier(value.ReasonCode)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static bool ValidateOperation(ConsoleOperation operation, ref int nodeCount)
    {
        switch (operation.PayloadCase)
        {
            case ConsoleOperation.PayloadOneofCase.AppendNodes:
                if (operation.AppendNodes.Nodes.Count == 0 || operation.AppendNodes.Nodes.Count > IpcLimits.MaxDisplayNodes)
                    return false;
                foreach (ConsoleNode node in operation.AppendNodes.Nodes)
                {
                    if (!ValidateNode(node, ref nodeCount, 1)) return false;
                }
                return true;
            case ConsoleOperation.PayloadOneofCase.ClearConsole:
                return true;
            case ConsoleOperation.PayloadOneofCase.OpenPrompt:
                return ValidatePrompt(operation.OpenPrompt.Prompt);
            case ConsoleOperation.PayloadOneofCase.ClosePrompt:
                return IsIdentifier(operation.ClosePrompt.PromptId) &&
                       operation.ClosePrompt.Reason is not PromptCloseReason.PromptCloseUnspecified;
            default:
                return false;
        }
    }

    private static bool ValidateNode(ConsoleNode node, ref int nodeCount, int depth)
    {
        if (++nodeCount > IpcLimits.MaxDisplayNodes || depth > 8)
            return false;

        return node.KindCase switch
        {
            ConsoleNode.KindOneofCase.Text => node.Text.Text.Length <= IpcLimits.MaxStringLength,
            ConsoleNode.KindOneofCase.LineBreak => true,
            ConsoleNode.KindOneofCase.Image => IsText(node.Image.AssetId) &&
                node.Image.Width >= 0 && node.Image.Height >= 0 &&
                node.Image.AltText.Length <= IpcLimits.MaxStringLength,
            ConsoleNode.KindOneofCase.Button => IsText(node.Button.Value) &&
                node.Button.Label.Count > 0 && node.Button.Label.Count <= 16 &&
                node.Button.Tooltip.Length <= IpcLimits.MaxStringLength &&
                node.Button.Label.All(child => child.KindCase == ConsoleNode.KindOneofCase.Text &&
                    child.Text.Text.Length <= IpcLimits.MaxStringLength),
            _ => false
        };
    }

    private static bool ValidatePrompt(ConsolePrompt prompt) =>
        IsIdentifier(prompt.PromptId) &&
        prompt.InputType is ConsoleInputType.ConsoleInputText or ConsoleInputType.ConsoleInputInteger &&
        prompt.PromptText.Length <= IpcLimits.MaxStringLength &&
        prompt.DefaultValue.Length <= IpcLimits.MaxStringLength &&
        prompt.TimeoutBehavior is not PromptTimeoutBehavior.PromptTimeoutUnspecified &&
        (!prompt.HasTimeout || prompt.TimeoutMilliseconds >= 0) &&
        prompt.Constraints.KindCase is InputConstraints.KindOneofCase.Text or InputConstraints.KindOneofCase.Integer;

    private static bool IsText(string value) => value.Length <= IpcLimits.MaxStringLength;

    private static bool IsToken(string value) =>
        value.Length is > 0 and <= IpcLimits.MaxTokenLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

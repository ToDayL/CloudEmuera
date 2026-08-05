using Google.Protobuf;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V1;
using ProtoConsoleColor = CloudEmuera.Ipc.V1.ConsoleColor;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace CloudEmuera.Ipc.ContractTests;

[Trait("Category", "IpcContract")]
public sealed class IpcContractTests
{
    private static readonly WorkerBinding Binding = new("sess_contract", "wrk_contract", 7);

    [Fact]
    public void V1EnvelopeRoundTripsStructuredDisplayAndInputPayloads()
    {
        var original = new WorkerEnvelope
        {
            ProtocolVersion = IpcProtocol.CurrentVersion,
            MessageId = "display_1",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            DisplayBatch = new DisplayBatch
            {
                FirstSequence = 1,
                LastSequence = 1
            }
        };
        original.DisplayBatch.Operations.Add(new ConsoleOperation
        {
            AppendNodes = new AppendNodes
            {
                Nodes =
                {
                    new ConsoleNode
                    {
                        Text = new TextNode
                        {
                            Text = "safe text",
                            Style = new TextStyle
                            {
                                Decorations = 1,
                                Foreground = new ProtoConsoleColor { Red = 1, Green = 2, Blue = 3, Alpha = 255 }
                            }
                        }
                    },
                    new ConsoleNode { LineBreak = new LineBreakNode() }
                }
            }
        });

        WorkerEnvelope parsed = WorkerEnvelope.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(original, parsed);
        Assert.True(IpcValidator.ValidateWorkerEnvelope(parsed, registered: true, Binding).IsValid);
    }

    [Fact]
    public void UnknownFieldIsPreservedByV1ParserAndSerializer()
    {
        var original = new WorkerEnvelope
        {
            ProtocolVersion = IpcProtocol.CurrentVersion,
            MessageId = "heartbeat_1",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            Heartbeat = new WorkerHeartbeat { OutputSequence = 3 }
        };
        byte[] known = original.ToByteArray();
        byte[] withUnknown = [.. known, 0xC8, 0x06, 0x01]; // field 99, varint 1

        WorkerEnvelope parsed = WorkerEnvelope.Parser.ParseFrom(withUnknown);
        byte[] roundTripped = parsed.ToByteArray();

        Assert.Equal(original.MessageId, parsed.MessageId);
        Assert.True(roundTripped.AsSpan().IndexOf(new byte[] { 0xC8, 0x06, 0x01 }) >= 0);
    }

    [Theory]
    [InlineData(0, "unsupported_protocol_version")]
    [InlineData(2, "unsupported_protocol_version")]
    public void UnsupportedProtocolVersionIsRejectedBeforePayloadRouting(uint version, string reason)
    {
        WorkerEnvelope envelope = CreateRegistration(version);

        IpcValidationResult result = IpcValidator.ValidateWorkerEnvelope(envelope, registered: false);

        Assert.False(result.IsValid);
        Assert.Equal(reason, result.ReasonCode);
    }

    [Fact]
    public void RegistrationRequiresBindingAndTokenBeforeRuntimeMessages()
    {
        WorkerEnvelope envelope = new()
        {
            ProtocolVersion = IpcProtocol.CurrentVersion,
            MessageId = "heartbeat_1",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            Heartbeat = new WorkerHeartbeat { OutputSequence = 1 }
        };

        IpcValidationResult unregistered = IpcValidator.ValidateWorkerEnvelope(envelope, registered: false);
        IpcValidationResult mismatched = IpcValidator.ValidateWorkerEnvelope(
            envelope,
            registered: true,
            new WorkerBinding("sess_other", Binding.WorkerId, Binding.WorkerEpoch));

        Assert.Equal(IpcReasonCodes.UnsupportedMessage, unregistered.ReasonCode);
        Assert.Equal(IpcReasonCodes.BindingMismatch, mismatched.ReasonCode);
    }

    [Fact]
    public void UnspecifiedEnumAndOversizedBatchAreRejected()
    {
        WorkerEnvelope unspecified = CreateEnvelope(new InputResult
        {
            PromptId = "prompt_1",
            ClientMessageId = "client_1"
        });
        var oversizedBatch = new DisplayBatch { FirstSequence = 1, LastSequence = IpcLimits.MaxDisplayOperations + 1 };
        for (int index = 0; index < IpcLimits.MaxDisplayOperations + 1; index++)
        {
            oversizedBatch.Operations.Add(new ConsoleOperation { ClearConsole = new ClearConsole() });
        }

        IpcValidationResult enumResult = IpcValidator.ValidateWorkerEnvelope(unspecified, registered: true, Binding);
        IpcValidationResult batchResult = IpcValidator.ValidateWorkerEnvelope(
            CreateEnvelope(oversizedBatch), registered: true, Binding);

        Assert.Equal(IpcReasonCodes.InvalidEnvelope, enumResult.ReasonCode);
        Assert.Equal(IpcReasonCodes.InvalidEnvelope, batchResult.ReasonCode);
    }

    [Fact]
    public void PublishedFieldNumbersAreStableAndCorrelationIsSeparate()
    {
        Assert.Equal(1, WorkerEnvelope.Descriptor.FindFieldByName("protocol_version").FieldNumber);
        Assert.Equal(2, WorkerEnvelope.Descriptor.FindFieldByName("message_id").FieldNumber);
        Assert.Equal(6, WorkerEnvelope.Descriptor.FindFieldByName("correlation_id").FieldNumber);
        Assert.Equal(10, WorkerEnvelope.Descriptor.FindFieldByName("registration").FieldNumber);
        Assert.Equal(13, WorkerEnvelope.Descriptor.FindFieldByName("display_batch").FieldNumber);
        Assert.Equal(13, SupervisorEnvelope.Descriptor.FindFieldByName("submit_input").FieldNumber);
    }

    [Fact]
    public void BootstrapFileUsesPrivatePermissionsAndRejectsHardLinks()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-ipc-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "bootstrap.json");
        string hardLink = Path.Combine(root, "bootstrap-link.json");
        try
        {
            WorkerBootstrapFile.Write(path, CreateBootstrapDocument());
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(root));

            Assert.Equal(0, Link(path, hardLink));
            Assert.Throws<UnauthorizedAccessException>(() => WorkerBootstrapFile.Read(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BootstrapFileRejectsSymlinkAndOverbroadPermissions()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-ipc-bootstrap-security", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = Path.Combine(root, "bootstrap.json");
        string symlink = Path.Combine(root, "bootstrap-symlink.json");
        try
        {
            WorkerBootstrapFile.Write(path, CreateBootstrapDocument());
            File.CreateSymbolicLink(symlink, path);
            Assert.Throws<FileNotFoundException>(() => WorkerBootstrapFile.Read(symlink));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<UnauthorizedAccessException>(() => WorkerBootstrapFile.Read(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WorkerEnvelope CreateRegistration(uint version) => new()
    {
        ProtocolVersion = version,
        MessageId = "registration_1",
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        Registration = new WorkerRegistration
        {
            StartupToken = "token_1",
            RuntimeIntegrationVersion = "headless-p0.5.1",
            UpstreamCommit = "commit",
            ProcessId = 1
        }
    };

    private static WorkerEnvelope CreateEnvelope(DisplayBatch batch) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = "display_1",
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        DisplayBatch = batch
    };

    private static WorkerEnvelope CreateEnvelope(InputResult result) => new()
    {
        ProtocolVersion = IpcProtocol.CurrentVersion,
        MessageId = "input_1",
        SessionId = Binding.SessionId,
        WorkerId = Binding.WorkerId,
        WorkerEpoch = Binding.WorkerEpoch,
        InputResult = result
    };

    private static WorkerBootstrapDocument CreateBootstrapDocument() => new()
    {
        SessionId = "sess_bootstrap",
        WorkerId = "wrk_bootstrap",
        WorkerEpoch = 1,
        SessionRoot = Path.Combine(Path.GetTempPath(), "session-root"),
        CompatibilityProfile = "v18-compatible",
        SupervisorSocketPath = Path.Combine(Path.GetTempPath(), "supervisor.sock"),
        BootstrapToken = IpcProtocol.CreateBootstrapToken(),
        ConnectDeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
        HeartbeatIntervalMilliseconds = 500,
        ShutdownGracePeriodMilliseconds = 5_000,
        SaveLayout = 0
    };

    [SuppressMessage("Security", "CA2101", Justification = "The test P/Invoke explicitly marshals both paths as UTF-8.")]
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}

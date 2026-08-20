using Google.Protobuf;
using Google.Protobuf.Reflection;
using CloudEmuera.Ipc;
using W = CloudEmuera.Ipc.V4;
using Xunit;

namespace CloudEmuera.Ipc.ContractTests;

[Trait("Category", "IpcContract")]
public sealed class StructuredIpcContractTests
{
    private static readonly WorkerBinding Binding = new("sess_structured", "wrk_structured", 9);

    [Fact]
    public void V4InputDescriptorReservesPromptFieldAndUsesOptionalResolvedPromptPresence()
    {
        MessageDescriptor submitInput = W.StructuredWorkerReflection.Descriptor.MessageTypes.Single(message => message.Name == "SubmitInput");
        MessageDescriptor inputResult = W.StructuredWorkerReflection.Descriptor.MessageTypes.Single(message => message.Name == "InputResult");

        Assert.Null(submitInput.FindFieldByNumber(1));
        AssertReservedField(submitInput, 1);
        Assert.Null(inputResult.FindFieldByNumber(1));
        AssertReservedField(inputResult, 1);

        var withoutResolvedPrompt = new W.InputResult
        {
            ClientMessageId = "client-1",
            Kind = W.InputResultKind.NoActivePrompt,
            ReasonCode = "no_active_prompt",
        };
        W.InputResult parsedWithoutResolvedPrompt = W.InputResult.Parser.ParseFrom(withoutResolvedPrompt.ToByteArray());
        Assert.False(parsedWithoutResolvedPrompt.HasResolvedPromptId);

        var withResolvedPrompt = withoutResolvedPrompt.Clone();
        withResolvedPrompt.ResolvedPromptId = "prompt-1";
        W.InputResult parsedWithResolvedPrompt = W.InputResult.Parser.ParseFrom(withResolvedPrompt.ToByteArray());
        Assert.True(parsedWithResolvedPrompt.HasResolvedPromptId);
        Assert.Equal("prompt-1", parsedWithResolvedPrompt.ResolvedPromptId);
    }

    private static void AssertReservedField(MessageDescriptor message, int fieldNumber)
    {
        FileDescriptorProto file = FileDescriptorProto.Parser.ParseFrom(message.File.SerializedData);
        DescriptorProto descriptor = file.MessageType.Single(candidate => candidate.Name == message.Name);
        Assert.Contains(descriptor.ReservedRange, range => range.Start <= fieldNumber && range.End > fieldNumber);
    }

    [Fact]
    public void RegistrationHandshakeCarriesProtocolAndCapabilityDigest()
    {
        W.WorkerEnvelope original = StructuredIpcHandshake.CreateRegistration(
            Binding,
            "registration-1",
            "startup_token_1",
            "structured-p1-07",
            Environment.ProcessId);
        W.WorkerEnvelope parsed = W.WorkerEnvelope.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(original, parsed);
        Assert.True(StructuredIpcValidator.ValidateWorkerEnvelope(parsed, registered: false).IsValid);

        W.WorkerCommandEnvelope result = StructuredIpcHandshake.CreateRegistrationResult(
            Binding,
            "registration-result-1",
            "control-plane-1",
            accepted: true);
        Assert.True(StructuredIpcValidator.ValidateCommandEnvelope(
            result,
            Binding,
            "control-plane-1",
            StructuredIpcProtocol.CapabilitySetDigest).IsValid);
    }

    [Fact]
    public void RichDisplayTransactionRoundTripsWithoutFlattening()
    {
        var display = new W.DisplayBatch();
        var transaction = new W.ConsoleTransaction { Sequence = 17 };
        transaction.Operations.Add(new W.ConsoleOperation
        {
            AppendLine = new W.AppendLine
            {
                Line = new W.ConsoleLine
                {
                    LineId = "line-17",
                    Alignment = W.LineAlignment.Center,
                    Temporary = true,
                    Nodes =
                    {
                        new W.ConsoleNode
                        {
                            Text = new W.TextNode
                            {
                                Text = "保留结构",
                                Style = new W.TextStyle
                                {
                                    FontFamily = "noto-cjk",
                                    FontSize = 18,
                                    LineHeight = 24,
                                    Decorations = 1
                                }
                            }
                        },
                        new W.ConsoleNode
                        {
                            Sprite = new W.SpriteNode
                            {
                                AssetId = "sprite-asset",
                                SourceRect = new W.Rect { Width = 16, Height = 16 },
                                Destination = new W.Rect { Width = 32, Height = 32 },
                                Opacity = 1f,
                                HasHover = true,
                                HoverAssetId = "sprite-hover",
                                HoverSourceRect = new W.Rect { X = 16, Width = 16, Height = 16 },
                                HasMapping = true,
                                MappingAssetId = "sprite-map",
                                MappingSourceRect = new W.Rect { Width = 32, Height = 32 }
                            }
                        }
                    }
                }
            }
        });
        transaction.Operations.Add(new W.ConsoleOperation
        {
            SetMediaChannel = new W.SetMediaChannel
            {
                Channel = new W.MediaChannelState
                {
                    Channel = "music",
                    AssetId = "audio-asset",
                    HasAssetId = true,
                    PlaybackState = W.MediaPlaybackState.Requested,
                    StartPolicy = W.MediaStartPolicy.OnUserGesture,
                    Volume = 0.5f,
                    Revision = 1
                }
            }
        });
        display.Transactions.Add(transaction);

        var envelope = new W.WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "display-17",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            DisplayBatch = display
        };
        W.WorkerEnvelope parsed = W.WorkerEnvelope.Parser.ParseFrom(envelope.ToByteArray());

        Assert.Equal(envelope, parsed);
        Assert.True(StructuredIpcValidator.ValidateWorkerEnvelope(
            parsed,
            registered: true,
            Binding,
            StructuredIpcProtocol.CapabilitySetDigest).IsValid);
        Assert.Equal(W.LineAlignment.Center, parsed.DisplayBatch.Transactions[0].Operations[0].AppendLine.Line.Alignment);
        Assert.Equal(W.MediaStartPolicy.OnUserGesture, parsed.DisplayBatch.Transactions[0].Operations[1].SetMediaChannel.Channel.StartPolicy);
    }

    [Fact]
    public void ButtonLabelBudgetMatchesTheRuntimeContract()
    {
        var button = new W.ButtonNode { Value = "choice", Tooltip = string.Empty, Enabled = true };
        for (int index = 0; index < StructuredIpcLimits.MaxButtonLabelNodes; index++)
        {
            button.Label.Add(new W.ConsoleNode
            {
                Text = new W.TextNode
                {
                    Text = "x",
                    Style = new W.TextStyle { FontFamily = "default", FontSize = 16 }
                }
            });
        }

        var envelope = new W.WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "button-label-budget",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            DisplayBatch = new W.DisplayBatch
            {
                Transactions =
                {
                    new W.ConsoleTransaction
                    {
                        Sequence = 1,
                        Operations =
                        {
                            new W.ConsoleOperation
                            {
                                AppendLine = new W.AppendLine
                                {
                                    Line = new W.ConsoleLine
                                    {
                                        LineId = "line-button-budget",
                                        Alignment = W.LineAlignment.Left,
                                        Nodes = { new W.ConsoleNode { Button = button } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        Assert.True(StructuredIpcValidator.ValidateWorkerEnvelope(envelope, true, Binding).IsValid);

        button.Label.Add(button.Label[0]);
        Assert.Equal(IpcReasonCodes.InvalidEnvelope, StructuredIpcValidator.ValidateWorkerEnvelope(envelope, true, Binding).ReasonCode);
    }

    [Fact]
    public void RasterDrawableRequiresPngSignatureAtIpcBoundary()
    {
        var raster = new W.RasterDrawable
        {
            DrawableId = "raster-1",
            PngData = ByteString.CopyFrom(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            Bounds = new W.Rect { Width = 1, Height = 1 },
            Opacity = 1f
        };
        var envelope = new W.WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "raster-1",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            DisplayBatch = new W.DisplayBatch
            {
                Transactions =
                {
                    new W.ConsoleTransaction
                    {
                        Sequence = 1,
                        Operations =
                        {
                            new W.ConsoleOperation
                            {
                                UpsertDrawable = new W.UpsertDrawable
                                {
                                    Drawable = new W.CanvasDrawable { Raster = raster }
                                }
                            }
                        }
                    }
                }
            }
        };

        Assert.True(StructuredIpcValidator.ValidateWorkerEnvelope(envelope, true, Binding).IsValid);

        raster.PngData = ByteString.CopyFrom(new byte[8]);
        Assert.Equal(IpcReasonCodes.InvalidEnvelope, StructuredIpcValidator.ValidateWorkerEnvelope(envelope, true, Binding).ReasonCode);
    }

    [Fact]
    public void ExplicitSnapshotAndUnknownOrMismatchedPeerAreRejectedFailClosed()
    {
        var snapshot = new W.ConsoleSnapshot
        {
            SnapshotSequence = 3,
            CanvasScene = new W.CanvasScene(),
            MediaState = new W.MediaState(),
            WindowMetadata = new W.WindowMetadata
            {
                DefaultFont = new W.TextStyle { FontFamily = "default", FontSize = 16 }
            },
            Truncation = new W.TruncationMetadata()
        };
        snapshot.Scrollback.Add(new W.ConsoleLine
        {
            LineId = "line-3",
            Alignment = W.LineAlignment.Left,
            Nodes = { new W.ConsoleNode { LineBreak = new W.LineBreakNode() } }
        });
        var envelope = new W.WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "snapshot-3",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            DisplayBatch = new W.DisplayBatch { IsSnapshot = true, Snapshot = snapshot }
        };

        Assert.True(StructuredIpcValidator.ValidateWorkerEnvelope(envelope, true, Binding).IsValid);

        envelope.CapabilitySetDigest = new string('0', 64);
        Assert.False(StructuredIpcValidator.ValidateWorkerEnvelope(
            envelope,
            true,
            Binding,
            StructuredIpcProtocol.CapabilitySetDigest).IsValid);
        Assert.Equal(
            IpcReasonCodes.UnsupportedProtocolVersion,
            StructuredIpcHandshake.ValidatePeer(2, StructuredIpcProtocol.CapabilitySetDigest).ReasonCode);

        var unknown = new W.WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "unknown-1",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            DisplayBatch = new W.DisplayBatch
            {
                Transactions =
                {
                    new W.ConsoleTransaction
                    {
                        Sequence = 1,
                        Operations =
                        {
                            new W.ConsoleOperation
                            {
                                AppendLine = new W.AppendLine
                                {
                                    Line = new W.ConsoleLine { LineId = "line-1", Alignment = W.LineAlignment.Unspecified }
                                }
                            }
                        }
                    }
                }
            }
        };
        Assert.Equal(IpcReasonCodes.InvalidEnvelope, StructuredIpcValidator.ValidateWorkerEnvelope(unknown, true, Binding).ReasonCode);
    }

    [Fact]
    public void HeartbeatAllowsPromptTimingWithoutDeadlineButRequiresZeroRemaining()
    {
        var envelope = new W.WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "heartbeat-1",
            SessionId = Binding.SessionId,
            WorkerId = Binding.WorkerId,
            WorkerEpoch = Binding.WorkerEpoch,
            ControlPlaneInstanceId = "control-plane-1",
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            Heartbeat = new W.WorkerHeartbeat
            {
                MonotonicTimestampTicks = 1,
                OutputSequence = 1,
                WaitingForInput = true,
                CurrentPromptId = "prompt-1",
                PromptTiming = new W.PromptTiming
                {
                    OpenedAtUnixMilliseconds = 100,
                    ServerNowUnixMilliseconds = 200,
                    RemainingMilliseconds = 0
                },
                ResidentMemoryBytes = 1
            }
        };

        Assert.True(StructuredIpcValidator.ValidateWorkerEnvelope(
            envelope,
            registered: true,
            Binding,
            StructuredIpcProtocol.CapabilitySetDigest).IsValid);

        envelope.Heartbeat.PromptTiming.RemainingMilliseconds = 1;
        Assert.False(StructuredIpcValidator.ValidateWorkerEnvelope(
            envelope,
            registered: true,
            Binding,
            StructuredIpcProtocol.CapabilitySetDigest).IsValid);
    }
}

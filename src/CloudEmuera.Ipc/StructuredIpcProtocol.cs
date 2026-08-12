using System.Security.Cryptography;
using System.Text;
using CloudEmuera.Ipc.V3;
using ProtoConsoleColor = CloudEmuera.Ipc.V3.ConsoleColor;

namespace CloudEmuera.Ipc;

/// <summary>Versioned constants for the lossless structured Worker protocol.</summary>
public static class StructuredIpcProtocol
{
    public const uint CurrentVersion = 3;
    public const string CapabilityMatrixVersion = "p1-07";
    public const string UpstreamCommit = "2175f8a629257efb08214e093704b3a3d3d06d05";

    public static string CapabilitySetDigest { get; } = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"cloudemuera:{CapabilityMatrixVersion}:{UpstreamCommit}:structured-console-v3")))
        .ToLowerInvariant();
}

/// <summary>
/// Builds and checks the v3 bootstrap handshake.  Registration carries the
/// same capability digest in the envelope and payload so a peer cannot
/// silently downgrade by dropping the new contract metadata.
/// </summary>
public static class StructuredIpcHandshake
{
    public static WorkerEnvelope CreateRegistration(
        WorkerBinding binding,
        string messageId,
        string startupToken,
        string runtimeIntegrationVersion,
        long processId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var envelope = new WorkerEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = messageId,
            SessionId = binding.SessionId,
            WorkerId = binding.WorkerId,
            WorkerEpoch = binding.WorkerEpoch,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            Registration = new WorkerRegistration
            {
                StartupToken = startupToken,
                RuntimeIntegrationVersion = runtimeIntegrationVersion,
                UpstreamCommit = StructuredIpcProtocol.UpstreamCommit,
                ProcessId = processId,
                ProcessBootId = "handshake",
                ProcessStartTicks = 1,
                LastOutputSequence = 0,
                CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest
            }
        };
        return envelope;
    }

    public static WorkerCommandEnvelope CreateRegistrationResult(
        WorkerBinding binding,
        string messageId,
        string controlPlaneInstanceId,
        bool accepted,
        string reasonCode = IpcReasonCodes.Accepted)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new WorkerCommandEnvelope
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = messageId,
            SessionId = binding.SessionId,
            WorkerId = binding.WorkerId,
            WorkerEpoch = binding.WorkerEpoch,
            ControlPlaneInstanceId = controlPlaneInstanceId,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            RegistrationResult = new RegistrationResult
            {
                Accepted = accepted,
                ReasonCode = reasonCode,
                NegotiatedProtocolVersion = StructuredIpcProtocol.CurrentVersion,
                CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
                RuntimeIntegrationVersion = "headless-p0.5.1",
                UpstreamCommit = StructuredIpcProtocol.UpstreamCommit,
                ControlPlaneInstanceId = controlPlaneInstanceId
            }
        };
    }

    public static IpcValidationResult ValidatePeer(
        uint protocolVersion,
        string capabilitySetDigest,
        uint expectedProtocolVersion = StructuredIpcProtocol.CurrentVersion,
        string? expectedCapabilitySetDigest = null)
    {
        if (protocolVersion != expectedProtocolVersion)
            return IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedProtocolVersion);
        if (!string.Equals(
                capabilitySetDigest,
                expectedCapabilitySetDigest ?? StructuredIpcProtocol.CapabilitySetDigest,
                StringComparison.Ordinal))
            return IpcValidationResult.Invalid(IpcReasonCodes.BindingMismatch);
        return IpcValidationResult.Valid();
    }
}

public static class StructuredIpcLimits
{
    public const int MaxEnvelopeBytes = 512 * 1024;
    public const int MaxIdentifierLength = 128;
    public const int MaxStringLength = 32 * 1024;
    public const int MaxInputLength = 16_384;
    public const int MaxTransactions = 512;
    public const int MaxOperationsPerTransaction = 128;
    public const int MaxNodes = 8_192;
    public const int MaxScrollbackLines = 4_096;
    public const int MaxSceneItems = 2_048;
    public const int MaxHtmlDepth = 16;
    public const int MaxHtmlChildren = 256;
    public const int MaxGeometryPoints = 256;
    public const int MaxProtocolErrorMessageLength = 512;
}

public static class StructuredIpcValidator
{
    public static IpcValidationResult ValidateWorkerEnvelope(
        WorkerEnvelope envelope,
        bool registered,
        WorkerBinding? binding = null,
        string? expectedCapabilitySetDigest = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ProtocolVersion != StructuredIpcProtocol.CurrentVersion ||
            !IsIdentifier(envelope.MessageId) ||
            !IsIdentifier(envelope.SessionId) ||
            !IsIdentifier(envelope.WorkerId) ||
            envelope.WorkerEpoch == 0 ||
            (!string.IsNullOrEmpty(envelope.CorrelationId) && !IsIdentifier(envelope.CorrelationId)) ||
            !IsDigest(envelope.CapabilitySetDigest) ||
            (expectedCapabilitySetDigest is not null && !string.Equals(
                envelope.CapabilitySetDigest,
                expectedCapabilitySetDigest,
                StringComparison.Ordinal)) ||
            binding is not null && !binding.Matches(envelope.SessionId, envelope.WorkerId, envelope.WorkerEpoch))
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }

        if (!registered && envelope.PayloadCase != WorkerEnvelope.PayloadOneofCase.Registration)
            return IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedMessage);
        if (registered && envelope.PayloadCase == WorkerEnvelope.PayloadOneofCase.Registration)
            return IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedMessage);

        return envelope.PayloadCase switch
        {
            WorkerEnvelope.PayloadOneofCase.Registration => ValidateRegistration(envelope.Registration, envelope.CapabilitySetDigest),
            WorkerEnvelope.PayloadOneofCase.Ready => ValidateReady(envelope.Ready, envelope.CapabilitySetDigest),
            WorkerEnvelope.PayloadOneofCase.Heartbeat => ValidateHeartbeat(envelope.Heartbeat),
            WorkerEnvelope.PayloadOneofCase.DisplayBatch => ValidateDisplayBatch(envelope.DisplayBatch),
            WorkerEnvelope.PayloadOneofCase.InputResult => ValidateInputResult(envelope.InputResult),
            WorkerEnvelope.PayloadOneofCase.RuntimeCompleted => envelope.RuntimeCompleted.LastOutputSequence >= 0
                ? IpcValidationResult.Valid()
                : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope),
            WorkerEnvelope.PayloadOneofCase.RuntimeFailed => ValidateRuntimeFailed(envelope.RuntimeFailed),
            WorkerEnvelope.PayloadOneofCase.WorkerStopped => ValidateWorkerStopped(envelope.WorkerStopped),
            WorkerEnvelope.PayloadOneofCase.CommandResult => ValidateCommandResult(envelope.CommandResult),
            _ => IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedMessage)
        };
    }

    public static IpcValidationResult ValidateCommandEnvelope(
        WorkerCommandEnvelope envelope,
        WorkerBinding binding,
        string controlPlaneInstanceId,
        string? expectedCapabilitySetDigest = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(binding);
        if (envelope.ProtocolVersion != StructuredIpcProtocol.CurrentVersion ||
            !IsIdentifier(envelope.MessageId) ||
            !IsIdentifier(envelope.SessionId) ||
            !IsIdentifier(envelope.WorkerId) ||
            envelope.WorkerEpoch == 0 ||
            !binding.Matches(envelope.SessionId, envelope.WorkerId, envelope.WorkerEpoch) ||
            !IsIdentifier(controlPlaneInstanceId) ||
            !string.Equals(envelope.ControlPlaneInstanceId, controlPlaneInstanceId, StringComparison.Ordinal) ||
            !IsDigest(envelope.CapabilitySetDigest) ||
            expectedCapabilitySetDigest is not null && !string.Equals(
                envelope.CapabilitySetDigest,
                expectedCapabilitySetDigest,
                StringComparison.Ordinal))
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.BindingMismatch);
        }

        return envelope.PayloadCase switch
        {
            WorkerCommandEnvelope.PayloadOneofCase.RegistrationResult => ValidateRegistrationResult(envelope.RegistrationResult, envelope.CapabilitySetDigest),
            WorkerCommandEnvelope.PayloadOneofCase.StartRuntime => ValidateStart(envelope.StartRuntime),
            WorkerCommandEnvelope.PayloadOneofCase.SubmitInput => ValidateSubmitInput(envelope.SubmitInput),
            WorkerCommandEnvelope.PayloadOneofCase.Stop => ValidateStop(envelope.Stop),
            _ => IpcValidationResult.Invalid(IpcReasonCodes.UnsupportedMessage)
        };
    }

    private static IpcValidationResult ValidateRegistration(WorkerRegistration value, string envelopeDigest) =>
        IsToken(value.StartupToken) && IsText(value.RuntimeIntegrationVersion) && IsText(value.UpstreamCommit) &&
        value.ProcessId > 0 && IsText(value.ProcessBootId) && value.ProcessStartTicks > 0 && value.LastOutputSequence >= 0 &&
        IsDigest(value.CapabilitySetDigest) &&
        string.Equals(value.CapabilitySetDigest, envelopeDigest, StringComparison.Ordinal)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateReady(WorkerReady value, string envelopeDigest) =>
        IsText(value.RuntimeIntegrationVersion) && IsText(value.UpstreamCommit) &&
        value.LastOutputSequence >= 0 && IsIdentifier(value.CompatibilityProfile) &&
        IsDigest(value.CapabilitySetDigest) && IsText(value.SessionRootManifestDigest) &&
        value.SaveLayout is SaveLayout.Root or SaveLayout.SavDirectory &&
        string.Equals(value.CapabilitySetDigest, envelopeDigest, StringComparison.Ordinal)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateHeartbeat(WorkerHeartbeat value) =>
        value.MonotonicTimestampTicks >= 0 && value.OutputSequence >= 0 &&
        value.ResidentMemoryBytes >= 0 && value.ResidentMemoryBytes <= (1L << 50) &&
        (!string.IsNullOrEmpty(value.CurrentPromptId) && !IsIdentifier(value.CurrentPromptId) ||
         value.PromptTiming is not null && !ValidateTiming(value.PromptTiming))
            ? IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope)
            : IpcValidationResult.Valid();

    private static IpcValidationResult ValidateDisplayBatch(DisplayBatch value)
    {
        if (value.IsSnapshot)
        {
            if (value.Snapshot is null || !ValidateSnapshot(value.Snapshot))
                return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }
        else if (value.Snapshot is not null || value.Transactions.Count == 0)
        {
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        }

        if (value.Transactions.Count > StructuredIpcLimits.MaxTransactions)
            return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
        long previousSequence = 0;
        int nodeCount = 0;
        foreach (ConsoleTransaction transaction in value.Transactions)
        {
            if (transaction.Sequence <= previousSequence || transaction.Sequence <= 0 ||
                transaction.Operations.Count == 0 || transaction.Operations.Count > StructuredIpcLimits.MaxOperationsPerTransaction)
                return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
            previousSequence = transaction.Sequence;
            foreach (ConsoleOperation operation in transaction.Operations)
            {
                if (!ValidateOperation(operation, ref nodeCount))
                    return IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
            }
        }

        return nodeCount <= StructuredIpcLimits.MaxNodes
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);
    }

    private static bool ValidateSnapshot(ConsoleSnapshot value)
    {
        if (value.SnapshotSequence < 0 || value.Scrollback.Count > StructuredIpcLimits.MaxScrollbackLines ||
            value.BackgroundLayers.Count > StructuredIpcLimits.MaxSceneItems || value.CanvasScene is null ||
            value.CanvasScene.Drawables.Count > StructuredIpcLimits.MaxSceneItems ||
            value.CanvasScene.HitRegions.Count > StructuredIpcLimits.MaxSceneItems ||
            value.MediaState is null || value.MediaState.Channels.Count > 32 ||
            value.WindowMetadata is null || value.Truncation is null ||
            !ValidateWindow(value.WindowMetadata) || !ValidateTruncation(value.Truncation))
            return false;

        int nodes = 0;
        foreach (ConsoleLine line in value.Scrollback)
        {
            if (!IsIdentifier(line.LineId) || line.Alignment == LineAlignment.Unspecified)
                return false;
            foreach (ConsoleNode node in line.Nodes)
                if (!ValidateNode(node, ref nodes, 1)) return false;
        }

        foreach (BackgroundLayer layer in value.BackgroundLayers)
            if (!ValidateBackground(layer)) return false;
        foreach (CanvasDrawable drawable in value.CanvasScene.Drawables)
            if (!ValidateDrawable(drawable)) return false;
        foreach (HitRegion region in value.CanvasScene.HitRegions)
            if (!ValidateHitRegion(region)) return false;
        foreach (MediaChannelState channel in value.MediaState.Channels)
            if (!ValidateMedia(channel)) return false;
        return !value.HasCurrentPrompt || ValidatePrompt(value.CurrentPrompt);
    }

    private static bool ValidateOperation(ConsoleOperation operation, ref int nodeCount)
    {
        switch (operation.PayloadCase)
        {
            case ConsoleOperation.PayloadOneofCase.AppendNodes:
                if (operation.AppendNodes.Nodes.Count == 0) return false;
                return ValidateNodeList(operation.AppendNodes.Nodes, ref nodeCount);
            case ConsoleOperation.PayloadOneofCase.ClearScrollback:
                return true;
            case ConsoleOperation.PayloadOneofCase.OpenPrompt:
                return operation.OpenPrompt.Prompt is not null && ValidatePrompt(operation.OpenPrompt.Prompt);
            case ConsoleOperation.PayloadOneofCase.ClosePrompt:
                return IsIdentifier(operation.ClosePrompt.PromptId) && operation.ClosePrompt.Reason != PromptCloseReason.Unspecified;
            case ConsoleOperation.PayloadOneofCase.AppendLine:
                return operation.AppendLine.Line is not null && ValidateLine(operation.AppendLine.Line, ref nodeCount);
            case ConsoleOperation.PayloadOneofCase.AppendInline:
                return IsIdentifier(operation.AppendInline.LineId) && operation.AppendInline.Nodes.Count > 0 &&
                    ValidateNodeList(operation.AppendInline.Nodes, ref nodeCount);
            case ConsoleOperation.PayloadOneofCase.ReplaceLine:
                return operation.ReplaceLine.Line is not null && ValidateLine(operation.ReplaceLine.Line, ref nodeCount);
            case ConsoleOperation.PayloadOneofCase.DeleteLines:
                return operation.DeleteLines.LineIds.Count > 0 && operation.DeleteLines.LineIds.All(IsIdentifier);
            case ConsoleOperation.PayloadOneofCase.SetWindowMetadata:
                return operation.SetWindowMetadata.Metadata is not null && ValidateWindow(operation.SetWindowMetadata.Metadata);
            case ConsoleOperation.PayloadOneofCase.UpsertBackground:
                return operation.UpsertBackground.Layer is not null && ValidateBackground(operation.UpsertBackground.Layer);
            case ConsoleOperation.PayloadOneofCase.RemoveBackground:
                return IsIdentifier(operation.RemoveBackground.LayerId);
            case ConsoleOperation.PayloadOneofCase.ClearBackgrounds:
                return true;
            case ConsoleOperation.PayloadOneofCase.UpsertDrawable:
                return operation.UpsertDrawable.Drawable is not null && ValidateDrawable(operation.UpsertDrawable.Drawable);
            case ConsoleOperation.PayloadOneofCase.RemoveDrawable:
                return IsIdentifier(operation.RemoveDrawable.DrawableId);
            case ConsoleOperation.PayloadOneofCase.ClearSceneRange:
                return operation.ClearSceneRange.MinimumZIndex <= operation.ClearSceneRange.MaximumZIndex;
            case ConsoleOperation.PayloadOneofCase.ClearScene:
                return true;
            case ConsoleOperation.PayloadOneofCase.UpsertHitRegion:
                return operation.UpsertHitRegion.Region is not null && ValidateHitRegion(operation.UpsertHitRegion.Region);
            case ConsoleOperation.PayloadOneofCase.RemoveHitRegion:
                return IsIdentifier(operation.RemoveHitRegion.RegionId);
            case ConsoleOperation.PayloadOneofCase.ClearHitRegions:
                return true;
            case ConsoleOperation.PayloadOneofCase.SetMediaChannel:
                return operation.SetMediaChannel.Channel is not null && ValidateMedia(operation.SetMediaChannel.Channel);
            case ConsoleOperation.PayloadOneofCase.StopMediaChannel:
                return IsIdentifier(operation.StopMediaChannel.Channel);
            case ConsoleOperation.PayloadOneofCase.StopAllMedia:
                return true;
            default:
                return false;
        }
    }

    private static bool ValidateLine(ConsoleLine line, ref int nodeCount) =>
        IsIdentifier(line.LineId) && line.Alignment != LineAlignment.Unspecified &&
        ValidateNodeList(line.Nodes, ref nodeCount);

    private static bool ValidateNodeList(IEnumerable<ConsoleNode> nodes, ref int nodeCount)
    {
        foreach (ConsoleNode node in nodes)
        {
            if (!ValidateNode(node, ref nodeCount, 1))
                return false;
        }

        return true;
    }

    private static bool ValidateNode(ConsoleNode node, ref int nodeCount, int depth)
    {
        if (++nodeCount > StructuredIpcLimits.MaxNodes || depth > StructuredIpcLimits.MaxHtmlDepth)
            return false;
        return node.KindCase switch
        {
            ConsoleNode.KindOneofCase.Text => IsText(node.Text.Text) && node.Text.Style is not null && ValidateStyle(node.Text.Style),
            ConsoleNode.KindOneofCase.LineBreak => true,
            ConsoleNode.KindOneofCase.Button => IsText(node.Button.Value) && node.Button.Label.Count > 0 &&
                node.Button.Label.Count <= 16 && node.Button.Label.All(label => IsText(label.Text) && label.Style is not null && ValidateStyle(label.Style)) &&
                IsText(node.Button.Tooltip) && node.Button.Generation >= 0,
            ConsoleNode.KindOneofCase.Image => IsAsset(node.Image.AssetId) &&
                (!node.Image.HasSourceRect || ValidateRect(node.Image.SourceRect)) &&
                (!node.Image.HasDestination || ValidateRect(node.Image.Destination)) && IsText(node.Image.AltText),
            ConsoleNode.KindOneofCase.Sprite => IsAsset(node.Sprite.AssetId) && ValidateRect(node.Sprite.SourceRect) &&
                ValidateRect(node.Sprite.Destination) && node.Sprite.Frame >= 0 && node.Sprite.Opacity is >= 0 and <= 1 &&
                IsText(node.Sprite.AltText),
            ConsoleNode.KindOneofCase.Shape => ValidateShape(node.Shape),
            ConsoleNode.KindOneofCase.HtmlIsland => ValidateHtmlIsland(node.HtmlIsland, depth),
            _ => false
        };
    }

    private static bool ValidateShape(ShapeNode shape) =>
        shape.Shape != ShapeKind.Unspecified && ValidateRect(shape.Bounds) &&
        shape.Points.Count <= StructuredIpcLimits.MaxGeometryPoints && shape.Points.All(ValidatePoint) &&
        (!shape.HasFill || ValidateColor(shape.Fill)) && (!shape.HasStroke || ValidateColor(shape.Stroke));

    private static bool ValidateHtmlIsland(HtmlIslandNode island, int depth) =>
        (!island.HasLayout || ValidateRect(island.Layout)) && ValidateHtml(island.Root, depth);

    private static bool ValidateHtml(HtmlNode node, int depth)
    {
        if (depth > StructuredIpcLimits.MaxHtmlDepth)
            return false;
        return node.KindCase switch
        {
            HtmlNode.KindOneofCase.Text => IsText(node.Text),
            HtmlNode.KindOneofCase.BreakNode => true,
            HtmlNode.KindOneofCase.Element => IsTag(node.Element.Tag) && node.Element.Children.Count <= StructuredIpcLimits.MaxHtmlChildren &&
            node.Element.Children.All(child => ValidateHtml(child, depth + 1)) && node.Element.Style is not null && ValidateStyle(node.Element.Style) &&
                (!node.Element.HasAssetId || IsAsset(node.Element.AssetId)) && IsText(node.Element.AltText),
            _ => false
        };
    }

    private static bool ValidateDrawable(CanvasDrawable drawable) => drawable.KindCase switch
    {
        CanvasDrawable.KindOneofCase.Sprite => IsIdentifier(drawable.Sprite.DrawableId) && IsAsset(drawable.Sprite.AssetId) &&
            ValidateRect(drawable.Sprite.SourceRect) && ValidateRect(drawable.Sprite.Bounds) && drawable.Sprite.Opacity is >= 0 and <= 1 &&
            drawable.Sprite.Frame >= 0,
        CanvasDrawable.KindOneofCase.Shape => IsIdentifier(drawable.Shape.DrawableId) && ValidateShapeDrawable(drawable.Shape),
        CanvasDrawable.KindOneofCase.HtmlIsland => IsIdentifier(drawable.HtmlIsland.DrawableId) && ValidateRect(drawable.HtmlIsland.Bounds) &&
            drawable.HtmlIsland.Opacity is >= 0 and <= 1 && ValidateHtml(drawable.HtmlIsland.Root, 1),
        _ => false
    };

    private static bool ValidateShapeDrawable(ShapeDrawable shape) =>
        shape.Shape != ShapeKind.Unspecified && ValidateRect(shape.Bounds) && shape.Opacity is >= 0 and <= 1 &&
        shape.Points.Count <= StructuredIpcLimits.MaxGeometryPoints && shape.Points.All(ValidatePoint) &&
        (!shape.HasFill || ValidateColor(shape.Fill)) && (!shape.HasStroke || ValidateColor(shape.Stroke));

    private static bool ValidateBackground(BackgroundLayer layer) =>
        IsIdentifier(layer.LayerId) && IsAsset(layer.AssetId) &&
        layer.Mode != BackgroundMode.Unspecified && layer.Opacity is >= 0 and <= 1;

    private static bool ValidateHitRegion(HitRegion region) =>
        IsIdentifier(region.RegionId) && ValidateRect(region.Bounds) && IsText(region.InputValue) && region.InputValue.Length > 0 && IsText(region.Tooltip);

    private static bool ValidateMedia(MediaChannelState channel) =>
        IsIdentifier(channel.Channel) && (!channel.HasAssetId || IsAsset(channel.AssetId)) &&
        channel.PlaybackState != MediaPlaybackState.Unspecified && channel.StartPolicy != MediaStartPolicy.Unspecified &&
        channel.Volume is >= 0 and <= 1 && channel.Revision >= 0;

    private static bool ValidatePrompt(ConsolePrompt prompt) =>
        IsIdentifier(prompt.PromptId) && prompt.InputType != InputType.Unspecified &&
        IsText(prompt.PromptText) && (!prompt.HasDefaultValue || IsText(prompt.DefaultValue)) &&
        prompt.AllowedSources != InputSource.Unspecified &&
        (((int)prompt.AllowedSources & ~((int)InputSource.Keyboard | (int)InputSource.Button | (int)InputSource.Pointer | (int)InputSource.System)) == 0) &&
        (!prompt.HasDeadline || prompt.OpenedAtUnixMilliseconds <= prompt.DeadlineUnixMilliseconds) &&
        (!prompt.HasDeadline || prompt.DeadlineUnixMilliseconds > 0) &&
        prompt.TimeoutAction != TimeoutAction.Unspecified && prompt.Constraints is not null && ValidateConstraints(prompt.Constraints);

    private static bool ValidateConstraints(InputConstraints constraints) => constraints.KindCase switch
    {
        InputConstraints.KindOneofCase.Text => constraints.Text.MaxLength >= 0,
        InputConstraints.KindOneofCase.Integer =>
            (!constraints.Integer.HasMinimum || !constraints.Integer.HasMaximum || constraints.Integer.Minimum <= constraints.Integer.Maximum),
        InputConstraints.KindOneofCase.AnyValue => constraints.AnyValue.MaxLength >= 0,
        _ => false
    };

    private static bool ValidateTiming(PromptTiming timing) =>
        timing.OpenedAtUnixMilliseconds >= 0 && timing.ServerNowUnixMilliseconds >= 0 &&
        timing.RemainingMilliseconds >= 0 &&
        (timing.DeadlineUnixMilliseconds == 0
            ? timing.RemainingMilliseconds == 0
            : timing.DeadlineUnixMilliseconds >= timing.OpenedAtUnixMilliseconds);

    private static bool ValidateWindow(WindowMetadata metadata) =>
        IsText(metadata.Title) && metadata.ViewportWidth >= 0 && metadata.ViewportHeight >= 0 &&
        (!metadata.HasDefaultForeground || ValidateColor(metadata.DefaultForeground)) &&
        (!metadata.HasDefaultBackground || ValidateColor(metadata.DefaultBackground)) &&
        metadata.DefaultFont is not null && ValidateStyle(metadata.DefaultFont);

    private static bool ValidateTruncation(TruncationMetadata metadata) =>
        metadata.DroppedNodeCount >= 0 && metadata.DroppedLineCount >= 0 && metadata.DroppedTextLength >= 0;

    private static bool ValidateStyle(TextStyle? style) => style is not null &&
        IsText(style.FontFamily) && style.FontSize > 0 && style.FontSize <= 256 && style.LineHeight >= 0 && style.LineHeight <= 512 &&
        (!style.HasForeground || ValidateColor(style.Foreground)) && (!style.HasBackground || ValidateColor(style.Background));

    private static bool ValidateColor(ProtoConsoleColor? color) => color is not null &&
        color.Red <= 255 && color.Green <= 255 && color.Blue <= 255 && color.Alpha <= 255;

    private static bool ValidateRect(Rect? rect) => rect is not null &&
        rect.Width > 0 && rect.Height > 0 && rect.Width <= 8_192 && rect.Height <= 8_192 &&
        rect.X >= -1_000_000 && rect.X <= 1_000_000 && rect.Y >= -1_000_000 && rect.Y <= 1_000_000;

    private static bool ValidatePoint(Point? point) => point is not null && point.X >= -1_000_000 && point.X <= 1_000_000 && point.Y >= -1_000_000 && point.Y <= 1_000_000;

    private static IpcValidationResult ValidateInputResult(InputResult value) =>
        IsIdentifier(value.PromptId) && IsIdentifier(value.ClientMessageId) && value.Kind != InputResultKind.Unspecified &&
        (!value.HasNormalizedValue || value.NormalizedValue.Length <= StructuredIpcLimits.MaxInputLength)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateRuntimeFailed(RuntimeFailed value) =>
        IsIdentifier(value.StableCode) && IsIdentifier(value.Phase) && IsText(value.SafeMessage) && value.LastOutputSequence >= 0
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateWorkerStopped(WorkerStopped value) => IsIdentifier(value.ReasonCode) && value.LastOutputSequence >= 0
        ? IpcValidationResult.Valid()
        : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateCommandResult(WorkerCommandResult value) =>
        IsIdentifier(value.CommandType) && IsIdentifier(value.ReasonCode)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateRegistrationResult(RegistrationResult value, string envelopeDigest) =>
        IsIdentifier(value.ReasonCode) && value.NegotiatedProtocolVersion == StructuredIpcProtocol.CurrentVersion && IsDigest(value.CapabilitySetDigest) &&
        IsText(value.RuntimeIntegrationVersion) && IsText(value.UpstreamCommit) && IsIdentifier(value.ControlPlaneInstanceId) &&
        string.Equals(value.CapabilitySetDigest, envelopeDigest, StringComparison.Ordinal)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateStart(StartRuntime value) =>
        IsIdentifier(value.ExpectedSessionId) && IsIdentifier(value.ExpectedWorkerId) && value.ExpectedWorkerEpoch > 0 &&
        IsIdentifier(value.ExpectedCompatibilityProfile) && value.DeadlineUnixMilliseconds > 0 && IsDigest(value.ExpectedCapabilitySetDigest)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateSubmitInput(SubmitInput value) =>
        IsIdentifier(value.PromptId) && IsIdentifier(value.ClientMessageId) && value.Value.Length <= StructuredIpcLimits.MaxInputLength &&
        value.Source != InputSource.Unspecified && value.DeadlineUnixMilliseconds > 0 &&
        (value.PayloadCase == SubmitInput.PayloadOneofCase.None ||
         value.PayloadCase == SubmitInput.PayloadOneofCase.Pointer && value.Pointer.Position is not null && ValidatePoint(value.Pointer.Position) && value.Pointer.Button is >= 0 and <= 16 ||
         value.PayloadCase == SubmitInput.PayloadOneofCase.Key && value.Key.KeyCode is >= 0 and <= 255)
            ? IpcValidationResult.Valid()
            : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static IpcValidationResult ValidateStop(StopWorker value) => value.DeadlineUnixMilliseconds > 0 && IsIdentifier(value.ReasonCode)
        ? IpcValidationResult.Valid()
        : IpcValidationResult.Invalid(IpcReasonCodes.InvalidEnvelope);

    private static bool IsText(string? value) => value is not null && value.Length <= StructuredIpcLimits.MaxStringLength && !value.Any(char.IsControl);

    private static bool IsAsset(string value) => IsIdentifier(value) && !value.Contains("..", StringComparison.Ordinal);

    private static bool IsTag(string value) => value is "span" or "div" or "p" or "b" or "strong" or "i" or "em" or "u" or "s" or "strike" or "img";

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= StructuredIpcLimits.MaxIdentifierLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~');

    private static bool IsToken(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));
}

using CloudEmuera.Ipc.V2;
using V2 = CloudEmuera.Ipc.V2;
using RuntimeConsoleColor = CloudEmuera.RuntimeAdapter.ConsoleColor;
using RuntimeConsoleInputType = CloudEmuera.RuntimeAdapter.ConsoleInputType;
using RuntimePromptTimeoutBehavior = CloudEmuera.RuntimeAdapter.ConsolePromptTimeoutBehavior;
using RuntimePromptCloseReason = CloudEmuera.RuntimeAdapter.ConsolePromptCloseReason;

namespace CloudEmuera.Worker;

/// <summary>
/// Explicit mapping between the bounded RuntimeAdapter console model and the
/// versioned IPC model. No HTML, host path or runtime object crosses this
/// boundary.
/// </summary>
public static class ConsoleWireMapper
{
    public static ConsoleOperation ToProto(CloudEmuera.RuntimeAdapter.ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation switch
        {
            CloudEmuera.RuntimeAdapter.AppendNodesOperation append => new ConsoleOperation
            {
                AppendNodes = new AppendNodes()
            }.WithAppendNodes(append.Nodes),
            CloudEmuera.RuntimeAdapter.ClearConsoleOperation => new ConsoleOperation
            {
                ClearConsole = new ClearConsole()
            },
            CloudEmuera.RuntimeAdapter.OpenPromptOperation open => new ConsoleOperation
            {
                OpenPrompt = new OpenPrompt { Prompt = ToProto(open.Prompt) }
            },
            CloudEmuera.RuntimeAdapter.ClosePromptOperation close => new ConsoleOperation
            {
                ClosePrompt = new ClosePrompt
                {
                    PromptId = close.PromptId,
                    Reason = ToProto(close.Reason)
                }
            },
            _ => throw new InvalidOperationException("The console operation is outside the IPC contract.")
        };
    }

    public static CloudEmuera.RuntimeAdapter.ConsoleOperation FromProto(ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.PayloadCase switch
        {
            ConsoleOperation.PayloadOneofCase.AppendNodes =>
                new CloudEmuera.RuntimeAdapter.AppendNodesOperation(operation.AppendNodes.Nodes.Select(FromProto)),
            ConsoleOperation.PayloadOneofCase.ClearConsole => new CloudEmuera.RuntimeAdapter.ClearConsoleOperation(),
            ConsoleOperation.PayloadOneofCase.OpenPrompt =>
                new CloudEmuera.RuntimeAdapter.OpenPromptOperation(FromProto(operation.OpenPrompt.Prompt)),
            ConsoleOperation.PayloadOneofCase.ClosePrompt =>
                new CloudEmuera.RuntimeAdapter.ClosePromptOperation(
                    operation.ClosePrompt.PromptId,
                    FromProto(operation.ClosePrompt.Reason)),
            _ => throw new InvalidDataException("The IPC console operation has no known payload.")
        };
    }

    public static ConsoleNode ToProto(CloudEmuera.RuntimeAdapter.ConsoleNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node switch
        {
            CloudEmuera.RuntimeAdapter.TextNode text => new ConsoleNode
            {
                Text = new TextNode
                {
                    Text = text.Text,
                    Style = ToProto(text.Style)
                }
            },
            CloudEmuera.RuntimeAdapter.LineBreakNode => new ConsoleNode
            {
                LineBreak = new LineBreakNode()
            },
            CloudEmuera.RuntimeAdapter.ButtonNode button => new ConsoleNode
            {
                Button = new ButtonNode
                {
                    Value = button.Value,
                    Tooltip = button.Tooltip ?? string.Empty,
                    Enabled = button.Enabled
                }.WithLabels(button.Children)
            },
            CloudEmuera.RuntimeAdapter.ImageNode image => new ConsoleNode
            {
                Image = new ImageNode
                {
                    AssetId = image.AssetId.Value,
                    Width = image.Width ?? 0,
                    Height = image.Height ?? 0,
                    AltText = image.AltText ?? string.Empty,
                    HasWidth = image.Width is not null,
                    HasHeight = image.Height is not null
                }
            },
            _ => throw new InvalidOperationException("The console node is outside the IPC contract.")
        };
    }

    public static CloudEmuera.RuntimeAdapter.ConsoleNode FromProto(ConsoleNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.KindCase switch
        {
            ConsoleNode.KindOneofCase.Text => new CloudEmuera.RuntimeAdapter.TextNode(
                node.Text.Text,
                FromProto(node.Text.Style)),
            ConsoleNode.KindOneofCase.LineBreak => CloudEmuera.RuntimeAdapter.LineBreakNode.Instance,
            ConsoleNode.KindOneofCase.Button => new CloudEmuera.RuntimeAdapter.ButtonNode(
                node.Button.Label.Select(FromProto),
                node.Button.Value,
                string.IsNullOrEmpty(node.Button.Tooltip) ? null : node.Button.Tooltip,
                node.Button.Enabled),
            ConsoleNode.KindOneofCase.Image => new CloudEmuera.RuntimeAdapter.ImageNode(
                node.Image.AssetId,
                node.Image.HasWidth ? node.Image.Width : null,
                node.Image.HasHeight ? node.Image.Height : null,
                string.IsNullOrEmpty(node.Image.AltText) ? null : node.Image.AltText),
            _ => throw new InvalidDataException("The IPC console node has no known payload.")
        };
    }

    public static CloudEmuera.RuntimeAdapter.ConsolePrompt FromProto(V2.ConsolePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        RuntimeConsoleInputType inputType = prompt.InputType switch
        {
            ConsoleInputType.ConsoleInputText => RuntimeConsoleInputType.Text,
            ConsoleInputType.ConsoleInputInteger => RuntimeConsoleInputType.Integer,
            _ => throw new InvalidDataException("The IPC prompt input type is unspecified.")
        };

        CloudEmuera.RuntimeAdapter.ConsoleInputConstraints constraints = prompt.Constraints.KindCase switch
        {
            InputConstraints.KindOneofCase.Text => new CloudEmuera.RuntimeAdapter.TextInputConstraints(
                prompt.Constraints.Text.HasMaxLength ? prompt.Constraints.Text.MaxLength : null,
                prompt.Constraints.Text.AllowControlCharacters),
            InputConstraints.KindOneofCase.Integer => new CloudEmuera.RuntimeAdapter.IntegerInputConstraints(
                prompt.Constraints.Integer.HasMinimum ? prompt.Constraints.Integer.Minimum : null,
                prompt.Constraints.Integer.HasMaximum ? prompt.Constraints.Integer.Maximum : null,
                prompt.Constraints.Integer.AllowSign),
            _ => throw new InvalidDataException("The IPC prompt constraints are unspecified.")
        };

        TimeSpan? timeout = prompt.HasTimeout
            ? prompt.TimeoutMilliseconds == -1
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(prompt.TimeoutMilliseconds)
            : null;
        RuntimePromptTimeoutBehavior timeoutBehavior = prompt.TimeoutBehavior switch
        {
            PromptTimeoutBehavior.PromptTimeoutCancel => RuntimePromptTimeoutBehavior.Cancel,
            PromptTimeoutBehavior.PromptTimeoutDefaultValue => RuntimePromptTimeoutBehavior.ReturnDefaultValue,
            _ => throw new InvalidDataException("The IPC prompt timeout behavior is unspecified.")
        };

        return new CloudEmuera.RuntimeAdapter.ConsolePrompt(
            prompt.PromptId,
            inputType,
            string.IsNullOrEmpty(prompt.PromptText) ? null : prompt.PromptText,
            prompt.HasDefaultValue ? prompt.DefaultValue : null,
            constraints,
            timeout,
            timeoutBehavior);
    }

    public static V2.ConsolePrompt ToProto(CloudEmuera.RuntimeAdapter.ConsolePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var result = new V2.ConsolePrompt
        {
            PromptId = prompt.PromptId,
            InputType = prompt.InputType switch
            {
                RuntimeConsoleInputType.Text => ConsoleInputType.ConsoleInputText,
                RuntimeConsoleInputType.Integer => ConsoleInputType.ConsoleInputInteger,
                _ => throw new InvalidDataException("The runtime prompt input type is unspecified.")
            },
            PromptText = prompt.PromptText ?? string.Empty,
            DefaultValue = prompt.DefaultValue ?? string.Empty,
            HasDefaultValue = prompt.DefaultValue is not null,
            Constraints = new InputConstraints(),
            TimeoutBehavior = prompt.TimeoutBehavior switch
            {
                RuntimePromptTimeoutBehavior.Cancel => PromptTimeoutBehavior.PromptTimeoutCancel,
                RuntimePromptTimeoutBehavior.ReturnDefaultValue => PromptTimeoutBehavior.PromptTimeoutDefaultValue,
                _ => throw new InvalidDataException("The runtime prompt timeout behavior is unspecified.")
            }
        };

        switch (prompt.Constraints)
        {
            case CloudEmuera.RuntimeAdapter.TextInputConstraints text:
                result.Constraints.Text = new TextInputConstraints
                {
                    HasMaxLength = text.MaxLength is not null,
                    MaxLength = text.MaxLength ?? 0,
                    AllowControlCharacters = text.AllowControlCharacters
                };
                break;
            case CloudEmuera.RuntimeAdapter.IntegerInputConstraints integer:
                result.Constraints.Integer = new IntegerInputConstraints
                {
                    HasMinimum = integer.Minimum is not null,
                    Minimum = integer.Minimum ?? 0,
                    HasMaximum = integer.Maximum is not null,
                    Maximum = integer.Maximum ?? 0,
                    AllowSign = integer.AllowSign
                };
                break;
            default:
                throw new InvalidDataException("The runtime prompt constraint type is unspecified.");
        }

        if (prompt.Timeout is TimeSpan value)
        {
            result.HasTimeout = true;
            result.TimeoutMilliseconds = value == Timeout.InfiniteTimeSpan ? -1 : checked((long)value.TotalMilliseconds);
        }

        return result;
    }

    public static InputResultKind ToProto(CloudEmuera.RuntimeAdapter.ConsoleInputResultKind kind) => kind switch
    {
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.Accepted => InputResultKind.InputResultAccepted,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.Duplicate => InputResultKind.InputResultDuplicate,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.StalePrompt => InputResultKind.InputResultStalePrompt,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.NoActivePrompt => InputResultKind.InputResultNoActivePrompt,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.InvalidCommand => InputResultKind.InputResultInvalidCommand,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.InvalidFormat => InputResultKind.InputResultInvalidFormat,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.MessageConflict => InputResultKind.InputResultConflict,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.Cancelled => InputResultKind.InputResultCancelled,
        CloudEmuera.RuntimeAdapter.ConsoleInputResultKind.TimedOut => InputResultKind.InputResultTimedOut,
        _ => InputResultKind.InputResultUnspecified
    };

    private static TextStyle ToProto(CloudEmuera.RuntimeAdapter.ConsoleTextStyle style)
    {
        var result = new TextStyle { Decorations = (uint)style.Decorations };
        if (style.Foreground is RuntimeConsoleColor foreground)
            result.Foreground = ToProto(foreground);
        if (style.Background is RuntimeConsoleColor background)
            result.Background = ToProto(background);
        return result;
    }

    private static CloudEmuera.RuntimeAdapter.ConsoleTextStyle FromProto(TextStyle style)
    {
        RuntimeConsoleColor? foreground = style.Foreground is null ? null : FromProto(style.Foreground);
        RuntimeConsoleColor? background = style.Background is null ? null : FromProto(style.Background);
        return new CloudEmuera.RuntimeAdapter.ConsoleTextStyle(
            foreground,
            background,
            (CloudEmuera.RuntimeAdapter.ConsoleFontStyle)style.Decorations);
    }

    private static V2.ConsoleColor ToProto(RuntimeConsoleColor color) => new()
    {
        Red = color.Red,
        Green = color.Green,
        Blue = color.Blue,
        Alpha = color.Alpha
    };

    private static RuntimeConsoleColor FromProto(V2.ConsoleColor color) => new(
        checked((byte)color.Red),
        checked((byte)color.Green),
        checked((byte)color.Blue),
        checked((byte)color.Alpha));

    private static PromptCloseReason ToProto(RuntimePromptCloseReason reason) => reason switch
    {
        RuntimePromptCloseReason.Completed => PromptCloseReason.PromptCloseCompleted,
        RuntimePromptCloseReason.InputAccepted => PromptCloseReason.PromptCloseInputAccepted,
        RuntimePromptCloseReason.Cancelled => PromptCloseReason.PromptCloseCancelled,
        RuntimePromptCloseReason.TimedOut => PromptCloseReason.PromptCloseTimedOut,
        RuntimePromptCloseReason.Explicit => PromptCloseReason.PromptCloseExplicit,
        _ => PromptCloseReason.PromptCloseUnspecified
    };

    private static RuntimePromptCloseReason FromProto(PromptCloseReason reason) => reason switch
    {
        PromptCloseReason.PromptCloseCompleted => RuntimePromptCloseReason.Completed,
        PromptCloseReason.PromptCloseInputAccepted => RuntimePromptCloseReason.InputAccepted,
        PromptCloseReason.PromptCloseCancelled => RuntimePromptCloseReason.Cancelled,
        PromptCloseReason.PromptCloseTimedOut => RuntimePromptCloseReason.TimedOut,
        PromptCloseReason.PromptCloseExplicit => RuntimePromptCloseReason.Explicit,
        _ => throw new InvalidDataException("The IPC prompt close reason is unspecified.")
    };

    private static ConsoleOperation WithAppendNodes(
        this ConsoleOperation operation,
        IEnumerable<CloudEmuera.RuntimeAdapter.ConsoleNode> nodes)
    {
        operation.AppendNodes.Nodes.AddRange(nodes.Select(ToProto));
        return operation;
    }

    private static ButtonNode WithLabels(
        this ButtonNode button,
        IEnumerable<CloudEmuera.RuntimeAdapter.ConsoleNode> labels)
    {
        button.Label.AddRange(labels.Select(ToProto));
        return button;
    }
}

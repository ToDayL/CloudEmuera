namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Typed input returned to the fixed upstream runtime. Value is kept in its
/// original textual form so adapters can apply the upstream's exact parsing
/// rules without losing what the client submitted.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1720", Justification = "Pointer payload is part of the stable structured input contract.")]
public sealed record GameConsoleInput
{
    public GameConsoleInput(
        string promptId,
        ConsoleInputType inputType,
        string value,
        bool isDefaultValue = false,
        bool skipMessage = false,
        ConsolePointerPayload? pointer = null)
    {
        ConsoleContractValidation.ValidateIdentifier(
            promptId,
            nameof(promptId),
            ConsoleContractLimits.Default.MaxPromptIdLength);
        ArgumentNullException.ThrowIfNull(value);
        if (inputType is not ConsoleInputType.EnterKey and not ConsoleInputType.AnyKey and not ConsoleInputType.Integer and
            not ConsoleInputType.Text and not ConsoleInputType.AnyValue and not ConsoleInputType.IntegerButton and
            not ConsoleInputType.TextButton and not ConsoleInputType.PrimitivePointerKey and not ConsoleInputType.WaitOnly)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Unknown input type.", nameof(inputType));
        }

        PromptId = promptId;
        InputType = inputType;
        Value = value;
        IsDefaultValue = isDefaultValue;
        SkipMessage = skipMessage;
        Pointer = pointer;
    }

    public string PromptId { get; }

    public ConsoleInputType InputType { get; }

    public string Value { get; }

    public string RawValue => Value;

    public bool IsDefaultValue { get; }

    /// <summary>
    /// Whether the accepted input requested desktop-compatible message
    /// skipping (the browser encodes this as a pressed right pointer button).
    /// </summary>
    public bool SkipMessage { get; }

    /// <summary>
    /// The physical pointer event that produced this accepted input, when
    /// one exists. Headless Emuera uses it to preserve INPUTS,1's upstream
    /// left/middle/right mouse result semantics.
    /// </summary>
    public ConsolePointerPayload? Pointer { get; }
}

/// <summary>
/// Runtime console boundary. It has no dependency on a window, control, font,
/// color or desktop event loop.
/// </summary>
public interface IGameConsole
{
    void Emit(ConsoleOperation operation);

    GameConsoleInput Read(ConsolePrompt prompt, CancellationToken cancellationToken = default);
}

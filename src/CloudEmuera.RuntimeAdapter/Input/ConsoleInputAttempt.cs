namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// A client input intention submitted to whichever prompt owns the input slot
/// when the Worker receives it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1720", Justification = "Pointer payload is part of the stable structured input contract.")]
public sealed class ConsoleInputAttempt
{
    public ConsoleInputAttempt(string clientMessageId, string value)
        : this(clientMessageId, value, ConsoleInputSource.Keyboard)
    {
    }

    public ConsoleInputAttempt(
        string clientMessageId,
        string value,
        ConsoleInputSource source,
        ConsolePointerPayload? pointer = null,
        ConsoleKeyPayload? key = null)
    {
        ConsoleContractValidation.ValidateIdentifier(
            clientMessageId,
            nameof(clientMessageId),
            ConsoleContractLimits.Default.MaxClientMessageIdLength);
        ConsoleContractValidation.ValidateText(
            value,
            nameof(value),
            ConsoleContractLimits.Default.MaxInputValueLength,
            ConsoleContractViolationReason.InputValueTooLong,
            allowControlCharacters: true);

        if (source is < ConsoleInputSource.None or > ConsoleInputSource.All || source == ConsoleInputSource.None)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Input source is invalid.");
        if (pointer is not null && !source.HasFlag(ConsoleInputSource.Pointer))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Pointer payload requires the pointer source.");
        if (key is not null && !source.HasFlag(ConsoleInputSource.Keyboard))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Key payload requires the keyboard source.");

        ClientMessageId = clientMessageId;
        Value = value;
        Source = source;
        Pointer = pointer;
        Key = key;
    }

    public string ClientMessageId { get; }

    public string Value { get; }

    public ConsoleInputSource Source { get; }

    public ConsolePointerPayload? Pointer { get; }

    public ConsoleKeyPayload? Key { get; }

    /// <summary>
    /// A pressed right pointer button is the cross-platform message-skip
    /// gesture. The current prompt still decides whether the gesture is
    /// applicable; this property only preserves the input intent.
    /// </summary>
    public bool IsMessageSkip =>
        Source.HasFlag(ConsoleInputSource.Pointer) &&
        Pointer is { Button: 2, Pressed: true };

    /// <summary>
    /// Stable length-delimited encoding for a single Worker epoch receipt.
    /// Internal prompt identity intentionally never participates in it.
    /// </summary>
    public string Fingerprint =>
        $"v:{Value.Length}:{Value}|s:{(int)Source}|p:{FormatPointer(Pointer)}|k:{FormatKey(Key)}";

    private static string FormatPointer(ConsolePointerPayload? pointer) => pointer is null
        ? "-"
        : $"{pointer.Position.X}:{pointer.Position.Y}:{pointer.Button}:{pointer.Pressed}";

    private static string FormatKey(ConsoleKeyPayload? key) => key is null
        ? "-"
        : $"{key.KeyCode}:{key.Control}:{key.Alt}:{key.Shift}";
}

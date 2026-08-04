namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Logical manifest key for a runtime image. It is deliberately not a path or
/// URI and therefore cannot request a host file or a browser resource.
/// </summary>
public readonly record struct ConsoleAssetId
{
    public ConsoleAssetId(string value)
    {
        ConsoleContractValidation.ValidateIdentifier(
            value,
            nameof(value),
            ConsoleContractLimits.Default.MaxAssetIdLength,
            ConsoleContractViolationReason.InvalidAssetId);

        if (value.Contains("..", StringComparison.Ordinal))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidAssetId,
                "An asset id cannot contain a parent traversal sequence.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static ConsoleAssetId Parse(string value) => new(value);

    public override string ToString() => Value ?? string.Empty;

    internal void Validate(ConsoleContractLimits limits)
    {
        if (string.IsNullOrEmpty(Value))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidAssetId,
                "An asset id is required.");
        }

        ConsoleContractValidation.ValidateIdentifier(Value, nameof(Value), limits.MaxAssetIdLength, ConsoleContractViolationReason.InvalidAssetId);
        if (Value.Contains("..", StringComparison.Ordinal))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidAssetId,
                "An asset id cannot contain a parent traversal sequence.");
        }
    }
}

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Bounded in-memory state and replay configuration for one worker console.
/// </summary>
public sealed record ConsoleHistoryOptions
{
    public static ConsoleHistoryOptions Default => new();

    public int MaxVisibleNodes { get; init; } = 4_096;

    public int MaxVisibleTextLength { get; init; } = 262_144;

    public int MaxDeltaCount { get; init; } = 1_024;

    public long MaxEstimatedBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxInputReceiptCount { get; init; } = 2_048;

    public ConsoleContractLimits ContractLimits { get; init; } = ConsoleContractLimits.Default;

    public void Validate()
    {
        if (MaxVisibleNodes <= 0 || MaxVisibleTextLength <= 0 || MaxDeltaCount <= 0 || MaxEstimatedBytes <= 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.HistoryLimitInvalid,
                "Console history limits must be positive.");
        }

        if (MaxInputReceiptCount <= 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InputReceiptLimitInvalid,
                "The input receipt limit must be positive.");
        }

        if (ContractLimits is null)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.NullValue,
                "Console contract limits are required.");
        }

        ContractLimits.Validate();
    }
}

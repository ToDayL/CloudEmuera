namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Limits that form part of the in-process console contract.  They are kept in
/// one value so callers can use deliberately small limits in contract tests.
/// </summary>
public sealed record ConsoleContractLimits
{
    public static ConsoleContractLimits Default => new();

    public int MaxTextLength { get; init; } = 16_384;

    public int MaxTooltipLength { get; init; } = 4_096;

    public int MaxButtonValueLength { get; init; } = 256;

    public int MaxAltTextLength { get; init; } = 1_024;

    public int MaxAssetIdLength { get; init; } = 128;

    public int MaxBatchNodeCount { get; init; } = 512;

    public int MaxButtonLabelNodeCount { get; init; } = 16;

    public int MaxNodeDepth { get; init; } = 8;

    public int MaxImageWidth { get; init; } = 8_192;

    public int MaxImageHeight { get; init; } = 8_192;

    public int MaxHtmlInputLength { get; init; } = 32_768;

    public int MaxHtmlTagCount { get; init; } = 256;

    public int MaxHtmlNestingDepth { get; init; } = 16;

    public int MaxPromptIdLength { get; init; } = 128;

    public int MaxClientMessageIdLength { get; init; } = 128;

    public int MaxPromptTextLength { get; init; } = 4_096;

    public int MaxPromptDefaultValueLength { get; init; } = 4_096;

    public int MaxInputValueLength { get; init; } = 16_384;

    public void Validate()
    {
        ValidatePositive(MaxTextLength, nameof(MaxTextLength));
        ValidatePositive(MaxTooltipLength, nameof(MaxTooltipLength));
        ValidatePositive(MaxButtonValueLength, nameof(MaxButtonValueLength));
        ValidatePositive(MaxAltTextLength, nameof(MaxAltTextLength));
        ValidatePositive(MaxAssetIdLength, nameof(MaxAssetIdLength));
        ValidatePositive(MaxBatchNodeCount, nameof(MaxBatchNodeCount));
        ValidatePositive(MaxButtonLabelNodeCount, nameof(MaxButtonLabelNodeCount));
        ValidatePositive(MaxNodeDepth, nameof(MaxNodeDepth));
        ValidatePositive(MaxImageWidth, nameof(MaxImageWidth));
        ValidatePositive(MaxImageHeight, nameof(MaxImageHeight));
        ValidatePositive(MaxHtmlInputLength, nameof(MaxHtmlInputLength));
        ValidatePositive(MaxHtmlTagCount, nameof(MaxHtmlTagCount));
        ValidatePositive(MaxHtmlNestingDepth, nameof(MaxHtmlNestingDepth));
        ValidatePositive(MaxPromptIdLength, nameof(MaxPromptIdLength));
        ValidatePositive(MaxClientMessageIdLength, nameof(MaxClientMessageIdLength));
        ValidatePositive(MaxPromptTextLength, nameof(MaxPromptTextLength));
        ValidatePositive(MaxPromptDefaultValueLength, nameof(MaxPromptDefaultValueLength));
        ValidatePositive(MaxInputValueLength, nameof(MaxInputValueLength));
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Console contract limits must be positive.");
        }
    }
}

public enum ConsoleContractViolationReason
{
    NullValue,
    EmptyValue,
    InvalidIdentifier,
    TextTooLong,
    TooltipTooLong,
    ButtonValueTooLong,
    AltTextTooLong,
    AssetIdTooLong,
    InvalidAssetId,
    InvalidColor,
    UnknownFontStyle,
    EmptyBatch,
    BatchTooLarge,
    TooManyButtonLabelNodes,
    InvalidNodeType,
    NodeTooDeep,
    InvalidImageDimension,
    ImageTooLarge,
    PromptAlreadyActive,
    PromptIdMismatch,
    PromptAlreadyCompleted,
    PromptTextTooLong,
    PromptDefaultValueTooLong,
    InputValueTooLong,
    InvalidPrompt,
    InvalidInputConstraint,
    InvalidCursor,
    SequenceExhausted,
    NodeExceedsHistoryBudget,
    NodeExceedsVisibleTextBudget,
    HtmlInputTooLong,
    HtmlTagLimitExceeded,
    HtmlNestingLimitExceeded,
    MalformedHtml,
    UnsupportedHtml,
    HistoryLimitInvalid,
    InputReceiptLimitInvalid,
    MessageConflict
}

/// <summary>
/// Stable, non-UI error for data rejected by the structured console contract.
/// The reason is safe to expose to callers; exception text is diagnostic only.
/// </summary>
public sealed class ConsoleContractException : ArgumentException
{
    public ConsoleContractException(ConsoleContractViolationReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public ConsoleContractException(
        ConsoleContractViolationReason reason,
        string message,
        string parameterName)
        : base(message, parameterName)
    {
        Reason = reason;
    }

    public ConsoleContractViolationReason Reason { get; }

    public string ReasonCode => Reason.ToString();
}

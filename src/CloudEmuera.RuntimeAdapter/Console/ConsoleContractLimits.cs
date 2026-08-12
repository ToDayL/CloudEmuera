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

    public int MaxLineIdLength { get; init; } = 128;

    public int MaxLayerIdLength { get; init; } = 128;

    public int MaxDrawableIdLength { get; init; } = 128;

    public int MaxHitRegionIdLength { get; init; } = 128;

    public int MaxMediaChannelLength { get; init; } = 64;

    public int MaxWindowTitleLength { get; init; } = 512;

    public int MaxFontFamilyLength { get; init; } = 128;

    public int MaxNodesPerLine { get; init; } = 512;

    public int MaxScrollbackLines { get; init; } = 4_096;

    public int MaxScrollbackNodes { get; init; } = 8_192;

    public int MaxScrollbackTextLength { get; init; } = 524_288;

    public int MaxBackgroundLayers { get; init; } = 64;

    public int MaxDrawables { get; init; } = 2_048;

    public int MaxHitRegions { get; init; } = 2_048;

    public int MaxMediaChannels { get; init; } = 32;

    public int MaxTransactionOperations { get; init; } = 128;

    public int MaxGeometryPoints { get; init; } = 256;

    public int MaxHtmlTagNameLength { get; init; } = 32;

    public int MaxHtmlChildren { get; init; } = 256;

    public int MaxSpriteFrames { get; init; } = 4_096;

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
        ValidatePositive(MaxLineIdLength, nameof(MaxLineIdLength));
        ValidatePositive(MaxLayerIdLength, nameof(MaxLayerIdLength));
        ValidatePositive(MaxDrawableIdLength, nameof(MaxDrawableIdLength));
        ValidatePositive(MaxHitRegionIdLength, nameof(MaxHitRegionIdLength));
        ValidatePositive(MaxMediaChannelLength, nameof(MaxMediaChannelLength));
        ValidatePositive(MaxWindowTitleLength, nameof(MaxWindowTitleLength));
        ValidatePositive(MaxFontFamilyLength, nameof(MaxFontFamilyLength));
        ValidatePositive(MaxNodesPerLine, nameof(MaxNodesPerLine));
        ValidatePositive(MaxScrollbackLines, nameof(MaxScrollbackLines));
        ValidatePositive(MaxScrollbackNodes, nameof(MaxScrollbackNodes));
        ValidatePositive(MaxScrollbackTextLength, nameof(MaxScrollbackTextLength));
        ValidatePositive(MaxBackgroundLayers, nameof(MaxBackgroundLayers));
        ValidatePositive(MaxDrawables, nameof(MaxDrawables));
        ValidatePositive(MaxHitRegions, nameof(MaxHitRegions));
        ValidatePositive(MaxMediaChannels, nameof(MaxMediaChannels));
        ValidatePositive(MaxTransactionOperations, nameof(MaxTransactionOperations));
        ValidatePositive(MaxGeometryPoints, nameof(MaxGeometryPoints));
        ValidatePositive(MaxHtmlTagNameLength, nameof(MaxHtmlTagNameLength));
        ValidatePositive(MaxHtmlChildren, nameof(MaxHtmlChildren));
        ValidatePositive(MaxSpriteFrames, nameof(MaxSpriteFrames));
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
    MessageConflict,
    InvalidGeometry,
    InvalidFont,
    InvalidAlignment,
    InvalidBackgroundMode,
    InvalidOpacity,
    InvalidSpriteFrame,
    InvalidShape,
    GeometryTooLarge,
    LineTooLarge,
    SceneTooLarge,
    DuplicateIdentifier,
    InvalidMediaRevision,
    InvalidMediaState,
    InvalidMediaStartPolicy,
    MediaTooLarge,
    WindowMetadataTooLong,
    InvalidViewport,
    HtmlNodeLimitExceeded
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

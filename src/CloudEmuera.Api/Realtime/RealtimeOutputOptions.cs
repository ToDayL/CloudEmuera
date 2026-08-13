using CloudEmuera.Ipc;

namespace CloudEmuera.Api.Realtime;

public sealed record RealtimeOutputOptions
{
    public const long DefaultSnapshotMaxBytes = 12 * 1024 * 1024;
    public const long DefaultBatchTargetBytes = 256 * 1024;
    public const int DefaultBatchMaxTransactions = 64;
    public const int DefaultQueueSoftMessages = 32;
    public const int DefaultQueueHardMessages = 64;
    public const long DefaultQueueSoftBytes = 1 * 1024 * 1024;
    public const long DefaultQueueHardBytes = 2 * 1024 * 1024;
    public const int AbsoluteMaxQueueMessages = 4096;

    public static RealtimeOutputOptions Default => new();

    public long SnapshotMaxBytes { get; init; } = DefaultSnapshotMaxBytes;

    public long BatchTargetBytes { get; init; } = DefaultBatchTargetBytes;

    public int BatchMaxTransactions { get; init; } = DefaultBatchMaxTransactions;

    public TimeSpan BatchMaxDelay { get; init; } = TimeSpan.FromMilliseconds(16);

    public long ConnectionQueueSoftBytes { get; init; } = DefaultQueueSoftBytes;

    public long ConnectionQueueHardBytes { get; init; } = DefaultQueueHardBytes;

    public int ConnectionQueueSoftMessages { get; init; } = DefaultQueueSoftMessages;

    public int ConnectionQueueHardMessages { get; init; } = DefaultQueueHardMessages;

    public int MaxSnapshotResyncAttempts { get; init; } = 3;

    public TimeSpan SnapshotResyncWindow { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        ValidatePositive(SnapshotMaxBytes, nameof(SnapshotMaxBytes));
        ValidatePositive(BatchTargetBytes, nameof(BatchTargetBytes));
        ValidatePositive(BatchMaxTransactions, nameof(BatchMaxTransactions));
        ValidatePositive(BatchMaxDelay, nameof(BatchMaxDelay));
        ValidatePositive(ConnectionQueueSoftBytes, nameof(ConnectionQueueSoftBytes));
        ValidatePositive(ConnectionQueueHardBytes, nameof(ConnectionQueueHardBytes));
        ValidatePositive(ConnectionQueueSoftMessages, nameof(ConnectionQueueSoftMessages));
        ValidatePositive(ConnectionQueueHardMessages, nameof(ConnectionQueueHardMessages));
        ValidatePositive(MaxSnapshotResyncAttempts, nameof(MaxSnapshotResyncAttempts));
        ValidatePositive(SnapshotResyncWindow, nameof(SnapshotResyncWindow));

        if (SnapshotMaxBytes > StructuredIpcLimits.MaxEnvelopeBytes)
            throw new ArgumentOutOfRangeException(nameof(SnapshotMaxBytes), "The JSON snapshot cannot exceed the Worker envelope limit.");
        if (BatchTargetBytes > SnapshotMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(BatchTargetBytes), "The transaction batch target cannot exceed the snapshot limit.");
        if (BatchMaxTransactions > 512)
            throw new ArgumentOutOfRangeException(nameof(BatchMaxTransactions), "The transaction batch count exceeds the protocol limit.");
        if (ConnectionQueueSoftBytes >= ConnectionQueueHardBytes || ConnectionQueueSoftMessages >= ConnectionQueueHardMessages)
            throw new ArgumentException("Realtime queue soft limits must be lower than hard limits.");
        if (ConnectionQueueHardMessages > AbsoluteMaxQueueMessages)
            throw new ArgumentOutOfRangeException(nameof(ConnectionQueueHardMessages), "The realtime queue message limit exceeds the deployment safety bound.");
        if (ConnectionQueueHardBytes > SnapshotMaxBytes || BatchTargetBytes > ConnectionQueueHardBytes)
            throw new ArgumentException("Realtime queue and batch limits exceed the snapshot budget.");
        if (BatchMaxDelay > TimeSpan.FromSeconds(1) || SnapshotResyncWindow > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(BatchMaxDelay), "Realtime timing limits exceed the deployment safety bound.");
        if (MaxSnapshotResyncAttempts > 32)
            throw new ArgumentOutOfRangeException(nameof(MaxSnapshotResyncAttempts), "The resync attempt limit exceeds the deployment safety bound.");
    }

    private static void ValidatePositive(long value, string parameterName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Realtime output limits must be positive.");
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Realtime output limits must be positive.");
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName, value, "Realtime output durations must be positive.");
    }
}

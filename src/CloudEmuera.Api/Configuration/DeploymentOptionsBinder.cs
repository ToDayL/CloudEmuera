using System.Globalization;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Workers;
using CloudEmuera.Contracts.Realtime;
using CloudEmuera.Infrastructure.Assets;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Ipc;

namespace CloudEmuera.Api.Configuration;

internal static class DeploymentOptionsBinder
{
    public static InstanceCapacityOptions BindCapacity(IConfiguration configuration, out bool usedLegacyArchiveKey, out bool usedLegacyFreeSpaceKey)
    {
        const string prefix = "CloudEmuera:Capacity:";
        string? archiveValue = configuration[$"{prefix}MaxArchiveBytes"];
        usedLegacyArchiveKey = string.IsNullOrWhiteSpace(archiveValue) && configuration[$"{prefix}MaxGamePackageBytes"] is not null;
        if (string.IsNullOrWhiteSpace(archiveValue))
            archiveValue = configuration[$"{prefix}MaxGamePackageBytes"];

        string? freeSpaceValue = configuration[$"{prefix}MinDataRootFreeBytes"];
        usedLegacyFreeSpaceKey = string.IsNullOrWhiteSpace(freeSpaceValue) && configuration["CloudEmuera:MinDataRootFreeBytes"] is not null;
        if (string.IsNullOrWhiteSpace(freeSpaceValue))
            freeSpaceValue = configuration["CloudEmuera:MinDataRootFreeBytes"];

        return new InstanceCapacityOptions
        {
            MaxActiveWorkers = ReadInt(configuration, $"{prefix}MaxActiveWorkers") ?? InstanceCapacityOptions.DefaultMaxActiveWorkers,
            MaxInactiveSessions = ReadInt(configuration, $"{prefix}MaxInactiveSessions") ?? InstanceCapacityOptions.DefaultMaxInactiveSessions,
            MaxArchiveBytes = ReadInt64(archiveValue, $"{prefix}MaxArchiveBytes") ?? InstanceCapacityOptions.DefaultMaxArchiveBytes,
            MaxExpandedBytes = ReadInt64(configuration, $"{prefix}MaxExpandedBytes") ?? InstanceCapacityOptions.DefaultMaxExpandedBytes,
            MaxArchiveSingleFileBytes = ReadInt64(configuration, $"{prefix}MaxArchiveSingleFileBytes") ?? InstanceCapacityOptions.DefaultMaxArchiveSingleFileBytes,
            MaxArchiveEntryCount = ReadInt(configuration, $"{prefix}MaxArchiveEntryCount") ?? InstanceCapacityOptions.DefaultMaxArchiveEntryCount,
            MaxSessionRootBytes = ReadInt64(configuration, $"{prefix}MaxSessionRootBytes") ?? InstanceCapacityOptions.DefaultMaxSessionRootBytes,
            MaxSessionRootFileCount = ReadInt(configuration, $"{prefix}MaxSessionRootFileCount") ?? InstanceCapacityOptions.DefaultMaxSessionRootFileCount,
            MaxStagingReservedBytes = ReadInt64(configuration, $"{prefix}MaxStagingReservedBytes") ?? InstanceCapacityOptions.DefaultMaxStagingReservedBytes,
            MaxSaveFileBytes = ReadInt64(configuration, $"{prefix}MaxSaveFileBytes") ?? InstanceCapacityOptions.DefaultMaxSaveFileBytes,
            MaxSaveListedFiles = ReadInt(configuration, $"{prefix}MaxSaveListedFiles") ?? InstanceCapacityOptions.DefaultMaxSaveListedFiles,
            MaxSaveListBytes = ReadInt64(configuration, $"{prefix}MaxSaveListBytes") ?? InstanceCapacityOptions.DefaultMaxSaveListBytes,
            MinDataRootFreeBytes = ReadInt64(freeSpaceValue, $"{prefix}MinDataRootFreeBytes") ?? InstanceCapacityOptions.DefaultMinDataRootFreeBytes,
        };
    }

    public static PresentationAssetOptions BindAssets(IConfiguration configuration)
    {
        const string prefix = "CloudEmuera:Assets:";
        return new PresentationAssetOptions
        {
            MaxManifestBytes = ReadInt64(configuration, $"{prefix}MaxManifestBytes") ?? PresentationAssetOptions.DefaultMaxManifestBytes,
            MaxAssetBytes = ReadInt64(configuration, $"{prefix}MaxAssetBytes") ?? PresentationAssetOptions.DefaultMaxAssetBytes,
            MaxRangeBytes = ReadInt64(configuration, $"{prefix}MaxRangeBytes") ?? PresentationAssetOptions.DefaultMaxRangeBytes,
            MaxConcurrentReads = ReadInt(configuration, $"{prefix}MaxConcurrentReads") ?? PresentationAssetOptions.DefaultMaxConcurrentReads,
            MaxInFlightBytes = ReadInt64(configuration, $"{prefix}MaxInFlightBytes") ?? PresentationAssetOptions.DefaultMaxInFlightBytes,
        };
    }

    public static RealtimeOutputOptions BindRealtimeOutput(IConfiguration configuration)
    {
        const string prefix = "CloudEmuera:Realtime:";
        return new RealtimeOutputOptions
        {
            SnapshotMaxBytes = ReadInt64(configuration, $"{prefix}SnapshotMaxBytes") ?? RealtimeOutputOptions.DefaultSnapshotMaxBytes,
            BatchTargetBytes = ReadInt64(configuration, $"{prefix}BatchTargetBytes") ?? RealtimeOutputOptions.DefaultBatchTargetBytes,
            BatchMaxTransactions = ReadInt(configuration, $"{prefix}BatchMaxTransactions") ?? RealtimeOutputOptions.DefaultBatchMaxTransactions,
            BatchMaxDelay = TimeSpan.FromMilliseconds(ReadInt(configuration, $"{prefix}BatchMaxDelayMilliseconds") ?? 16),
            ConnectionQueueSoftBytes = ReadInt64(configuration, $"{prefix}QueueSoftBytes") ?? RealtimeOutputOptions.DefaultQueueSoftBytes,
            ConnectionQueueHardBytes = ReadInt64(configuration, $"{prefix}QueueHardBytes") ?? RealtimeOutputOptions.DefaultQueueHardBytes,
            ConnectionQueueSoftMessages = ReadInt(configuration, $"{prefix}QueueSoftMessages") ?? RealtimeOutputOptions.DefaultQueueSoftMessages,
            ConnectionQueueHardMessages = ReadInt(configuration, $"{prefix}QueueHardMessages") ?? RealtimeOutputOptions.DefaultQueueHardMessages,
            MaxSnapshotResyncAttempts = ReadInt(configuration, $"{prefix}MaxSnapshotResyncAttempts") ?? 3,
            SnapshotResyncWindow = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}SnapshotResyncWindowSeconds") ?? 30),
        };
    }

    public static RealtimeGatewayOptions BindRealtimeGateway(IConfiguration configuration)
    {
        const string prefix = "CloudEmuera:Realtime:";
        return new RealtimeGatewayOptions
        {
            ClientHelloTimeout = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}ClientHelloTimeoutSeconds") ?? 5),
            ClientMessageMaxBytes = ReadInt(configuration, $"{prefix}ClientMessageMaxBytes") ?? RealtimeProtocol.DefaultClientMessageMaxBytes,
            ClientJsonMaxDepth = ReadInt(configuration, $"{prefix}ClientJsonMaxDepth") ?? RealtimeProtocol.DefaultClientJsonMaxDepth,
            MaxConnections = ReadInt(configuration, $"{prefix}MaxConnections") ?? 256,
            MaxConnectionsPerSession = ReadInt(configuration, $"{prefix}MaxConnectionsPerSession") ?? 16,
            MaxSubscriptionsPerConnection = ReadInt(configuration, $"{prefix}MaxSubscriptionsPerConnection") ?? 4,
            MaxPendingInputsPerConnection = ReadInt(configuration, $"{prefix}MaxPendingInputsPerConnection") ?? 32,
            MaxPendingInputsPerWorker = ReadInt(configuration, $"{prefix}MaxPendingInputsPerWorker") ?? 128,
            ControlQueueMaxBytes = ReadInt64(configuration, $"{prefix}ControlQueueMaxBytes") ?? 256 * 1024,
            ControlQueueMaxMessages = ReadInt(configuration, $"{prefix}ControlQueueMaxMessages") ?? 64,
            EnvelopeMaxBytes = ReadInt64(configuration, $"{prefix}EnvelopeMaxBytes") ?? 2 * 1024,
            InputResultTimeout = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}InputResultTimeoutSeconds") ?? 10),
            WebSocketSendTimeout = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}WebSocketSendTimeoutSeconds") ?? 10),
            HeartbeatInterval = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}HeartbeatIntervalSeconds") ?? 20),
            HeartbeatTimeout = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}HeartbeatTimeoutSeconds") ?? 10),
            IdentityRevalidationInterval = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}IdentityRevalidationIntervalSeconds") ?? 60),
            ConnectionShutdownTimeout = TimeSpan.FromSeconds(ReadInt(configuration, $"{prefix}ConnectionShutdownTimeoutSeconds") ?? 5),
        };
    }

    public static int? ReadInt(IConfiguration configuration, string key) =>
        ReadInt(configuration[key], key);

    public static long? ReadInt64(IConfiguration configuration, string key) =>
        ReadInt64(configuration[key], key);

    private static int? ReadInt(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        throw new InvalidOperationException($"Configuration key {key} must be a decimal integer.");
    }

    private static long? ReadInt64(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            return parsed;
        throw new InvalidOperationException($"Configuration key {key} must be a decimal integer.");
    }
}

internal static class DeploymentOptionsValidator
{
    public static void Validate(
        InstanceCapacityOptions capacity,
        RealtimeOutputOptions realtimeOutput,
        RealtimeGatewayOptions realtimeGateway,
        WorkerManagerOptions worker,
        PresentationAssetOptions assets)
    {
        capacity.Validate();
        assets.Validate();
        realtimeGateway.Validate(realtimeOutput);
        worker.Validate();

        if (worker.PendingEventMaxBytes > StructuredIpcLimits.MaxEnvelopeBytes ||
            worker.PendingInputMaxBytes > StructuredIpcLimits.MaxEnvelopeBytes * 2L)
            throw new InvalidOperationException("Worker pending queue bytes exceed the protocol safety bound.");

        // The v5 IPC envelope is 12 MiB. The browser envelope has a small
        // framing allowance, but must remain bounded by the same deployment
        // safety ceiling rather than turning a configured snapshot into an
        // unbounded WebSocket frame.
        const long browserEnvelopeAllowance = 64L * 1024;
        if (realtimeGateway.ServerMessageMaxBytes > StructuredIpcLimits.MaxEnvelopeBytes + browserEnvelopeAllowance)
            throw new InvalidOperationException("Realtime server message size exceeds the protocol safety bound.");

        if (assets.MaxAssetBytes > capacity.MaxSessionRootBytes)
            throw new InvalidOperationException("CloudEmuera:Assets:MaxAssetBytes cannot exceed Capacity:MaxSessionRootBytes.");
    }
}

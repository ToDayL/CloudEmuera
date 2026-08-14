namespace CloudEmuera.Api.Realtime;

/// <summary>
/// Deployment-level limits for the WebSocket control plane.  Display payload
/// limits remain owned by <see cref="RealtimeOutputOptions"/>; this type only
/// accounts for the envelope, connection and input-control budgets.
/// </summary>
public sealed record RealtimeGatewayOptions
{
    public const int AbsoluteMaxConnections = 4096;
    public const int AbsoluteMaxConnectionsPerSession = 256;
    public const int AbsoluteMaxSubscriptionsPerConnection = 16;
    public const int AbsoluteMaxPendingInputsPerConnection = 256;
    public const int AbsoluteMaxClientMessageBytes = 4 * 1024 * 1024;

    public static RealtimeGatewayOptions Default => new();

    public TimeSpan ClientHelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int ClientMessageMaxBytes { get; init; } = 64 * 1024;
    public int ClientJsonMaxDepth { get; init; } = 32;
    public int MaxConnections { get; init; } = 256;
    public int MaxConnectionsPerSession { get; init; } = 16;
    public int MaxSubscriptionsPerConnection { get; init; } = 4;
    public int MaxPendingInputsPerConnection { get; init; } = 32;
    public int MaxPendingInputsPerWorker { get; init; } = 128;
    public long ControlQueueMaxBytes { get; init; } = 256 * 1024;
    public int ControlQueueMaxMessages { get; init; } = 64;
    public long EnvelopeMaxBytes { get; init; } = 2 * 1024;
    public long SnapshotMaxBytes { get; private set; }
    public long ServerMessageMaxBytes { get; private set; }
    public TimeSpan InputResultTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan WebSocketSendTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdentityRevalidationInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ConnectionShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate(long snapshotMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotMaxBytes);
        ValidateConnectionLimits();
        try
        {
            SnapshotMaxBytes = snapshotMaxBytes;
            ServerMessageMaxBytes = checked(snapshotMaxBytes + EnvelopeMaxBytes);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotMaxBytes), "Realtime message size overflows.");
        }
    }

    public void ValidateConnectionLimits()
    {
        ValidatePositive(ClientHelloTimeout, nameof(ClientHelloTimeout));
        ValidatePositive(HeartbeatInterval, nameof(HeartbeatInterval));
        ValidatePositive(HeartbeatTimeout, nameof(HeartbeatTimeout));
        ValidatePositive(IdentityRevalidationInterval, nameof(IdentityRevalidationInterval));
        ValidatePositive(InputResultTimeout, nameof(InputResultTimeout));
        ValidatePositive(WebSocketSendTimeout, nameof(WebSocketSendTimeout));
        ValidatePositive(ConnectionShutdownTimeout, nameof(ConnectionShutdownTimeout));
        if (HeartbeatInterval < HeartbeatTimeout)
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval), "Heartbeat interval must not be shorter than its response timeout.");
        if (ClientMessageMaxBytes <= 0 || ClientMessageMaxBytes > AbsoluteMaxClientMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(ClientMessageMaxBytes));
        if (ClientJsonMaxDepth is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(ClientJsonMaxDepth));
        if (MaxConnections is < 1 or > AbsoluteMaxConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (MaxConnectionsPerSession is < 1 or > AbsoluteMaxConnectionsPerSession)
            throw new ArgumentOutOfRangeException(nameof(MaxConnectionsPerSession));
        if (MaxSubscriptionsPerConnection is < 1 or > AbsoluteMaxSubscriptionsPerConnection || MaxSubscriptionsPerConnection > 16)
            throw new ArgumentOutOfRangeException(nameof(MaxSubscriptionsPerConnection));
        if (MaxPendingInputsPerConnection is < 1 or > AbsoluteMaxPendingInputsPerConnection)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingInputsPerConnection));
        if (MaxPendingInputsPerWorker is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingInputsPerWorker));
        if (ControlQueueMaxMessages is < 1 or > 4096 || ControlQueueMaxBytes <= 0 || ControlQueueMaxBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(ControlQueueMaxMessages));
        if (EnvelopeMaxBytes <= 0 || EnvelopeMaxBytes > 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(EnvelopeMaxBytes));
        if (HeartbeatTimeout >= TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(HeartbeatTimeout));
    }

    public void Validate(RealtimeOutputOptions outputOptions)
    {
        ArgumentNullException.ThrowIfNull(outputOptions);
        outputOptions.Validate();
        Validate(outputOptions.SnapshotMaxBytes);
    }

    private static void ValidatePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name);
    }
}

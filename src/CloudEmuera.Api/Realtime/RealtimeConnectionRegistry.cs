namespace CloudEmuera.Api.Realtime;

public sealed record RealtimeConnectionDiagnostics(
    string ConnectionId,
    string ActorUserId,
    IReadOnlyList<string> SessionIds,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastActivityAt,
    int PendingInputs,
    long ControlQueueBytes);

public sealed record RealtimeRegistryDiagnostics(int ConnectionCount, int SubscriptionCount);

/// <summary>
/// Transient connection admission and diagnostics.  It stores no cookie,
/// prompt, input value, snapshot or Session state and is never persisted.
/// </summary>
public sealed class RealtimeConnectionRegistry
{
    private readonly RealtimeGatewayOptions options;
    private readonly TimeProvider timeProvider;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> sessionCounts = new(StringComparer.Ordinal);

    private sealed class Entry(string connectionId, string actorUserId, DateTimeOffset connectedAt)
    {
        public string ConnectionId { get; } = connectionId;
        public string ActorUserId { get; } = actorUserId;
        public DateTimeOffset ConnectedAt { get; } = connectedAt;
        public DateTimeOffset LastActivityAt { get; set; } = connectedAt;
        public HashSet<string> SessionIds { get; } = new(StringComparer.Ordinal);
        public int PendingInputs { get; set; }
        public long ControlQueueBytes { get; set; }
    }

    public RealtimeConnectionRegistry(RealtimeGatewayOptions options, TimeProvider? timeProvider = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.options.ValidateConnectionLimits();
    }

    public int ConnectionCount
    {
        get { lock (sync) return entries.Count; }
    }

    public int SubscriptionCount
    {
        get { lock (sync) return sessionCounts.Values.Sum(); }
    }

    public RealtimeRegistryDiagnostics ReadDiagnostics()
    {
        lock (sync)
            return new RealtimeRegistryDiagnostics(entries.Count, sessionCounts.Values.Sum());
    }

    public RealtimeConnectionAdmission? TryReserve(string actorUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        lock (sync)
        {
            if (entries.Count >= options.MaxConnections)
                return null;
            string connectionId = $"conn_{Guid.CreateVersion7():N}";
            DateTimeOffset now = timeProvider.GetUtcNow();
            entries.Add(connectionId, new Entry(connectionId, actorUserId, now));
            return new RealtimeConnectionAdmission(this, connectionId);
        }
    }

    internal bool TryAddSubscription(string connectionId, string sessionId)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(connectionId, out Entry? entry))
                return false;
            if (entry.SessionIds.Contains(sessionId))
                return true;
            int count = sessionCounts.TryGetValue(sessionId, out int current) ? current : 0;
            if (count >= options.MaxConnectionsPerSession)
                return false;
            entry.SessionIds.Add(sessionId);
            sessionCounts[sessionId] = count + 1;
            return true;
        }
    }

    internal void RemoveSubscription(string connectionId, string sessionId)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(connectionId, out Entry? entry) || !entry.SessionIds.Remove(sessionId))
                return;
            if (sessionCounts.TryGetValue(sessionId, out int count) && count <= 1)
                sessionCounts.Remove(sessionId);
            else if (count > 1)
                sessionCounts[sessionId] = count - 1;
        }
    }

    internal bool TrySetPendingInputs(string connectionId, int pendingInputs)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(connectionId, out Entry? entry))
                return false;
            entry.PendingInputs = pendingInputs;
            entry.LastActivityAt = timeProvider.GetUtcNow();
            return true;
        }
    }

    internal bool TrySetControlQueueBytes(string connectionId, long bytes)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(connectionId, out Entry? entry))
                return false;
            entry.ControlQueueBytes = bytes;
            entry.LastActivityAt = timeProvider.GetUtcNow();
            return true;
        }
    }

    internal void Touch(string connectionId)
    {
        lock (sync)
        {
            if (entries.TryGetValue(connectionId, out Entry? entry))
                entry.LastActivityAt = timeProvider.GetUtcNow();
        }
    }

    internal void Release(string connectionId)
    {
        lock (sync)
        {
            if (!entries.Remove(connectionId, out Entry? entry))
                return;
            foreach (string sessionId in entry.SessionIds)
            {
                if (sessionCounts.TryGetValue(sessionId, out int count) && count <= 1)
                    sessionCounts.Remove(sessionId);
                else if (count > 1)
                    sessionCounts[sessionId] = count - 1;
            }
        }
    }

    public IReadOnlyList<RealtimeConnectionDiagnostics> Snapshot()
    {
        lock (sync)
        {
            return entries.Values
                .Select(entry => new RealtimeConnectionDiagnostics(
                    entry.ConnectionId,
                    entry.ActorUserId,
                    entry.SessionIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    entry.ConnectedAt,
                    entry.LastActivityAt,
                    entry.PendingInputs,
                    entry.ControlQueueBytes))
                .ToArray();
        }
    }
}

public sealed class RealtimeConnectionAdmission : IDisposable
{
    private readonly RealtimeConnectionRegistry registry;
    private int released;

    internal RealtimeConnectionAdmission(RealtimeConnectionRegistry registry, string connectionId)
    {
        this.registry = registry;
        ConnectionId = connectionId;
    }

    public string ConnectionId { get; }

    public bool TryAddSubscription(string sessionId) =>
        Volatile.Read(ref released) == 0 && registry.TryAddSubscription(ConnectionId, sessionId);

    public void RemoveSubscription(string sessionId)
    {
        if (Volatile.Read(ref released) == 0)
            registry.RemoveSubscription(ConnectionId, sessionId);
    }

    public void SetPendingInputs(int count)
    {
        if (Volatile.Read(ref released) == 0)
            registry.TrySetPendingInputs(ConnectionId, count);
    }

    public void SetControlQueueBytes(long bytes)
    {
        if (Volatile.Read(ref released) == 0)
            registry.TrySetControlQueueBytes(ConnectionId, bytes);
    }

    public void Touch()
    {
        if (Volatile.Read(ref released) == 0)
            registry.Touch(ConnectionId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
            registry.Release(ConnectionId);
    }
}

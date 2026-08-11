namespace CloudEmuera.Infrastructure.Persistence;

public sealed class WorkerLeaseRow
{
    public string SessionId { get; set; } = string.Empty;

    public string WorkerId { get; set; } = string.Empty;

    public long Epoch { get; set; }

    public WorkerLeaseStatus Status { get; set; }

    public long? Pid { get; set; }

    public string ControlPlaneInstanceId { get; set; } = string.Empty;

    public string? ProcessBootId { get; set; }

    public long? ProcessStartTicks { get; set; }

    public string IpcEndpoint { get; set; } = string.Empty;

    public string RuntimeVersion { get; set; } = string.Empty;

    public int ProtocolVersion { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }

    public DateTimeOffset HeartbeatAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public SessionRow? Session { get; set; }
}

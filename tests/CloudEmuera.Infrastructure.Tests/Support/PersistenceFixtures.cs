using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Persistence;

namespace CloudEmuera.Infrastructure.Tests.Support;

internal static class PersistenceFixtures
{
    public static readonly DateTimeOffset CreatedAt = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

    public static QuotaProfileRow CreateQuotaProfile(string id = "qtp_fixture") => new()
    {
        Id = id,
        Name = $"Fixture {id}",
        MaxActiveSessions = 4,
        MaxGamePackageBytes = 10_000_000,
        MaxSessionBytes = 20_000_000,
        MaxOutputBytesPerSecond = 100_000,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    public static CloudEmueraUser CreateUser(string id = "usr_fixture", string normalizedName = "FIXTURE") => new()
    {
        Id = id,
        LoginName = normalizedName.ToLowerInvariant(),
        NormalizedLoginName = normalizedName,
        QuotaProfileId = "qtp_fixture",
        SecurityStamp = "fixture-stamp",
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    public static GameRow CreateGame(string id = "game_fixture", string ownerId = "usr_fixture", string name = "Fixture Game", bool withContent = true) => new()
    {
        Id = id,
        OwnerUserId = ownerId,
        Name = name,
        Visibility = GameVisibility.Private,
        Status = GameStatus.Active,
        WorkspaceStatus = GameWorkspaceStatus.None,
        CurrentContentPath = withContent ? $"games/{id}/content" : null,
        ContentDigest = withContent ? "sha256:" + new string('a', 64) : null,
        ContentRevision = withContent ? 1 : 0,
        ActivatedBy = withContent ? ownerId : null,
        ActivatedAt = withContent ? CreatedAt : null,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    public static SessionRow CreateSession(
        string id = "sess_fixture",
        string gameId = "game_fixture",
        long workerEpoch = 0) => new()
    {
        Id = id,
        OwnerUserId = "usr_fixture",
        GameId = gameId,
        SourceContentDigest = "sha256:" + new string('a', 64),
        SourceContentRevision = 1,
        SessionRootManifestDigest = "sha256:" + new string('a', 64),
        SaveLayout = 0,
        RuntimeVersion = "headless-test",
        SessionRootPath = $"sessions/{id}/root",
        Name = "Fixture Session",
        State = SessionState.Creating,
        WorkerEpoch = workerEpoch,
        CreatedAt = CreatedAt,
        LastActivityAt = CreatedAt,
    };

    public static WorkerLeaseRow CreateLease(string sessionId = "sess_fixture", long epoch = 1, string workerId = "wrk_fixture") => new()
    {
        SessionId = sessionId,
        WorkerId = workerId,
        Epoch = epoch,
        Status = WorkerLeaseStatus.Active,
        Pid = 1,
        ControlPlaneInstanceId = "ctl_fixture",
        ProcessBootId = "00000000-0000-0000-0000-000000000000",
        ProcessStartTicks = 1,
        IpcEndpoint = $"uds/{workerId}",
        RuntimeVersion = "headless-test",
        ProtocolVersion = 2,
        AcquiredAt = CreatedAt,
        HeartbeatAt = CreatedAt.AddSeconds(1),
        ExpiresAt = CreatedAt.AddSeconds(30),
    };

    public static AuditEventRow CreateAudit(string id = "audit_fixture") => new()
    {
        Id = id,
        OccurredAt = CreatedAt,
        ActorType = AuditActorType.System,
        Action = "SESSION_CREATED",
        ResourceType = "SESSION",
        ResourceId = "sess_fixture",
        Result = AuditResult.Succeeded,
        MetadataJson = "{}",
    };

    public static IdempotencyRecordRow CreateIdempotency(string key = "request-1") => new()
    {
        ActorUserId = "usr_fixture",
        Scope = "SESSION_CREATE",
        IdempotencyKey = key,
        RequestDigest = "sha256:" + new string('a', 64),
        ResponseStatus = 201,
        ResponseJson = "{}",
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        ExpiresAt = CreatedAt.AddHours(1),
    };

    public static GameContentOperationRow CreateGameContentOperation(string id = "gop_fixture", GameContentOperationStatus status = GameContentOperationStatus.Running) => new()
    {
        Id = id,
        GameId = "game_fixture",
        OperationType = GameContentOperationType.Validate,
        Status = status,
        ExpectedGameStateVersion = 0,
        ExpectedContentRevision = 1,
        LeaseExpiresAt = CreatedAt.AddMinutes(15),
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        CompletedAt = status is GameContentOperationStatus.Committed or GameContentOperationStatus.Failed ? CreatedAt : null,
    };
}

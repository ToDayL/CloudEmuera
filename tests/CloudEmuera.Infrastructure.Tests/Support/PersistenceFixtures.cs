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

    public static GameRow CreateGame(string id = "game_fixture", string ownerId = "usr_fixture", string name = "Fixture Game") => new()
    {
        Id = id,
        OwnerUserId = ownerId,
        Name = name,
        Visibility = GameVisibility.Private,
        Status = GameStatus.Active,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
    };

    public static GameVersionRow CreateVersion(
        string id = "gver_fixture",
        string gameId = "game_fixture",
        string? digest = null,
        GameVersionStatus status = GameVersionStatus.Draft) => new()
    {
        Id = id,
        GameId = gameId,
        VersionLabel = id,
        Status = status,
        ContentDigest = digest,
        ContentPath = $"games/{gameId}/{id}/content",
        ManifestJson = "{}",
        RuntimeConfigJson = "{}",
        CompatibilitySummaryJson = "{}",
        CreatedBy = "usr_fixture",
        CreatedAt = CreatedAt,
        PublishedAt = status is GameVersionStatus.Published or GameVersionStatus.Blocked ? CreatedAt : null,
    };

    public static SessionRow CreateSession(
        string id = "sess_fixture",
        string gameId = "game_fixture",
        string gameVersionId = "gver_fixture",
        long workerEpoch = 0) => new()
    {
        Id = id,
        OwnerUserId = "usr_fixture",
        GameId = gameId,
        GameVersionId = gameVersionId,
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
        IpcEndpoint = $"uds/{workerId}",
        RuntimeVersion = "headless-test",
        ProtocolVersion = 1,
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
        ExpiresAt = CreatedAt.AddHours(1),
    };
}

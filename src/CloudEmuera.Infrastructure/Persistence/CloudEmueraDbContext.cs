using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class CloudEmueraDbContext(DbContextOptions<CloudEmueraDbContext> options)
    : DbContext(options)
{
    public DbSet<QuotaProfileRow> QuotaProfiles => Set<QuotaProfileRow>();

    public DbSet<CloudEmueraUser> Users => Set<CloudEmueraUser>();

    public DbSet<GameRow> Games => Set<GameRow>();

    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    public DbSet<WorkerLeaseRow> WorkerLeases => Set<WorkerLeaseRow>();

    public DbSet<IdempotencyRecordRow> IdempotencyRecords => Set<IdempotencyRecordRow>();

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    public DbSet<AuthSessionRow> AuthSessions => Set<AuthSessionRow>();
    public DbSet<InstanceStateRow> InstanceStates => Set<InstanceStateRow>();
    public DbSet<GamePackageIngestionRow> GamePackageIngestions => Set<GamePackageIngestionRow>();
    public DbSet<GameContentOperationRow> GameContentOperations => Set<GameContentOperationRow>();
    public DbSet<GameFileRow> GameFiles => Set<GameFileRow>();
    public DbSet<CompatibilityDiagnosticRow> CompatibilityDiagnostics => Set<CompatibilityDiagnosticRow>();
    public DbSet<GameContentCopyLeaseRow> GameContentCopyLeases => Set<GameContentCopyLeaseRow>();
    public DbSet<SessionCreationOperationRow> SessionCreationOperations => Set<SessionCreationOperationRow>();
    public DbSet<SessionRootMutationLeaseRow> SessionRootMutationLeases => Set<SessionRootMutationLeaseRow>();
    public DbSet<SaveFileOperationRow> SaveFileOperations => Set<SaveFileOperationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }
}

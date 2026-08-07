using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class WorkerLeaseConfiguration : IEntityTypeConfiguration<WorkerLeaseRow>
{
    public void Configure(EntityTypeBuilder<WorkerLeaseRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.WorkerLeasesTable, table =>
        {
            table.HasCheckConstraint("ck_worker_leases_session_id", SqliteCheckExpressions.IdentifierPrefix("session_id", "sess_"));
            table.HasCheckConstraint("ck_worker_leases_worker_id", "substr(worker_id, 1, 4) = 'wrk_' AND length(worker_id) BETWEEN 5 AND 128 AND instr(worker_id, char(0)) = 0");
            table.HasCheckConstraint("ck_worker_leases_epoch", "epoch > 0");
            table.HasCheckConstraint("ck_worker_leases_status", "status IN ('STARTING', 'ACTIVE', 'STOPPING', 'EXPIRED')");
            table.HasCheckConstraint("ck_worker_leases_pid", "pid IS NULL OR pid > 0");
            table.HasCheckConstraint("ck_worker_leases_ipc_endpoint", "length(ipc_endpoint) BETWEEN 1 AND 512 AND substr(ipc_endpoint, 1, 1) <> '/' AND instr(ipc_endpoint, char(92)) = 0 AND instr(ipc_endpoint, char(0)) = 0 AND instr(ipc_endpoint, '://') = 0 AND instr(ipc_endpoint, '//') = 0");
            table.HasCheckConstraint("ck_worker_leases_runtime_version", "length(runtime_version) BETWEEN 1 AND 128 AND instr(runtime_version, char(0)) = 0");
            table.HasCheckConstraint("ck_worker_leases_protocol_version", "protocol_version > 0");
            table.HasCheckConstraint("ck_worker_leases_time_order", "acquired_at >= 0 AND heartbeat_at >= acquired_at AND expires_at > heartbeat_at");
        });

        builder.HasKey(row => row.SessionId).HasName("pk_worker_leases");
        builder.Property(row => row.SessionId).HasColumnName("session_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.WorkerId).HasColumnName("worker_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.WorkerIdMaxLength).IsRequired();
        builder.Property(row => row.Epoch).HasColumnName("epoch").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<WorkerLeaseStatus>(), SqliteValueConverters.CreateEnumComparer<WorkerLeaseStatus>()).IsRequired();
        builder.Property(row => row.Pid).HasColumnName("pid").HasColumnType("INTEGER");
        builder.Property(row => row.IpcEndpoint).HasColumnName("ipc_endpoint").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IpcEndpointMaxLength).IsRequired();
        builder.Property(row => row.RuntimeVersion).HasColumnName("runtime_version").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.RuntimeVersionMaxLength).IsRequired();
        builder.Property(row => row.ProtocolVersion).HasColumnName("protocol_version").HasColumnType("INTEGER").IsRequired();
        ConfigureTime(builder.Property(row => row.AcquiredAt), "acquired_at");
        ConfigureTime(builder.Property(row => row.HeartbeatAt), "heartbeat_at");
        ConfigureTime(builder.Property(row => row.ExpiresAt), "expires_at");

        builder.HasIndex(row => row.WorkerId).IsUnique().HasDatabaseName("ux_worker_leases_worker_id");
        builder.HasIndex(row => new { row.SessionId, row.Epoch }).IsUnique().HasDatabaseName("ux_worker_leases_session_epoch");
        builder.HasOne(row => row.Session).WithOne(session => session.WorkerLease).HasForeignKey<WorkerLeaseRow>(row => new { row.SessionId, row.Epoch }).HasPrincipalKey<SessionRow>(session => new { session.Id, session.WorkerEpoch }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_worker_leases_session_epoch");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

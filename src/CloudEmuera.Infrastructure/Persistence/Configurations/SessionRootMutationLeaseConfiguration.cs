using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class SessionRootMutationLeaseConfiguration : IEntityTypeConfiguration<SessionRootMutationLeaseRow>
{
    public void Configure(EntityTypeBuilder<SessionRootMutationLeaseRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.SessionRootMutationLeasesTable, table =>
        {
            table.HasCheckConstraint("ck_session_root_mutation_leases_session_id", SqliteCheckExpressions.IdentifierPrefix("session_id", "sess_"));
            table.HasCheckConstraint("ck_session_root_mutation_leases_operation_id", "(substr(operation_id, 1, 4) = 'mut_' OR substr(operation_id, 1, 5) = 'sfop_') AND length(operation_id) BETWEEN 5 AND 64 AND instr(operation_id, char(0)) = 0");
            table.HasCheckConstraint("ck_session_root_mutation_leases_actor_id", SqliteCheckExpressions.IdentifierPrefix("actor_user_id", "usr_"));
            table.HasCheckConstraint("ck_session_root_mutation_leases_purpose", "purpose IN ('SAVE_IMPORT', 'SAVE_RENAME', 'SAVE_DELETE', 'SAVE_COPY')");
            table.HasCheckConstraint("ck_session_root_mutation_leases_time", "acquired_at >= 0 AND expires_at > acquired_at");
        });

        builder.HasKey(row => row.SessionId).HasName("pk_session_root_mutation_leases");
        builder.Property(row => row.SessionId).HasColumnName("session_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.OperationId).HasColumnName("operation_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.OperationIdMaxLength).IsRequired();
        builder.Property(row => row.ActorUserId).HasColumnName("actor_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Purpose).HasColumnName("purpose").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.MutationPurposeMaxLength).IsRequired();
        ConfigureTime(builder.Property(row => row.AcquiredAt), "acquired_at");
        ConfigureTime(builder.Property(row => row.ExpiresAt), "expires_at");

        builder.HasIndex(row => row.OperationId).IsUnique().HasDatabaseName("ux_session_root_mutation_leases_operation");
        builder.HasOne(row => row.Session).WithOne(session => session.MutationLease).HasForeignKey<SessionRootMutationLeaseRow>(row => row.SessionId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_session_root_mutation_leases_session");
        builder.HasOne(row => row.ActorUser).WithMany().HasForeignKey(row => row.ActorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_session_root_mutation_leases_actor_user");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

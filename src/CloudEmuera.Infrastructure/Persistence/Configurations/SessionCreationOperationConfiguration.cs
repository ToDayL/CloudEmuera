using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class SessionCreationOperationConfiguration : IEntityTypeConfiguration<SessionCreationOperationRow>
{
    public void Configure(EntityTypeBuilder<SessionCreationOperationRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.SessionCreationOperationsTable, table =>
        {
            table.HasCheckConstraint("ck_session_creation_operations_id", SqliteCheckExpressions.IdentifierPrefix("id", "scop_"));
            table.HasCheckConstraint("ck_session_creation_operations_session_id", SqliteCheckExpressions.IdentifierPrefix("session_id", "sess_"));
            table.HasCheckConstraint("ck_session_creation_operations_actor_id", SqliteCheckExpressions.IdentifierPrefix("actor_user_id", "usr_"));
            table.HasCheckConstraint("ck_session_creation_operations_status", "status IN ('PREPARED', 'COPYING', 'ROOT_PUBLISHED', 'COMMITTED', 'FAILED')");
            table.HasCheckConstraint("ck_session_creation_operations_path", SqliteCheckExpressions.RelativePath("staging_path"));
            table.HasCheckConstraint("ck_session_creation_operations_counters", "reserved_bytes >= 0 AND expected_file_count >= 0 AND expected_content_bytes >= 0 AND attempt_count >= 0 AND state_version >= 0");
            table.HasCheckConstraint("ck_session_creation_operations_error", "last_error_code IS NULL OR (length(last_error_code) BETWEEN 1 AND 128 AND instr(last_error_code, char(0)) = 0)");
            table.HasCheckConstraint("ck_session_creation_operations_completion", "(status IN ('COMMITTED', 'FAILED') AND completed_at IS NOT NULL) OR (status NOT IN ('COMMITTED', 'FAILED') AND completed_at IS NULL)");
            table.HasCheckConstraint("ck_session_creation_operations_time", "created_at >= 0 AND updated_at >= created_at AND (completed_at IS NULL OR completed_at >= updated_at)");
        });

        builder.HasKey(row => row.Id).HasName("pk_session_creation_operations");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.OperationIdMaxLength).IsRequired();
        builder.Property(row => row.SessionId).HasColumnName("session_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.ActorUserId).HasColumnName("actor_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<SessionCreationOperationStatus>(), SqliteValueConverters.CreateEnumComparer<SessionCreationOperationStatus>()).IsRequired();
        builder.Property(row => row.StagingPath).HasColumnName("staging_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength).IsRequired();
        builder.Property(row => row.ReservedBytes).HasColumnName("reserved_bytes").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.ExpectedFileCount).HasColumnName("expected_file_count").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.ExpectedContentBytes).HasColumnName("expected_content_bytes").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.AttemptCount).HasColumnName("attempt_count").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();
        builder.Property(row => row.LastErrorCode).HasColumnName("last_error_code").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ReasonCodeMaxLength);
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        builder.Property(row => row.CompletedAt).HasColumnName("completed_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();

        builder.HasIndex(row => row.SessionId).IsUnique().HasDatabaseName("ux_session_creation_operations_session");
        builder.HasIndex(row => new { row.Status, row.UpdatedAt }).HasDatabaseName("ix_session_creation_operations_status_updated");
        builder.HasIndex(row => row.StagingPath).IsUnique().HasDatabaseName("ux_session_creation_operations_staging_path");
        builder.HasOne(row => row.Session).WithOne(session => session.CreationOperation).HasForeignKey<SessionCreationOperationRow>(row => row.SessionId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_session_creation_operations_session");
        builder.HasOne(row => row.ActorUser).WithMany().HasForeignKey(row => row.ActorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_session_creation_operations_actor_user");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class SaveFileOperationConfiguration : IEntityTypeConfiguration<SaveFileOperationRow>
{
    public void Configure(EntityTypeBuilder<SaveFileOperationRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.SaveFileOperationsTable, table =>
        {
            table.HasCheckConstraint("ck_save_file_operations_id", SqliteCheckExpressions.IdentifierPrefix("id", "sfop_"));
            table.HasCheckConstraint("ck_save_file_operations_session_id", SqliteCheckExpressions.IdentifierPrefix("session_id", "sess_"));
            table.HasCheckConstraint("ck_save_file_operations_actor_id", SqliteCheckExpressions.IdentifierPrefix("actor_user_id", "usr_"));
            table.HasCheckConstraint("ck_save_file_operations_scope", "idempotency_scope IN ('SAVE_IMPORT', 'SAVE_RENAME', 'SAVE_DELETE')");
            table.HasCheckConstraint("ck_save_file_operations_type", "type IN ('IMPORT', 'RENAME', 'DELETE')");
            table.HasCheckConstraint("ck_save_file_operations_status", "status IN ('PREPARED', 'STAGED', 'PUBLISHED', 'COMMITTED', 'FAILED')");
            table.HasCheckConstraint("ck_save_file_operations_key_hash", SqliteCheckExpressions.Sha256DigestColumn("idempotency_key_hash"));
            table.HasCheckConstraint("ck_save_file_operations_source_path", "source_path IS NULL OR (length(source_path) BETWEEN 1 AND 512 AND substr(source_path, 1, 1) <> '/' AND instr(source_path, char(92)) = 0 AND instr(source_path, char(0)) = 0 AND instr(source_path, '//') = 0)");
            table.HasCheckConstraint("ck_save_file_operations_target_path", SqliteCheckExpressions.RelativePath("target_path"));
            table.HasCheckConstraint("ck_save_file_operations_payload_path", "payload_path IS NULL OR (length(payload_path) BETWEEN 1 AND 512 AND substr(payload_path, 1, 1) <> '/' AND instr(payload_path, char(92)) = 0 AND instr(payload_path, char(0)) = 0 AND instr(payload_path, '//') = 0)");
            table.HasCheckConstraint("ck_save_file_operations_payload", "payload_size IS NULL OR payload_size >= 0");
            table.HasCheckConstraint("ck_save_file_operations_digest", SqliteCheckExpressions.Sha256DigestColumn("payload_digest", nullable: true));
            table.HasCheckConstraint("ck_save_file_operations_expected_identity", "expected_source_identity_json IS NULL OR (length(expected_source_identity_json) BETWEEN 2 AND 1048576 AND json_valid(expected_source_identity_json) = 1)");
            table.HasCheckConstraint("ck_save_file_operations_result", SqliteCheckExpressions.ValidJson("result_json"));
            table.HasCheckConstraint("ck_save_file_operations_error", "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 128 AND instr(error_code, char(0)) = 0)");
            table.HasCheckConstraint("ck_save_file_operations_completion", "(status IN ('COMMITTED', 'FAILED') AND completed_at IS NOT NULL) OR (status NOT IN ('COMMITTED', 'FAILED') AND completed_at IS NULL)");
            table.HasCheckConstraint("ck_save_file_operations_time", "created_at >= 0 AND updated_at >= created_at AND (completed_at IS NULL OR completed_at >= updated_at)");
            table.HasCheckConstraint("ck_save_file_operations_state", "state_version >= 0");
        });

        builder.HasKey(row => row.Id).HasName("pk_save_file_operations");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.OperationIdMaxLength).IsRequired();
        builder.Property(row => row.SessionId).HasColumnName("session_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.ActorUserId).HasColumnName("actor_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.IdempotencyScope).HasColumnName("idempotency_scope").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ScopeMaxLength).IsRequired();
        builder.Property(row => row.IdempotencyKeyHash).HasColumnName("idempotency_key_hash").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdempotencyKeyHashLength).IsRequired();
        builder.Property(row => row.Type).HasColumnName("type").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<SaveFileOperationType>(), SqliteValueConverters.CreateEnumComparer<SaveFileOperationType>()).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<SaveFileOperationStatus>(), SqliteValueConverters.CreateEnumComparer<SaveFileOperationStatus>()).IsRequired();
        builder.Property(row => row.SourcePath).HasColumnName("source_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.TargetPath).HasColumnName("target_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength).IsRequired();
        builder.Property(row => row.PayloadPath).HasColumnName("payload_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.PayloadSize).HasColumnName("payload_size").HasColumnType("INTEGER");
        builder.Property(row => row.PayloadDigest).HasColumnName("payload_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.ExpectedSourceIdentityJson).HasColumnName("expected_source_identity_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength);
        builder.Property(row => row.ExpectedTargetCaptured).HasColumnName("expected_target_captured").HasColumnType("INTEGER").HasDefaultValue(false).IsRequired();
        builder.Property(row => row.ExpectedTargetExists).HasColumnName("expected_target_exists").HasColumnType("INTEGER").HasDefaultValue(false).IsRequired();
        builder.Property(row => row.ExpectedTargetIdentityJson).HasColumnName("expected_target_identity_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength);
        builder.Property(row => row.ResultJson).HasColumnName("result_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).IsRequired();
        builder.Property(row => row.ErrorCode).HasColumnName("error_code").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ReasonCodeMaxLength);
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        builder.Property(row => row.CompletedAt).HasColumnName("completed_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();

        builder.HasIndex(row => new { row.Status, row.UpdatedAt, row.Id }).HasDatabaseName("ix_save_file_operations_status_updated");
        builder.HasIndex(row => new { row.SessionId, row.Status }).HasDatabaseName("ix_save_file_operations_session_status");
        builder.HasIndex(row => new { row.ActorUserId, row.IdempotencyScope, row.IdempotencyKeyHash }).IsUnique().HasDatabaseName("ux_save_file_operations_idempotency");
        builder.HasOne(row => row.Session).WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_save_file_operations_session");
        builder.HasOne(row => row.ActorUser).WithMany().HasForeignKey(row => row.ActorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_save_file_operations_actor_user");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

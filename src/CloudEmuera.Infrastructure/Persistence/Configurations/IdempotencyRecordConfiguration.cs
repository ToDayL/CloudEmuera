using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecordRow>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecordRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.IdempotencyRecordsTable, table =>
        {
            table.HasCheckConstraint("ck_idempotency_actor_id", SqliteCheckExpressions.IdentifierPrefix("actor_user_id", "usr_"));
            table.HasCheckConstraint("ck_idempotency_scope", "length(scope) BETWEEN 1 AND 100 AND instr(scope, char(0)) = 0");
            table.HasCheckConstraint("ck_idempotency_key", "length(idempotency_key) BETWEEN 1 AND 256 AND instr(idempotency_key, char(0)) = 0");
            table.HasCheckConstraint("ck_idempotency_request_digest", SqliteCheckExpressions.IdempotencyDigest);
            table.HasCheckConstraint("ck_idempotency_status", "status IN ('IN_PROGRESS', 'SUCCEEDED', 'FAILED')");
            table.HasCheckConstraint("ck_idempotency_terminal_fields", "(status = 'IN_PROGRESS' AND error_code IS NULL AND completed_at IS NULL) OR (status = 'SUCCEEDED' AND error_code IS NULL AND completed_at IS NOT NULL) OR (status = 'FAILED' AND error_code IS NOT NULL AND completed_at IS NOT NULL)");
            table.HasCheckConstraint("ck_idempotency_error_code", "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 128 AND instr(error_code, char(0)) = 0)");
            table.HasCheckConstraint("ck_idempotency_response_status", "response_status BETWEEN 100 AND 599");
            table.HasCheckConstraint("ck_idempotency_response_json", SqliteCheckExpressions.Json.Replace("{0}", "response_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_idempotency_resource_id", "resource_id IS NULL OR (length(resource_id) BETWEEN 1 AND 128 AND instr(resource_id, char(0)) = 0)");
            table.HasCheckConstraint("ck_idempotency_time_order", "created_at >= 0 AND updated_at >= created_at AND expires_at > created_at AND (completed_at IS NULL OR completed_at >= updated_at)");
        });

        builder.HasKey(row => new { row.ActorUserId, row.Scope, row.IdempotencyKey }).HasName("pk_idempotency_records");
        builder.Property(row => row.ActorUserId).HasColumnName("actor_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Scope).HasColumnName("scope").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ScopeMaxLength).IsRequired();
        builder.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdempotencyKeyMaxLength).IsRequired();
        builder.Property(row => row.RequestDigest).HasColumnName("request_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.RequestDigestLength).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<IdempotencyRecordStatus>(), SqliteValueConverters.CreateEnumComparer<IdempotencyRecordStatus>()).IsRequired();
        builder.Property(row => row.ResponseStatus).HasColumnName("response_status").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.ResponseJson).HasColumnName("response_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ResponseJsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.ResourceId).HasColumnName("resource_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ResourceIdMaxLength);
        builder.Property(row => row.ErrorCode).HasColumnName("error_code").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ReasonCodeMaxLength);
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        ConfigureTime(builder.Property(row => row.ExpiresAt), "expires_at");
        builder.Property(row => row.CompletedAt).HasColumnName("completed_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);

        builder.HasIndex(row => row.ExpiresAt).HasDatabaseName("ix_idempotency_records_expires_at");
        builder.HasIndex(row => new { row.Status, row.UpdatedAt }).HasDatabaseName("ix_idempotency_records_status_updated");
        builder.HasOne(row => row.ActorUser).WithMany().HasForeignKey(row => row.ActorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_idempotency_records_actor_user");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

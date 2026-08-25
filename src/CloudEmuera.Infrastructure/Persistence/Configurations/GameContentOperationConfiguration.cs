using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class GameContentOperationConfiguration : IEntityTypeConfiguration<GameContentOperationRow>
{
    public void Configure(EntityTypeBuilder<GameContentOperationRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.GameContentOperationsTable, table =>
        {
            table.HasCheckConstraint("ck_game_content_operations_id", SqliteCheckExpressions.IdentifierPrefix("id", "gop_"));
            table.HasCheckConstraint("ck_game_content_operations_game_id", SqliteCheckExpressions.IdentifierPrefix("game_id", "game_"));
            table.HasCheckConstraint("ck_game_content_operations_type", "operation_type IN ('IMPORT', 'RESET_WORKSPACE', 'VALIDATE', 'ACTIVATE')");
            table.HasCheckConstraint("ck_game_content_operations_status", "status IN ('PENDING', 'RUNNING', 'CONTENT_READY', 'COMMITTED', 'FAILED')");
            table.HasCheckConstraint("ck_game_content_operations_stage", "stage IN ('PREPARING', 'RECEIVING', 'INSPECTING_ARCHIVE', 'EXTRACTING', 'NORMALIZING_ENCODING', 'ANALYZING', 'CONSUMING_STAGING', 'COPYING_CONTENT', 'VALIDATING_CONTENT', 'RUNNING_VALIDATOR', 'PUBLISHING_CONTENT', 'COMPLETED')");
            table.HasCheckConstraint("ck_game_content_operations_versions", "expected_game_state_version >= 0 AND expected_content_revision >= 0 AND state_version >= 0");
            table.HasCheckConstraint("ck_game_content_operations_work_path", $"work_path IS NULL OR ({SqliteCheckExpressions.RelativePath("work_path")})");
            table.HasCheckConstraint("ck_game_content_operations_current_item", $"current_item IS NULL OR ({SqliteCheckExpressions.RelativePath("current_item")})");
            table.HasCheckConstraint("ck_game_content_operations_request_id", "request_id IS NULL OR (length(request_id) BETWEEN 1 AND 256 AND instr(request_id, char(0)) = 0)");
            table.HasCheckConstraint("ck_game_content_operations_digest", "content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
            table.HasCheckConstraint("ck_game_content_operations_error", "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 128 AND instr(error_code, char(0)) = 0)");
            table.HasCheckConstraint("ck_game_content_operations_time", "created_at >= 0 AND updated_at >= created_at AND lease_expires_at >= created_at AND (completed_at IS NULL OR completed_at >= created_at)");
            table.HasCheckConstraint("ck_game_content_operations_completion", "(status IN ('COMMITTED', 'FAILED') AND completed_at IS NOT NULL) OR (status NOT IN ('COMMITTED', 'FAILED') AND completed_at IS NULL)");
        });
        builder.HasKey(row => row.Id).HasName("pk_game_content_operations");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.GameId).HasColumnName("game_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.OperationType).HasColumnName("operation_type").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameContentOperationType>(), SqliteValueConverters.CreateEnumComparer<GameContentOperationType>()).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameContentOperationStatus>(), SqliteValueConverters.CreateEnumComparer<GameContentOperationStatus>()).IsRequired();
        builder.Property(row => row.Stage).HasColumnName("stage").HasColumnType("TEXT").HasMaxLength(32).HasConversion(SqliteValueConverters.CreateEnumConverter<GameContentOperationStage>(), SqliteValueConverters.CreateEnumComparer<GameContentOperationStage>()).HasDefaultValue(GameContentOperationStage.Preparing).IsRequired();
        builder.Property(row => row.CurrentItem).HasColumnName("current_item").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.ExpectedGameStateVersion).HasColumnName("expected_game_state_version").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.ExpectedContentRevision).HasColumnName("expected_content_revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.IngestionId).HasColumnName("ingestion_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.RequestId).HasColumnName("request_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdempotencyKeyMaxLength);
        builder.Property(row => row.WorkPath).HasColumnName("work_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.ContentDigest).HasColumnName("content_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.LeaseExpiresAt).HasColumnName("lease_expires_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
        builder.Property(row => row.ErrorCode).HasColumnName("error_code").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ReasonCodeMaxLength);
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        builder.Property(row => row.CompletedAt).HasColumnName("completed_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsConcurrencyToken().IsRequired();
        builder.HasIndex(row => row.IngestionId).IsUnique().HasFilter("ingestion_id IS NOT NULL").HasDatabaseName("ux_game_content_operations_ingestion");
        builder.HasIndex(row => row.RequestId).HasDatabaseName("ix_game_content_operations_request");
        builder.HasIndex(row => row.GameId).IsUnique().HasFilter("status IN ('PENDING', 'RUNNING', 'CONTENT_READY')").HasDatabaseName("ux_game_content_operations_active_game");
        builder.HasIndex(row => new { row.GameId, row.CreatedAt }).HasDatabaseName("ix_game_content_operations_game_created");
        builder.HasOne(row => row.Game).WithMany().HasForeignKey(row => row.GameId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_game_content_operations_game");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class GamePackageIngestionConfiguration : IEntityTypeConfiguration<GamePackageIngestionRow>
{
    public void Configure(EntityTypeBuilder<GamePackageIngestionRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.GamePackageIngestionsTable, table =>
        {
            table.HasCheckConstraint("ck_game_package_ingestions_id", SqliteCheckExpressions.IdentifierPrefix("id", "ing_"));
            table.HasCheckConstraint("ck_game_package_ingestions_owner", SqliteCheckExpressions.IdentifierPrefix("owner_user_id", "usr_"));
            table.HasCheckConstraint("ck_game_package_ingestions_status", "status IN ('RESERVED','RECEIVING','INSPECTING','EXTRACTING','ANALYZING','READY','CONSUMING','CONSUMED','FAILED','ABANDONED')");
            table.HasCheckConstraint("ck_game_package_ingestions_path", SqliteCheckExpressions.RelativePath("staging_path"));
            table.HasCheckConstraint("ck_game_package_ingestions_counters", "reserved_bytes >= 0 AND archive_bytes >= 0 AND expanded_bytes >= 0 AND entry_count >= 0 AND state_version >= 0");
            table.HasCheckConstraint("ck_game_package_ingestions_archive_digest", DigestExpression("archive_digest"));
            table.HasCheckConstraint("ck_game_package_ingestions_content_digest", DigestExpression("content_digest"));
            table.HasCheckConstraint("ck_game_package_ingestions_limits_json", SqliteCheckExpressions.Json.Replace("{0}", "limits_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_game_package_ingestions_summary_json", SqliteCheckExpressions.Json.Replace("{0}", "summary_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_game_package_ingestions_times", "created_at >= 0 AND updated_at >= created_at AND expires_at >= created_at AND (reservation_released_at IS NULL OR reservation_released_at >= created_at) AND (cleanup_completed_at IS NULL OR cleanup_completed_at >= created_at)");
            table.HasCheckConstraint("ck_game_package_ingestions_release", "(reserved_bytes > 0 AND reservation_released_at IS NULL) OR (reserved_bytes = 0 AND reservation_released_at IS NOT NULL)");
        });

        builder.HasKey(row => row.Id).HasName("pk_game_package_ingestions");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.OwnerUserId).HasColumnName("owner_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GamePackageIngestionStatus>(), SqliteValueConverters.CreateEnumComparer<GamePackageIngestionStatus>()).IsRequired();
        builder.Property(row => row.StagingPath).HasColumnName("staging_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength).IsRequired();
        builder.Property(row => row.ReservedBytes).HasColumnName("reserved_bytes").HasColumnType("INTEGER");
        builder.Property(row => row.ArchiveBytes).HasColumnName("archive_bytes").HasColumnType("INTEGER");
        builder.Property(row => row.ExpandedBytes).HasColumnName("expanded_bytes").HasColumnType("INTEGER");
        builder.Property(row => row.EntryCount).HasColumnName("entry_count").HasColumnType("INTEGER");
        builder.Property(row => row.ArchiveDigest).HasColumnName("archive_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.ContentDigest).HasColumnName("content_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.LimitsJson).HasColumnName("limits_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).IsRequired();
        builder.Property(row => row.SummaryJson).HasColumnName("summary_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).IsRequired();
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        ConfigureTime(builder.Property(row => row.ExpiresAt), "expires_at");
        builder.Property(row => row.ReservationReleasedAt).HasColumnName("reservation_released_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.CleanupCompletedAt).HasColumnName("cleanup_completed_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").IsConcurrencyToken();
        builder.HasIndex(row => row.StagingPath).IsUnique().HasDatabaseName("ux_game_package_ingestions_staging_path");
        builder.HasIndex(row => new { row.Status, row.ExpiresAt }).HasDatabaseName("ix_game_package_ingestions_status_expiry");
        builder.HasIndex(row => new { row.OwnerUserId, row.CreatedAt }).HasDatabaseName("ix_game_package_ingestions_owner_created");
        builder.HasOne(row => row.Owner).WithMany().HasForeignKey(row => row.OwnerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_game_package_ingestions_owner");
    }

    private static string DigestExpression(string column) =>
        $"{column} IS NULL OR (length({column}) = 71 AND substr({column}, 1, 7) = 'sha256:' AND lower({column}) = {column} AND substr({column}, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr({column}, 8)) = 64)";

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

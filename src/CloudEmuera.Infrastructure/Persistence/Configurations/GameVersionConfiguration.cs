using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class GameVersionConfiguration : IEntityTypeConfiguration<GameVersionRow>
{
    public void Configure(EntityTypeBuilder<GameVersionRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.GameVersionsTable, table =>
        {
            table.HasCheckConstraint("ck_game_versions_id", SqliteCheckExpressions.IdentifierPrefix("id", "gver_"));
            table.HasCheckConstraint("ck_game_versions_game_id", SqliteCheckExpressions.IdentifierPrefix("game_id", "game_"));
            table.HasCheckConstraint("ck_game_versions_version_label", "length(version_label) BETWEEN 1 AND 100 AND instr(version_label, char(0)) = 0");
            table.HasCheckConstraint("ck_game_versions_status", "status IN ('DRAFT', 'VALIDATING', 'PUBLISHED', 'BLOCKED', 'DELETED')");
            table.HasCheckConstraint("ck_game_versions_digest", SqliteCheckExpressions.Sha256Digest);
            table.HasCheckConstraint("ck_game_versions_content_path", SqliteCheckExpressions.RelativePath("content_path"));
            table.HasCheckConstraint("ck_game_versions_manifest_json", SqliteCheckExpressions.Json.Replace("{0}", "manifest_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_game_versions_runtime_config_json", SqliteCheckExpressions.Json.Replace("{0}", "runtime_config_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_game_versions_compatibility_json", SqliteCheckExpressions.Json.Replace("{0}", "compatibility_summary_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_game_versions_created_by", SqliteCheckExpressions.IdentifierPrefix("created_by", "usr_"));
            table.HasCheckConstraint("ck_game_versions_published_fields", "status NOT IN ('PUBLISHED', 'BLOCKED') OR (content_digest IS NOT NULL AND published_at IS NOT NULL)");
            table.HasCheckConstraint("ck_game_versions_time_order", "created_at >= 0 AND (published_at IS NULL OR published_at >= created_at)");
            table.HasCheckConstraint("ck_game_versions_state_version", SqliteCheckExpressions.NonNegativeCounters);
        });

        builder.HasKey(row => row.Id).HasName("pk_game_versions");
        builder.HasAlternateKey(row => new { row.Id, row.GameId }).HasName("ak_game_versions_id_game_id");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.GameId).HasColumnName("game_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.VersionLabel).HasColumnName("version_label").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.VersionLabelMaxLength).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameVersionStatus>(), SqliteValueConverters.CreateEnumComparer<GameVersionStatus>()).IsRequired();
        builder.Property(row => row.ContentDigest).HasColumnName("content_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.ContentPath).HasColumnName("content_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength).IsRequired();
        builder.Property(row => row.ManifestJson).HasColumnName("manifest_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.RuntimeConfigJson).HasColumnName("runtime_config_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.CompatibilitySummaryJson).HasColumnName("compatibility_summary_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.CreatedBy).HasColumnName("created_by").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        builder.Property(row => row.PublishedAt).HasColumnName("published_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();

        builder.HasIndex(row => new { row.GameId, row.VersionLabel }).IsUnique().HasDatabaseName("ux_game_versions_game_label");
        builder.HasIndex(row => row.ContentDigest).IsUnique().HasDatabaseName("ux_game_versions_content_digest").HasFilter("content_digest IS NOT NULL");
        builder.HasIndex(row => row.ContentPath).IsUnique().HasDatabaseName("ux_game_versions_content_path");
        builder.HasIndex(row => row.CreatedBy).HasDatabaseName("ix_game_versions_created_by");
        builder.HasOne(row => row.Game).WithMany(game => game.Versions).HasForeignKey(row => row.GameId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_game_versions_game");
        builder.HasOne(row => row.Creator).WithMany().HasForeignKey(row => row.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_game_versions_creator");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

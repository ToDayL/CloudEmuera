using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class GameConfiguration : IEntityTypeConfiguration<GameRow>
{
    public void Configure(EntityTypeBuilder<GameRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.GamesTable, table =>
        {
            table.HasCheckConstraint("ck_games_id", SqliteCheckExpressions.IdentifierPrefix("id", "game_"));
            table.HasCheckConstraint("ck_games_owner_id", SqliteCheckExpressions.IdentifierPrefix("owner_user_id", "usr_"));
            table.HasCheckConstraint("ck_games_name", "length(name) BETWEEN 1 AND 200 AND instr(name, char(0)) = 0");
            table.HasCheckConstraint("ck_games_visibility", "visibility IN ('PRIVATE', 'SERVER_SHARED')");
            table.HasCheckConstraint("ck_games_status", "status IN ('ACTIVE', 'BLOCKED', 'DELETED')");
            table.HasCheckConstraint("ck_games_workspace_status", "workspace_status IN ('NONE', 'DRAFT', 'VALIDATING')");
            table.HasCheckConstraint("ck_games_workspace", "(workspace_status = 'NONE' AND workspace_path IS NULL) OR (workspace_status <> 'NONE' AND workspace_path IS NOT NULL)");
            table.HasCheckConstraint("ck_games_workspace_path", $"workspace_path IS NULL OR ({SqliteCheckExpressions.RelativePath("workspace_path")})");
            table.HasCheckConstraint("ck_games_content_path", $"current_content_path IS NULL OR ({SqliteCheckExpressions.RelativePath("current_content_path")})");
            table.HasCheckConstraint("ck_games_content", "(current_content_path IS NULL AND content_digest IS NULL AND content_revision = 0 AND activated_by IS NULL AND activated_at IS NULL) OR (current_content_path IS NOT NULL AND content_revision > 0 AND activated_by IS NOT NULL AND activated_at IS NOT NULL)");
            table.HasCheckConstraint("ck_games_deleted_fields", "(status = 'DELETED' AND deleted_by IS NOT NULL AND deleted_at IS NOT NULL) OR (status <> 'DELETED' AND deleted_by IS NULL AND deleted_at IS NULL)");
            table.HasCheckConstraint("ck_games_manifest_json", SqliteCheckExpressions.ValidJson("manifest_json"));
            table.HasCheckConstraint("ck_games_runtime_config_json", SqliteCheckExpressions.ValidJson("runtime_config_json"));
            table.HasCheckConstraint("ck_games_compatibility_json", SqliteCheckExpressions.ValidJson("compatibility_summary_json"));
            table.HasCheckConstraint("ck_games_time_order", "created_at >= 0 AND updated_at >= created_at");
            table.HasCheckConstraint("ck_games_state_version", "state_version >= 0 AND content_revision >= 0");
        });

        builder.HasKey(row => row.Id).HasName("pk_games");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.OwnerUserId).HasColumnName("owner_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Name).HasColumnName("name").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.NameMaxLength).IsRequired();
        builder.Property(row => row.Visibility).HasColumnName("visibility").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameVisibility>(), SqliteValueConverters.CreateEnumComparer<GameVisibility>()).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameStatus>(), SqliteValueConverters.CreateEnumComparer<GameStatus>()).IsRequired();
        builder.Property(row => row.WorkspaceStatus).HasColumnName("workspace_status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameWorkspaceStatus>(), SqliteValueConverters.CreateEnumComparer<GameWorkspaceStatus>()).IsRequired();
        builder.Property(row => row.WorkspacePath).HasColumnName("workspace_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.CurrentContentPath).HasColumnName("current_content_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.ContentDigest).HasColumnName("content_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.ContentRevision).HasColumnName("content_revision").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();
        builder.Property(row => row.ManifestJson).HasColumnName("manifest_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.RuntimeConfigJson).HasColumnName("runtime_config_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.CompatibilitySummaryJson).HasColumnName("compatibility_summary_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.ActivatedBy).HasColumnName("activated_by").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.ActivatedAt).HasColumnName("activated_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.DeletedBy).HasColumnName("deleted_by").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.DeletedAt).HasColumnName("deleted_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsConcurrencyToken().IsRequired();

        // A deleted game is a recoverable tombstone (GAME-010); its name must not
        // stay reserved forever, so the uniqueness filter excludes DELETED rows.
        builder.HasIndex(row => new { row.OwnerUserId, row.Name }).IsUnique()
            .HasDatabaseName("ux_games_owner_name")
            .HasFilter("status != 'DELETED'");
        builder.HasIndex(row => row.WorkspacePath).IsUnique().HasDatabaseName("ux_games_workspace_path");
        builder.HasIndex(row => row.CurrentContentPath).IsUnique().HasDatabaseName("ux_games_current_content_path");
        builder.HasOne(row => row.OwnerUser).WithMany().HasForeignKey(row => row.OwnerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_games_owner_user");
        builder.HasOne<CloudEmueraUser>().WithMany().HasForeignKey(row => row.ActivatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_games_activated_by");
        builder.HasOne<CloudEmueraUser>().WithMany().HasForeignKey(row => row.DeletedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_games_deleted_by");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

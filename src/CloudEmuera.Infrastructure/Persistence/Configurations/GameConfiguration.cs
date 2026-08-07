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
            table.HasCheckConstraint("ck_games_status", "status IN ('ACTIVE', 'DELETED')");
            table.HasCheckConstraint("ck_games_time_order", "created_at >= 0 AND updated_at >= created_at");
            table.HasCheckConstraint("ck_games_state_version", SqliteCheckExpressions.NonNegativeCounters);
        });

        builder.HasKey(row => row.Id).HasName("pk_games");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.OwnerUserId).HasColumnName("owner_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Name).HasColumnName("name").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.NameMaxLength).IsRequired();
        builder.Property(row => row.Visibility).HasColumnName("visibility").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameVisibility>(), SqliteValueConverters.CreateEnumComparer<GameVisibility>()).IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<GameStatus>(), SqliteValueConverters.CreateEnumComparer<GameStatus>()).IsRequired();
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();

        builder.HasIndex(row => new { row.OwnerUserId, row.Name }).IsUnique().HasDatabaseName("ux_games_owner_name");
        builder.HasOne(row => row.OwnerUser).WithMany().HasForeignKey(row => row.OwnerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_games_owner_user");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

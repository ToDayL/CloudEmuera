using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class QuotaProfileConfiguration : IEntityTypeConfiguration<QuotaProfileRow>
{
    public void Configure(EntityTypeBuilder<QuotaProfileRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.QuotaProfilesTable, table =>
        {
            table.HasCheckConstraint(
                "ck_quota_profiles_id",
                $"{SqliteCheckExpressions.IdentifierPrefix("id", "qtp_")}");
            table.HasCheckConstraint("ck_quota_profiles_name", "length(name) BETWEEN 1 AND 200 AND instr(name, char(0)) = 0");
            table.HasCheckConstraint("ck_quota_profiles_limits_positive", "max_active_sessions > 0 AND max_game_package_bytes > 0 AND max_session_bytes > 0 AND max_output_bytes_per_second > 0");
            table.HasCheckConstraint("ck_quota_profiles_time_order", "created_at >= 0 AND updated_at >= created_at");
            table.HasCheckConstraint("ck_quota_profiles_state_version", "state_version >= 0");
        });

        builder.HasKey(row => row.Id).HasName("pk_quota_profiles");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Name).HasColumnName("name").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.NameMaxLength).IsRequired();
        builder.Property(row => row.MaxActiveSessions).HasColumnName("max_active_sessions").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.MaxGamePackageBytes).HasColumnName("max_game_package_bytes").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.MaxSessionBytes).HasColumnName("max_session_bytes").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.MaxOutputBytesPerSecond).HasColumnName("max_output_bytes_per_second").HasColumnType("INTEGER").IsRequired();
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.UpdatedAt), "updated_at");
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();
        builder.HasIndex(row => row.Name).IsUnique().HasDatabaseName("ux_quota_profiles_name");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

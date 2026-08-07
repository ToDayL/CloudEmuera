using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class CloudEmueraUserConfiguration : IEntityTypeConfiguration<CloudEmueraUser>
{
    public void Configure(EntityTypeBuilder<CloudEmueraUser> builder)
    {
        builder.ToTable(SqliteStorageConventions.UsersTable, table =>
        {
            table.HasCheckConstraint("ck_users_id", SqliteCheckExpressions.IdentifierPrefix("id", "usr_"));
            table.HasCheckConstraint("ck_users_login_names", "length(login_name) BETWEEN 1 AND 128 AND length(normalized_login_name) BETWEEN 1 AND 128 AND instr(login_name, char(0)) = 0 AND instr(normalized_login_name, char(0)) = 0");
            table.HasCheckConstraint("ck_users_role", "role IN ('PLAYER', 'ADMIN')");
            table.HasCheckConstraint("ck_users_status", "status IN ('ACTIVE', 'DISABLED')");
            table.HasCheckConstraint("ck_users_access_failed_count", "access_failed_count >= 0");
            table.HasCheckConstraint("ck_users_string_lengths", "(password_hash IS NULL OR length(password_hash) BETWEEN 1 AND 512) AND length(security_stamp) BETWEEN 1 AND 128 AND instr(security_stamp, char(0)) = 0");
            table.HasCheckConstraint("ck_users_preferences_json", SqliteCheckExpressions.Json.Replace("{0}", "preferences_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_users_time_order", "created_at >= 0 AND updated_at >= created_at AND (lockout_end IS NULL OR lockout_end >= 0)");
            table.HasCheckConstraint("ck_users_state_version", SqliteCheckExpressions.NonNegativeCounters);
        });

        builder.HasKey(user => user.Id).HasName("pk_users");
        builder.Property(user => user.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(user => user.LoginName).HasColumnName("login_name").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.LoginNameMaxLength).IsRequired();
        builder.Property(user => user.NormalizedLoginName).HasColumnName("normalized_login_name").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.LoginNameMaxLength).IsRequired();
        builder.Property(user => user.PasswordHash).HasColumnName("password_hash").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PasswordHashMaxLength);
        builder.Property(user => user.SecurityStamp).HasColumnName("security_stamp").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.SecurityStampMaxLength).IsRequired();
        builder.Property(user => user.Role).HasColumnName("role").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<UserRole>(), SqliteValueConverters.CreateEnumComparer<UserRole>()).IsRequired();
        builder.Property(user => user.Status).HasColumnName("status").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<UserStatus>(), SqliteValueConverters.CreateEnumComparer<UserStatus>()).IsRequired();
        builder.Property(user => user.AccessFailedCount).HasColumnName("access_failed_count").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();
        builder.Property(user => user.LockoutEnd).HasColumnName("lockout_end").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(user => user.QuotaProfileId).HasColumnName("quota_profile_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(user => user.PreferencesJson).HasColumnName("preferences_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        ConfigureTime(builder.Property(user => user.CreatedAt), "created_at");
        ConfigureTime(builder.Property(user => user.UpdatedAt), "updated_at");
        builder.Property(user => user.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();

        IgnoreIdentityProperty(builder, user => user.UserName);
        IgnoreIdentityProperty(builder, user => user.NormalizedUserName);
        IgnoreIdentityProperty(builder, user => user.Email);
        IgnoreIdentityProperty(builder, user => user.NormalizedEmail);
        IgnoreIdentityProperty(builder, user => user.EmailConfirmed);
        IgnoreIdentityProperty(builder, user => user.ConcurrencyStamp);
        IgnoreIdentityProperty(builder, user => user.PhoneNumber);
        IgnoreIdentityProperty(builder, user => user.PhoneNumberConfirmed);
        IgnoreIdentityProperty(builder, user => user.TwoFactorEnabled);
        IgnoreIdentityProperty(builder, user => user.LockoutEnabled);

        builder.HasIndex(user => user.NormalizedLoginName).IsUnique().HasDatabaseName("ux_users_normalized_login_name");
        builder.HasIndex(user => user.QuotaProfileId).HasDatabaseName("ix_users_quota_profile");
        builder.HasOne(user => user.QuotaProfile).WithMany(profile => profile.Users).HasForeignKey(user => user.QuotaProfileId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_users_quota_profiles");
    }

    private static void IgnoreIdentityProperty(EntityTypeBuilder<CloudEmueraUser> builder, System.Linq.Expressions.Expression<Func<CloudEmueraUser, object?>> property) =>
        builder.Ignore(property);

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSessionRow>
{
    public void Configure(EntityTypeBuilder<AuthSessionRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.AuthSessionsTable, table =>
        {
            table.HasCheckConstraint("ck_auth_sessions_id", SqliteCheckExpressions.IdentifierPrefix("id", "auths_"));
            table.HasCheckConstraint("ck_auth_sessions_times", "created_at >= 0 AND created_at <= last_seen_at AND last_seen_at <= idle_expires_at AND idle_expires_at <= absolute_expires_at");
            table.HasCheckConstraint("ck_auth_sessions_revocation", "(revoked_at IS NULL AND revoke_reason IS NULL) OR (revoked_at IS NOT NULL AND revoke_reason IS NOT NULL AND revoked_at >= created_at)");
        });
        builder.HasKey(row => row.Id).HasName("pk_auth_sessions");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.UserId).HasColumnName("user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.SecurityStamp).HasColumnName("security_stamp").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.SecurityStampMaxLength).IsRequired();
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        ConfigureTime(builder.Property(row => row.LastSeenAt), "last_seen_at");
        ConfigureTime(builder.Property(row => row.IdleExpiresAt), "idle_expires_at");
        ConfigureTime(builder.Property(row => row.AbsoluteExpiresAt), "absolute_expires_at");
        builder.Property(row => row.RevokedAt).HasColumnName("revoked_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.RevokeReason).HasColumnName("revoke_reason").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ReasonCodeMaxLength);
        builder.Property(row => row.IsPersistent).HasColumnName("is_persistent").HasColumnType("INTEGER");
        builder.HasIndex(row => new { row.UserId, row.RevokedAt, row.AbsoluteExpiresAt }).HasDatabaseName("ix_auth_sessions_user_active");
        builder.HasIndex(row => row.IdleExpiresAt).HasDatabaseName("ix_auth_sessions_idle_expiry");
        builder.HasOne(row => row.User).WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_auth_sessions_user");
    }
    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) => property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

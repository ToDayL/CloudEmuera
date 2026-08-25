using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class GameContentCopyLeaseConfiguration : IEntityTypeConfiguration<GameContentCopyLeaseRow>
{
    public void Configure(EntityTypeBuilder<GameContentCopyLeaseRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.GameContentCopyLeasesTable, table =>
        {
            table.HasCheckConstraint("ck_game_content_copy_leases_id", SqliteCheckExpressions.IdentifierPrefix("id", "gcl_"));
            table.HasCheckConstraint("ck_game_content_copy_leases_game_id", SqliteCheckExpressions.IdentifierPrefix("game_id", "game_"));
            table.HasCheckConstraint("ck_game_content_copy_leases_revision", "content_revision > 0");
            table.HasCheckConstraint("ck_game_content_copy_leases_digest", "content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
            table.HasCheckConstraint("ck_game_content_copy_leases_source_path", $"source_content_path IS NULL OR ({SqliteCheckExpressions.RelativePath("source_content_path")})");
            table.HasCheckConstraint("ck_game_content_copy_leases_consumer", "consumer_type IN ('SESSION_CREATE', 'VALIDATION') AND length(consumer_id) BETWEEN 1 AND 64 AND instr(consumer_id, char(0)) = 0");
            table.HasCheckConstraint("ck_game_content_copy_leases_time", "created_at >= 0 AND expires_at > created_at");
        });
        builder.HasKey(row => row.Id).HasName("pk_game_content_copy_leases");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.GameId).HasColumnName("game_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.ContentRevision).HasColumnName("content_revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.ContentDigest).HasColumnName("content_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.SourceContentPath).HasColumnName("source_content_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.ConsumerType).HasColumnName("consumer_type").HasColumnType("TEXT").HasMaxLength(32).IsRequired();
        builder.Property(row => row.ConsumerId).HasColumnName("consumer_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        ConfigureTime(builder.Property(row => row.ExpiresAt), "expires_at");
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        builder.HasIndex(row => new { row.ConsumerType, row.ConsumerId }).IsUnique().HasDatabaseName("ux_game_content_copy_leases_consumer");
        builder.HasIndex(row => new { row.GameId, row.ContentRevision, row.ExpiresAt }).HasDatabaseName("ix_game_content_copy_leases_content");
        builder.HasOne(row => row.Game).WithMany().HasForeignKey(row => row.GameId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_game_content_copy_leases_game");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}

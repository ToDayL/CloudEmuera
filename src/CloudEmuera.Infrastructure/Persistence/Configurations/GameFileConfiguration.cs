using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class GameFileConfiguration : IEntityTypeConfiguration<GameFileRow>
{
    public void Configure(EntityTypeBuilder<GameFileRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.GameFilesTable, table =>
        {
            table.HasCheckConstraint("ck_game_files_game_id", SqliteCheckExpressions.IdentifierPrefix("game_id", "game_"));
            table.HasCheckConstraint("ck_game_files_scope", "scope IN ('WORKSPACE', 'CURRENT')");
            table.HasCheckConstraint("ck_game_files_path", SqliteCheckExpressions.RelativePath("logical_path"));
            table.HasCheckConstraint("ck_game_files_kind", "entry_kind IN ('FILE', 'DIRECTORY')");
            table.HasCheckConstraint("ck_game_files_length", "byte_length >= 0 AND (entry_kind = 'FILE' OR byte_length = 0)");
            table.HasCheckConstraint("ck_game_files_digest", "entry_kind = 'DIRECTORY' OR content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
            table.HasCheckConstraint("ck_game_files_file_metadata", "(entry_kind = 'DIRECTORY' AND file_kind IS NULL AND text_encoding IS NULL AND has_bom IS NULL) OR (entry_kind = 'FILE' AND file_kind IN ('TEXT', 'BINARY') AND ((file_kind = 'BINARY' AND text_encoding IS NULL AND has_bom IS NULL) OR (file_kind = 'TEXT' AND text_encoding IN ('UTF8', 'UTF8_BOM', 'SHIFT_JIS', 'UNKNOWN') AND has_bom IN (0, 1))))");
        });
        builder.HasKey(row => new { row.GameId, row.Scope, row.LogicalPath }).HasName("pk_game_files");
        builder.Property(row => row.GameId).HasColumnName("game_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.Scope).HasColumnName("scope").HasColumnType("TEXT").HasMaxLength(16).IsRequired();
        builder.Property(row => row.LogicalPath).HasColumnName("logical_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength).IsRequired();
        builder.Property(row => row.EntryKind).HasColumnName("entry_kind").HasColumnType("TEXT").HasMaxLength(16).IsRequired();
        builder.Property(row => row.ByteLength).HasColumnName("byte_length").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.ContentDigest).HasColumnName("content_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength);
        builder.Property(row => row.FileKind).HasColumnName("file_kind").HasColumnType("TEXT").HasMaxLength(16);
        builder.Property(row => row.TextEncoding).HasColumnName("text_encoding").HasColumnType("TEXT").HasMaxLength(16);
        builder.Property(row => row.HasBom).HasColumnName("has_bom").HasColumnType("INTEGER");
        builder.HasIndex(row => new { row.GameId, row.Scope, row.EntryKind }).HasDatabaseName("ix_game_files_scope_kind");
        builder.HasOne(row => row.Game).WithMany().HasForeignKey(row => row.GameId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_game_files_game");
    }
}

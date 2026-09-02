using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

/// <summary>
/// Makes Game path/revision identity the normal content identity while keeping
/// old SHA-256 metadata readable. This migration only changes SQLite metadata;
/// it deliberately never walks a Game directory or calculates a digest.
/// </summary>
[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260825100000_AddPathRevisionContentIdentity")]
public partial class AddPathRevisionContentIdentity : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeFontSizeLineHeightModeSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.LegacyRuntimeWidthSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_games_content", "games");
        migrationBuilder.DropCheckConstraint("ck_game_files_digest", "game_files");
        migrationBuilder.DropCheckConstraint("ck_game_content_copy_leases_digest", "game_content_copy_leases");
        migrationBuilder.DropCheckConstraint("ck_sessions_source_digest", "sessions");
        migrationBuilder.DropCheckConstraint("ck_sessions_manifest_digest", "sessions");

        migrationBuilder.AddColumn<string>(
            name: "source_content_path",
            table: "game_content_copy_leases",
            type: "TEXT",
            maxLength: PersistenceLimits.PathMaxLength,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "session_identity_mode",
            table: "sessions",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "LEGACY_DIGEST");

        migrationBuilder.AddColumn<string>(
            name: "session_snapshot_id",
            table: "sessions",
            type: "TEXT",
            maxLength: PersistenceLimits.IdMaxLength,
            nullable: true);

        // Existing sessions retain their persistent identity. The snapshot id
        // is metadata only and is initialized from the already stable SessionId.
        migrationBuilder.Sql("UPDATE sessions SET session_snapshot_id = id WHERE session_snapshot_id IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "content_digest",
            table: "game_content_copy_leases",
            type: "TEXT",
            maxLength: PersistenceLimits.DigestLength,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: PersistenceLimits.DigestLength);

        migrationBuilder.AlterColumn<string>(
            name: "source_content_digest",
            table: "sessions",
            type: "TEXT",
            maxLength: PersistenceLimits.DigestLength,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: PersistenceLimits.DigestLength);

        migrationBuilder.AlterColumn<string>(
            name: "session_root_manifest_digest",
            table: "sessions",
            type: "TEXT",
            maxLength: PersistenceLimits.SessionRootManifestDigestMaxLength,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: PersistenceLimits.SessionRootManifestDigestMaxLength);

        migrationBuilder.AlterColumn<string>(
            name: "session_snapshot_id",
            table: "sessions",
            type: "TEXT",
            maxLength: PersistenceLimits.IdMaxLength,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: PersistenceLimits.IdMaxLength,
            oldNullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_games_content",
            table: "games",
            sql: "(current_content_path IS NULL AND content_digest IS NULL AND content_revision = 0 AND activated_by IS NULL AND activated_at IS NULL) OR (current_content_path IS NOT NULL AND content_revision > 0 AND activated_by IS NOT NULL AND activated_at IS NOT NULL)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_game_files_digest",
            table: "game_files",
            sql: "entry_kind = 'DIRECTORY' OR content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_game_content_copy_leases_digest",
            table: "game_content_copy_leases",
            sql: "content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_game_content_copy_leases_source_path",
            table: "game_content_copy_leases",
            sql: "source_content_path IS NULL OR (length(source_content_path) BETWEEN 1 AND 512 AND substr(source_content_path, 1, 1) <> '/' AND instr(source_content_path, char(92)) = 0 AND instr(source_content_path, char(0)) = 0 AND instr(source_content_path, '//') = 0 AND instr('/' || source_content_path || '/', '/./') = 0 AND instr('/' || source_content_path || '/', '/../') = 0)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_source_digest",
            table: "sessions",
            sql: "source_content_digest IS NULL OR (length(source_content_digest) = 71 AND substr(source_content_digest, 1, 7) = 'sha256:' AND lower(source_content_digest) = source_content_digest AND substr(source_content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(source_content_digest, 8)) = 64)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_identity_mode",
            table: "sessions",
            sql: "session_identity_mode IN ('LEGACY_DIGEST', 'PATH_REVISION')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_snapshot_id",
            table: "sessions",
            sql: "length(session_snapshot_id) BETWEEN 1 AND 128 AND instr(session_snapshot_id, char(0)) = 0");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_manifest_digest",
            table: "sessions",
            sql: "session_root_manifest_digest IS NULL OR (length(session_root_manifest_digest) BETWEEN 1 AND 128 AND instr(session_root_manifest_digest, char(0)) = 0)");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Path/revision identity is a one-way compatibility migration; restore a database backup to downgrade.");
}

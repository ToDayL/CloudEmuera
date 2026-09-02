using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260817130000_RemoveSessionRuntimeManifestJson")]
public partial class RemoveSessionRuntimeManifestJson : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeFontSizeLineHeightModeSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludePathRevisionIdentitySchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeBackslashToYenSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeFontFaceSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeWidthSchemaAnnotation, true);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
        modelBuilder.Entity<SessionRow>().Ignore(row => row.FontSize).Ignore(row => row.LineHeight).Ignore(row => row.FontFaceId);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "session_root_manifest_digest",
            table: "sessions",
            type: "TEXT",
            maxLength: PersistenceLimits.SessionRootManifestDigestMaxLength,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "save_layout",
            table: "sessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.Sql("""
            UPDATE sessions
            SET session_root_manifest_digest = COALESCE(
                    NULLIF(json_extract(runtime_manifest_json, '$.sourceManifestDigest'), ''),
                    NULLIF(json_extract(runtime_manifest_json, '$.manifestDigest'), ''),
                    source_content_digest),
                save_layout = CASE
                    WHEN json_extract(runtime_manifest_json, '$.saveLayout') IN (0, 1)
                        THEN CAST(json_extract(runtime_manifest_json, '$.saveLayout') AS INTEGER)
                    ELSE 0
                END;
            """);

        migrationBuilder.Sql("UPDATE sessions SET state = 'RUNNING', state_version = state_version + 1 WHERE state = 'DETACHED';");

        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_runtime_manifest_json",
            table: "sessions");
        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_state",
            table: "sessions");
        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_closed_fields",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "runtime_manifest_json",
            table: "sessions");

        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_manifest_digest",
            table: "sessions",
            sql: "length(session_root_manifest_digest) BETWEEN 1 AND 128 AND instr(session_root_manifest_digest, char(0)) = 0");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_save_layout",
            table: "sessions",
            sql: "save_layout IN (0, 1)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_state",
            table: "sessions",
            sql: "state IN ('CREATING', 'STARTING', 'RUNNING', 'STOPPING', 'CLOSED', 'CRASHED')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_closed_fields",
            table: "sessions",
            sql: "((state IN ('CLOSED', 'CRASHED') AND closed_at IS NOT NULL) OR (state NOT IN ('CLOSED', 'CRASHED') AND closed_at IS NULL)) AND ((state IN ('CREATING', 'CLOSED', 'CRASHED') AND waiting_for_input = 0 AND current_prompt_id IS NULL) OR state NOT IN ('CREATING', 'CLOSED', 'CRASHED'))");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "runtime_manifest_json",
            table: "sessions",
            type: "TEXT",
            maxLength: 16_777_216,
            nullable: false,
            defaultValue: "{}");

        migrationBuilder.Sql("""
            UPDATE sessions
            SET runtime_manifest_json = json_object(
                'schemaVersion', 1,
                'compatibilityProfile', 'v18-compatible',
                'saveLayout', save_layout,
                'sourceManifestDigest', session_root_manifest_digest,
                'entries', json('[]'));
            """);

        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_manifest_digest",
            table: "sessions");
        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_save_layout",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "session_root_manifest_digest",
            table: "sessions");
        migrationBuilder.DropColumn(
            name: "save_layout",
            table: "sessions");

        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_runtime_manifest_json",
            table: "sessions",
            sql: "length(runtime_manifest_json) BETWEEN 2 AND 16777216 AND json_valid(runtime_manifest_json) = 1 AND runtime_manifest_json <> ''");
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

/// <summary>
/// Collapses the legacy version resource into one editable workspace and one current content tree per Game.
/// This migration is intentionally one-way because recreating version history would fabricate data.
/// </summary>
public partial class CollapseGameVersionsIntoGames : Migration
{
    private static readonly string[] SessionContentRevisionColumns = ["game_id", "source_content_revision"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS __cloudemuera_game_collapse_selection (
                game_id TEXT PRIMARY KEY NOT NULL,
                current_version_id TEXT NULL,
                workspace_version_id TEXT NULL
            );
            """,
            suppressTransaction: true);

        migrationBuilder.Sql(
            """
            CREATE TEMP TABLE __game_collapse_guard (
                invalid INTEGER NOT NULL CHECK (invalid = 0)
            );
            INSERT INTO __game_collapse_guard (invalid)
            SELECT 1
            WHERE EXISTS (
                SELECT 1 FROM game_versions
                GROUP BY game_id
                HAVING COUNT(DISTINCT CASE WHEN status IN ('PUBLISHED', 'BLOCKED') THEN COALESCE(content_digest, '') END) > 1
                    OR COUNT(DISTINCT CASE WHEN status IN ('DRAFT', 'VALIDATING') THEN COALESCE(content_digest, '') END) > 1
            ) OR EXISTS (
                SELECT 1
                FROM sessions AS s
                JOIN game_versions AS v ON v.id = s.game_version_id AND v.game_id = s.game_id
                WHERE v.content_digest IS NULL
            ) OR EXISTS (
                SELECT 1
                FROM __cloudemuera_game_collapse_selection AS selected
                LEFT JOIN game_versions AS current_version ON current_version.id = selected.current_version_id
                    AND current_version.game_id = selected.game_id
                LEFT JOIN game_versions AS workspace_version ON workspace_version.id = selected.workspace_version_id
                    AND workspace_version.game_id = selected.game_id
                WHERE (selected.current_version_id IS NOT NULL AND (current_version.id IS NULL OR current_version.status NOT IN ('PUBLISHED', 'BLOCKED')))
                    OR (selected.workspace_version_id IS NOT NULL AND (workspace_version.id IS NULL OR workspace_version.status NOT IN ('DRAFT', 'VALIDATING')))
            );
            DROP TABLE __game_collapse_guard;
            """);

        migrationBuilder.Sql(
            """
            INSERT OR IGNORE INTO __cloudemuera_game_collapse_selection (game_id, current_version_id, workspace_version_id)
            SELECT g.id,
                (SELECT v.id FROM game_versions AS v WHERE v.game_id = g.id AND v.status IN ('PUBLISHED', 'BLOCKED') ORDER BY COALESCE(v.published_at, v.created_at) DESC, v.created_at DESC, v.id DESC LIMIT 1),
                (SELECT v.id FROM game_versions AS v WHERE v.game_id = g.id AND v.status IN ('DRAFT', 'VALIDATING') ORDER BY v.created_at DESC, v.id DESC LIMIT 1)
            FROM games AS g;
            """);

        migrationBuilder.DropForeignKey("fk_sessions_game_version_game", "sessions");
        migrationBuilder.DropIndex("ix_sessions_game_version", "sessions");
        migrationBuilder.DropIndex("ix_sessions_game_version_game", "sessions");
        migrationBuilder.DropCheckConstraint("ck_sessions_game_version_id", "sessions");
        migrationBuilder.DropCheckConstraint("ck_games_state_version", "games");
        migrationBuilder.DropCheckConstraint("ck_games_status", "games");

        migrationBuilder.AddColumn<string>("runtime_manifest_json", "sessions", "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>("source_content_digest", "sessions", "TEXT", maxLength: 71, nullable: false, defaultValue: "sha256:0000000000000000000000000000000000000000000000000000000000000000");
        migrationBuilder.AddColumn<long>("source_content_revision", "sessions", "INTEGER", nullable: false, defaultValue: 1L);

        migrationBuilder.AddColumn<long>("activated_at", "games", "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>("activated_by", "games", "TEXT", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("compatibility_summary_json", "games", "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>("content_digest", "games", "TEXT", maxLength: 71, nullable: true);
        migrationBuilder.AddColumn<long>("content_revision", "games", "INTEGER", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<string>("current_content_path", "games", "TEXT", maxLength: 512, nullable: true);
        migrationBuilder.AddColumn<string>("manifest_json", "games", "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>("runtime_config_json", "games", "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>("workspace_path", "games", "TEXT", maxLength: 512, nullable: true);
        migrationBuilder.AddColumn<string>("workspace_status", "games", "TEXT", nullable: false, defaultValue: "NONE");

        migrationBuilder.Sql(
            """
            UPDATE games
            SET workspace_status = COALESCE((
                    SELECT v.status FROM game_versions AS v
                    WHERE v.id = (SELECT workspace_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)
                ), 'NONE'),
                workspace_path = CASE WHEN EXISTS (
                    SELECT 1 FROM game_versions AS v
                    WHERE v.id = (SELECT workspace_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)
                ) THEN 'games/' || games.id || '/workspace' ELSE NULL END;

            UPDATE games
            SET status = CASE
                    WHEN status = 'DELETED' THEN 'DELETED'
                    WHEN (SELECT v.status FROM game_versions AS v
                          WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)) = 'BLOCKED'
                        THEN 'BLOCKED'
                    ELSE 'ACTIVE'
                END,
                current_content_path = CASE WHEN EXISTS (SELECT 1 FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id))
                    THEN 'games/' || games.id || '/content' ELSE NULL END,
                content_digest = (SELECT v.content_digest FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)),
                content_revision = CASE WHEN EXISTS (SELECT 1 FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)) THEN 1 ELSE 0 END,
                manifest_json = COALESCE((SELECT v.manifest_json FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)), '{}'),
                runtime_config_json = COALESCE((SELECT v.runtime_config_json FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)), '{}'),
                compatibility_summary_json = COALESCE((SELECT v.compatibility_summary_json FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)), '{}'),
                activated_by = (SELECT v.created_by FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id)),
                activated_at = (SELECT COALESCE(v.published_at, v.created_at) FROM game_versions AS v
                    WHERE v.id = (SELECT current_version_id FROM __cloudemuera_game_collapse_selection WHERE game_id = games.id));

            UPDATE sessions
            SET source_content_digest = (SELECT v.content_digest FROM game_versions AS v
                    WHERE v.id = sessions.game_version_id AND v.game_id = sessions.game_id),
                source_content_revision = 1,
                runtime_manifest_json = COALESCE((SELECT json_object(
                        'schemaVersion', 1,
                        'contentManifest', json(v.manifest_json),
                        'runtimeConfig', json(v.runtime_config_json))
                    FROM game_versions AS v
                    WHERE v.id = sessions.game_version_id AND v.game_id = sessions.game_id), '{}');
            """);

        migrationBuilder.DropColumn("game_version_id", "sessions");
        migrationBuilder.DropTable("game_versions");
        migrationBuilder.Sql("DROP TABLE IF EXISTS __cloudemuera_game_collapse_selection;", suppressTransaction: true);

        migrationBuilder.CreateIndex("ix_sessions_game_content_revision", "sessions", SessionContentRevisionColumns);
        migrationBuilder.AddCheckConstraint("ck_sessions_runtime_manifest_json", "sessions", "length(runtime_manifest_json) BETWEEN 2 AND 1048576 AND json_valid(runtime_manifest_json) = 1 AND runtime_manifest_json <> ''");
        migrationBuilder.AddCheckConstraint("ck_sessions_source_digest", "sessions", "length(source_content_digest) = 71 AND substr(source_content_digest, 1, 7) = 'sha256:' AND lower(source_content_digest) = source_content_digest AND substr(source_content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(source_content_digest, 8)) = 64");
        migrationBuilder.AddCheckConstraint("ck_sessions_source_revision", "sessions", "source_content_revision > 0");

        migrationBuilder.CreateIndex("IX_games_activated_by", "games", "activated_by");
        migrationBuilder.CreateIndex("ux_games_current_content_path", "games", "current_content_path", unique: true);
        migrationBuilder.CreateIndex("ux_games_workspace_path", "games", "workspace_path", unique: true);
        migrationBuilder.AddCheckConstraint("ck_games_compatibility_json", "games", "length(compatibility_summary_json) BETWEEN 2 AND 1048576 AND json_valid(compatibility_summary_json) = 1 AND compatibility_summary_json <> ''");
        migrationBuilder.AddCheckConstraint("ck_games_content", "games", "(current_content_path IS NULL AND content_digest IS NULL AND content_revision = 0 AND activated_by IS NULL AND activated_at IS NULL) OR (current_content_path IS NOT NULL AND length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64 AND content_revision > 0 AND activated_by IS NOT NULL AND activated_at IS NOT NULL)");
        migrationBuilder.AddCheckConstraint("ck_games_content_path", "games", "current_content_path IS NULL OR (length(current_content_path) BETWEEN 1 AND 512 AND substr(current_content_path, 1, 1) <> '/' AND instr(current_content_path, char(92)) = 0 AND instr(current_content_path, char(0)) = 0 AND instr(current_content_path, '//') = 0 AND instr('/' || current_content_path || '/', '/./') = 0 AND instr('/' || current_content_path || '/', '/../') = 0)");
        migrationBuilder.AddCheckConstraint("ck_games_manifest_json", "games", "length(manifest_json) BETWEEN 2 AND 1048576 AND json_valid(manifest_json) = 1 AND manifest_json <> ''");
        migrationBuilder.AddCheckConstraint("ck_games_runtime_config_json", "games", "length(runtime_config_json) BETWEEN 2 AND 1048576 AND json_valid(runtime_config_json) = 1 AND runtime_config_json <> ''");
        migrationBuilder.AddCheckConstraint("ck_games_state_version", "games", "state_version >= 0 AND content_revision >= 0");
        migrationBuilder.AddCheckConstraint("ck_games_status", "games", "status IN ('ACTIVE', 'BLOCKED', 'DELETED')");
        migrationBuilder.AddCheckConstraint("ck_games_workspace", "games", "(workspace_status = 'NONE' AND workspace_path IS NULL) OR (workspace_status <> 'NONE' AND workspace_path IS NOT NULL)");
        migrationBuilder.AddCheckConstraint("ck_games_workspace_path", "games", "workspace_path IS NULL OR (length(workspace_path) BETWEEN 1 AND 512 AND substr(workspace_path, 1, 1) <> '/' AND instr(workspace_path, char(92)) = 0 AND instr(workspace_path, char(0)) = 0 AND instr(workspace_path, '//') = 0 AND instr('/' || workspace_path || '/', '/./') = 0 AND instr('/' || workspace_path || '/', '/../') = 0)");
        migrationBuilder.AddCheckConstraint("ck_games_workspace_status", "games", "workspace_status IN ('NONE', 'DRAFT', 'VALIDATING')");
        migrationBuilder.AddForeignKey("fk_games_activated_by", "games", "activated_by", "users", principalColumn: "id", onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The GameVersion collapse is irreversible because version history is intentionally discarded.");
}

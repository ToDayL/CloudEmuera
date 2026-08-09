using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameFileIndexAndDiagnostics : Migration
    {
        private static readonly string[] DiagnosticIndexColumns = ["game_id", "workspace_revision", "activation_blocking"];
        private static readonly string[] GameFileIndexColumns = ["game_id", "scope", "entry_kind"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "compatibility_diagnostics",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    game_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    workspace_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    stage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    logical_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    line_number = table.Column<int>(type: "INTEGER", nullable: true),
                    message_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    arguments_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}"),
                    activation_blocking = table.Column<bool>(type: "INTEGER", nullable: false),
                    override_policy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    overridden_by = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    overridden_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_compatibility_diagnostics", x => x.id);
                    table.CheckConstraint("ck_compatibility_diagnostics_arguments", "length(arguments_json) BETWEEN 2 AND 1048576 AND json_valid(arguments_json) = 1 AND arguments_json <> ''");
                    table.CheckConstraint("ck_compatibility_diagnostics_blocking", "activation_blocking IN (0, 1)");
                    table.CheckConstraint("ck_compatibility_diagnostics_code", "length(code) BETWEEN 1 AND 128 AND instr(code, char(0)) = 0");
                    table.CheckConstraint("ck_compatibility_diagnostics_created", "created_at >= 0");
                    table.CheckConstraint("ck_compatibility_diagnostics_game_id", "substr(game_id, 1, 5) = 'game_' AND length(game_id) BETWEEN 5 AND 64 AND instr(game_id, char(0)) = 0");
                    table.CheckConstraint("ck_compatibility_diagnostics_id", "substr(id, 1, 5) = 'diag_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_compatibility_diagnostics_line", "line_number IS NULL OR line_number > 0");
                    table.CheckConstraint("ck_compatibility_diagnostics_message", "length(message_key) BETWEEN 1 AND 256 AND instr(message_key, char(0)) = 0");
                    table.CheckConstraint("ck_compatibility_diagnostics_override", "override_policy IN ('NEVER', 'ADMIN') AND ((overridden_by IS NULL AND overridden_at IS NULL) OR (override_policy = 'ADMIN' AND overridden_by IS NOT NULL AND overridden_at IS NOT NULL))");
                    table.CheckConstraint("ck_compatibility_diagnostics_path", "logical_path IS NULL OR (length(logical_path) BETWEEN 1 AND 512 AND substr(logical_path, 1, 1) <> '/' AND instr(logical_path, char(92)) = 0 AND instr(logical_path, char(0)) = 0 AND instr(logical_path, '//') = 0 AND instr('/' || logical_path || '/', '/./') = 0 AND instr('/' || logical_path || '/', '/../') = 0)");
                    table.CheckConstraint("ck_compatibility_diagnostics_revision", "workspace_revision >= 0");
                    table.CheckConstraint("ck_compatibility_diagnostics_severity", "severity IN ('INFO', 'WARNING', 'ERROR')");
                    table.CheckConstraint("ck_compatibility_diagnostics_stage", "length(stage) BETWEEN 1 AND 32 AND instr(stage, char(0)) = 0");
                    table.ForeignKey(
                        name: "fk_compatibility_diagnostics_game",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_compatibility_diagnostics_overridden_by",
                        column: x => x.overridden_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "game_files",
                columns: table => new
                {
                    game_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    logical_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    entry_kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: false),
                    content_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    file_kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    text_encoding = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    has_bom = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_files", x => new { x.game_id, x.scope, x.logical_path });
                    table.CheckConstraint("ck_game_files_digest", "(entry_kind = 'DIRECTORY' AND content_digest IS NULL) OR (entry_kind = 'FILE' AND length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
                    table.CheckConstraint("ck_game_files_file_metadata", "(entry_kind = 'DIRECTORY' AND file_kind IS NULL AND text_encoding IS NULL AND has_bom IS NULL) OR (entry_kind = 'FILE' AND file_kind IN ('TEXT', 'BINARY') AND ((file_kind = 'BINARY' AND text_encoding IS NULL AND has_bom IS NULL) OR (file_kind = 'TEXT' AND text_encoding IN ('UTF8', 'UTF8_BOM', 'SHIFT_JIS', 'UNKNOWN') AND has_bom IN (0, 1))))");
                    table.CheckConstraint("ck_game_files_game_id", "substr(game_id, 1, 5) = 'game_' AND length(game_id) BETWEEN 5 AND 64 AND instr(game_id, char(0)) = 0");
                    table.CheckConstraint("ck_game_files_kind", "entry_kind IN ('FILE', 'DIRECTORY')");
                    table.CheckConstraint("ck_game_files_length", "byte_length >= 0 AND (entry_kind = 'FILE' OR byte_length = 0)");
                    table.CheckConstraint("ck_game_files_path", "length(logical_path) BETWEEN 1 AND 512 AND substr(logical_path, 1, 1) <> '/' AND instr(logical_path, char(92)) = 0 AND instr(logical_path, char(0)) = 0 AND instr(logical_path, '//') = 0 AND instr('/' || logical_path || '/', '/./') = 0 AND instr('/' || logical_path || '/', '/../') = 0");
                    table.CheckConstraint("ck_game_files_scope", "scope IN ('WORKSPACE', 'CURRENT')");
                    table.ForeignKey(
                        name: "fk_game_files_game",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_compatibility_diagnostics_game_revision",
                table: "compatibility_diagnostics",
                columns: DiagnosticIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_compatibility_diagnostics_overridden_by",
                table: "compatibility_diagnostics",
                column: "overridden_by");

            migrationBuilder.CreateIndex(
                name: "ix_game_files_scope_kind",
                table: "game_files",
                columns: GameFileIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compatibility_diagnostics");

            migrationBuilder.DropTable(
                name: "game_files");
        }
    }
}

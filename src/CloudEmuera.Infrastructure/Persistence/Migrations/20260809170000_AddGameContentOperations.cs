using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameContentOperations : Migration
    {
        private static readonly string[] GameCreatedColumns = ["game_id", "created_at"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_content_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    game_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    operation_type = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    expected_game_state_version = table.Column<int>(type: "INTEGER", nullable: false),
                    expected_content_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ingestion_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    work_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    content_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    lease_expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_content_operations", x => x.id);
                    table.CheckConstraint("ck_game_content_operations_completion", "(status IN ('COMMITTED', 'FAILED') AND completed_at IS NOT NULL) OR (status NOT IN ('COMMITTED', 'FAILED') AND completed_at IS NULL)");
                    table.CheckConstraint("ck_game_content_operations_digest", "content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
                    table.CheckConstraint("ck_game_content_operations_error", "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 128 AND instr(error_code, char(0)) = 0)");
                    table.CheckConstraint("ck_game_content_operations_game_id", "substr(game_id, 1, 5) = 'game_' AND length(game_id) BETWEEN 5 AND 64 AND instr(game_id, char(0)) = 0");
                    table.CheckConstraint("ck_game_content_operations_id", "substr(id, 1, 4) = 'gop_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_game_content_operations_status", "status IN ('PENDING', 'RUNNING', 'CONTENT_READY', 'COMMITTED', 'FAILED')");
                    table.CheckConstraint("ck_game_content_operations_time", "created_at >= 0 AND updated_at >= created_at AND lease_expires_at >= created_at AND (completed_at IS NULL OR completed_at >= created_at)");
                    table.CheckConstraint("ck_game_content_operations_type", "operation_type IN ('IMPORT', 'RESET_WORKSPACE', 'VALIDATE', 'ACTIVATE')");
                    table.CheckConstraint("ck_game_content_operations_versions", "expected_game_state_version >= 0 AND expected_content_revision >= 0 AND state_version >= 0");
                    table.CheckConstraint("ck_game_content_operations_work_path", "work_path IS NULL OR (length(work_path) BETWEEN 1 AND 512 AND substr(work_path, 1, 1) <> '/' AND instr(work_path, char(92)) = 0 AND instr(work_path, char(0)) = 0 AND instr(work_path, '//') = 0 AND instr('/' || work_path || '/', '/./') = 0 AND instr('/' || work_path || '/', '/../') = 0)");
                    table.ForeignKey(
                        name: "fk_game_content_operations_game",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_game_content_operations_game_created",
                table: "game_content_operations",
                columns: GameCreatedColumns);

            migrationBuilder.CreateIndex(
                name: "ux_game_content_operations_active_game",
                table: "game_content_operations",
                column: "game_id",
                unique: true,
                filter: "status IN ('PENDING', 'RUNNING', 'CONTENT_READY')");

            migrationBuilder.CreateIndex(
                name: "ux_game_content_operations_ingestion",
                table: "game_content_operations",
                column: "ingestion_id",
                unique: true,
                filter: "ingestion_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_content_operations");
        }
    }
}

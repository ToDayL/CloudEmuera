using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGamePackageIngestions : Migration
    {
        private static readonly string[] OwnerCreatedColumns = ["owner_user_id", "created_at"];
        private static readonly string[] StatusExpiryColumns = ["status", "expires_at"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_package_ingestions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    staging_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    reserved_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    archive_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    expanded_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    entry_count = table.Column<int>(type: "INTEGER", nullable: false),
                    archive_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    content_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    limits_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false),
                    summary_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    reservation_released_at = table.Column<long>(type: "INTEGER", nullable: true),
                    cleanup_completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_package_ingestions", x => x.id);
                    table.CheckConstraint("ck_game_package_ingestions_archive_digest", "archive_digest IS NULL OR (length(archive_digest) = 71 AND substr(archive_digest, 1, 7) = 'sha256:' AND lower(archive_digest) = archive_digest AND substr(archive_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(archive_digest, 8)) = 64)");
                    table.CheckConstraint("ck_game_package_ingestions_content_digest", "content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
                    table.CheckConstraint("ck_game_package_ingestions_counters", "reserved_bytes >= 0 AND archive_bytes >= 0 AND expanded_bytes >= 0 AND entry_count >= 0 AND state_version >= 0");
                    table.CheckConstraint("ck_game_package_ingestions_id", "substr(id, 1, 4) = 'ing_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_game_package_ingestions_limits_json", "length(limits_json) BETWEEN 2 AND 1048576 AND json_valid(limits_json) = 1 AND limits_json <> ''");
                    table.CheckConstraint("ck_game_package_ingestions_owner", "substr(owner_user_id, 1, 4) = 'usr_' AND length(owner_user_id) BETWEEN 5 AND 64 AND instr(owner_user_id, char(0)) = 0");
                    table.CheckConstraint("ck_game_package_ingestions_path", "length(staging_path) BETWEEN 1 AND 512 AND substr(staging_path, 1, 1) <> '/' AND instr(staging_path, char(92)) = 0 AND instr(staging_path, char(0)) = 0 AND instr(staging_path, '//') = 0 AND instr('/' || staging_path || '/', '/./') = 0 AND instr('/' || staging_path || '/', '/../') = 0");
                    table.CheckConstraint("ck_game_package_ingestions_release", "(reserved_bytes > 0 AND reservation_released_at IS NULL) OR (reserved_bytes = 0 AND reservation_released_at IS NOT NULL)");
                    table.CheckConstraint("ck_game_package_ingestions_status", "status IN ('RESERVED','RECEIVING','INSPECTING','EXTRACTING','ANALYZING','READY','CONSUMING','CONSUMED','FAILED','ABANDONED')");
                    table.CheckConstraint("ck_game_package_ingestions_summary_json", "length(summary_json) BETWEEN 2 AND 1048576 AND json_valid(summary_json) = 1 AND summary_json <> ''");
                    table.CheckConstraint("ck_game_package_ingestions_times", "created_at >= 0 AND updated_at >= created_at AND expires_at >= created_at AND (reservation_released_at IS NULL OR reservation_released_at >= created_at) AND (cleanup_completed_at IS NULL OR cleanup_completed_at >= created_at)");
                    table.ForeignKey(
                        name: "fk_game_package_ingestions_owner",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_game_package_ingestions_owner_created",
                table: "game_package_ingestions",
                columns: OwnerCreatedColumns);

            migrationBuilder.CreateIndex(
                name: "ix_game_package_ingestions_status_expiry",
                table: "game_package_ingestions",
                columns: StatusExpiryColumns);

            migrationBuilder.CreateIndex(
                name: "ux_game_package_ingestions_staging_path",
                table: "game_package_ingestions",
                column: "staging_path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_package_ingestions");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameContentCopyLeases : Migration
    {
        private static readonly string[] ContentIndexColumns = ["game_id", "content_revision", "expires_at"];
        private static readonly string[] ConsumerIndexColumns = ["consumer_type", "consumer_id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_content_copy_leases",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    game_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    content_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    content_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    consumer_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    consumer_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_content_copy_leases", x => x.id);
                    table.CheckConstraint("ck_game_content_copy_leases_consumer", "consumer_type IN ('SESSION_CREATE', 'VALIDATION') AND length(consumer_id) BETWEEN 1 AND 64 AND instr(consumer_id, char(0)) = 0");
                    table.CheckConstraint("ck_game_content_copy_leases_digest", "length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64");
                    table.CheckConstraint("ck_game_content_copy_leases_game_id", "substr(game_id, 1, 5) = 'game_' AND length(game_id) BETWEEN 5 AND 64 AND instr(game_id, char(0)) = 0");
                    table.CheckConstraint("ck_game_content_copy_leases_id", "substr(id, 1, 4) = 'gcl_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_game_content_copy_leases_revision", "content_revision > 0");
                    table.CheckConstraint("ck_game_content_copy_leases_time", "created_at >= 0 AND expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_game_content_copy_leases_game",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_game_content_copy_leases_content",
                table: "game_content_copy_leases",
                columns: ContentIndexColumns);

            migrationBuilder.CreateIndex(
                name: "ux_game_content_copy_leases_consumer",
                table: "game_content_copy_leases",
                columns: ConsumerIndexColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_content_copy_leases");
        }
    }
}

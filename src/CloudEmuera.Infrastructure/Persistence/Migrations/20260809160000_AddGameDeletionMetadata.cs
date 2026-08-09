using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameDeletionMetadata : Migration
    {
        private static readonly string[] SessionContentDigestColumns = ["game_id", "source_content_digest"];
        private static readonly string[] SessionContentRevisionColumns = ["game_id", "source_content_revision"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_game_content_revision",
                table: "sessions");

            migrationBuilder.AddColumn<long>(
                name: "deleted_at",
                table: "games",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by",
                table: "games",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("UPDATE games SET deleted_by = owner_user_id, deleted_at = updated_at WHERE status = 'DELETED';");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_game_content_digest",
                table: "sessions",
                columns: SessionContentDigestColumns);

            migrationBuilder.CreateIndex(
                name: "IX_games_deleted_by",
                table: "games",
                column: "deleted_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_games_deleted_fields",
                table: "games",
                sql: "(status = 'DELETED' AND deleted_by IS NOT NULL AND deleted_at IS NOT NULL) OR (status <> 'DELETED' AND deleted_by IS NULL AND deleted_at IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_games_deleted_by",
                table: "games",
                column: "deleted_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_games_deleted_by",
                table: "games");

            migrationBuilder.DropIndex(
                name: "ix_sessions_game_content_digest",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "IX_games_deleted_by",
                table: "games");

            migrationBuilder.DropCheckConstraint(
                name: "ck_games_deleted_fields",
                table: "games");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "games");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                table: "games");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_game_content_revision",
                table: "sessions",
                columns: SessionContentRevisionColumns);
        }
    }
}

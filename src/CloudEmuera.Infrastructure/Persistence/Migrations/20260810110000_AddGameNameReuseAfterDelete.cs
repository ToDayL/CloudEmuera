using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameNameReuseAfterDelete : Migration
    {
        private static readonly string[] OwnerNameColumns = ["owner_user_id", "name"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_games_owner_name",
                table: "games");

            migrationBuilder.CreateIndex(
                name: "ux_games_owner_name",
                table: "games",
                columns: OwnerNameColumns,
                unique: true,
                filter: "status != 'DELETED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_games_owner_name",
                table: "games");

            migrationBuilder.CreateIndex(
                name: "ux_games_owner_name",
                table: "games",
                columns: OwnerNameColumns,
                unique: true);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260825110000_AddGameUploadProgress")]
public partial class AddGameUploadProgress : Migration
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
        migrationBuilder.AddColumn<string>(
            name: "stage",
            table: "game_content_operations",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "PREPARING");

        migrationBuilder.AddColumn<string>(
            name: "current_item",
            table: "game_content_operations",
            type: "TEXT",
            maxLength: PersistenceLimits.PathMaxLength,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "request_id",
            table: "game_content_operations",
            type: "TEXT",
            maxLength: PersistenceLimits.IdempotencyKeyMaxLength,
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_game_content_operations_stage",
            table: "game_content_operations",
            sql: "stage IN ('PREPARING', 'RECEIVING', 'INSPECTING_ARCHIVE', 'EXTRACTING', 'NORMALIZING_ENCODING', 'ANALYZING', 'CONSUMING_STAGING', 'COPYING_CONTENT', 'VALIDATING_CONTENT', 'RUNNING_VALIDATOR', 'PUBLISHING_CONTENT', 'COMPLETED')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_game_content_operations_current_item",
            table: "game_content_operations",
            sql: "current_item IS NULL OR (length(current_item) BETWEEN 1 AND 512 AND substr(current_item, 1, 1) <> '/' AND instr(current_item, char(92)) = 0 AND instr(current_item, char(0)) = 0 AND instr(current_item, '//') = 0 AND instr('/' || current_item || '/', '/./') = 0 AND instr('/' || current_item || '/', '/../') = 0)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_game_content_operations_request_id",
            table: "game_content_operations",
            sql: "request_id IS NULL OR (length(request_id) BETWEEN 1 AND 256 AND instr(request_id, char(0)) = 0)");
        migrationBuilder.CreateIndex(
            name: "ix_game_content_operations_request",
            table: "game_content_operations",
            column: "request_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_game_content_operations_request",
            table: "game_content_operations");
        migrationBuilder.DropCheckConstraint("ck_game_content_operations_request_id", "game_content_operations");
        migrationBuilder.DropCheckConstraint("ck_game_content_operations_current_item", "game_content_operations");
        migrationBuilder.DropCheckConstraint("ck_game_content_operations_stage", "game_content_operations");
        migrationBuilder.DropColumn(name: "request_id", table: "game_content_operations");
        migrationBuilder.DropColumn(name: "current_item", table: "game_content_operations");
        migrationBuilder.DropColumn(name: "stage", table: "game_content_operations");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260815100000_AddSaveOperationTargetIdentity")]
public partial class AddSaveOperationTargetIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "expected_target_captured",
            table: "save_file_operations",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "expected_target_exists",
            table: "save_file_operations",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "expected_target_identity_json",
            table: "save_file_operations",
            type: "TEXT",
            maxLength: PersistenceLimits.JsonMaxLength,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "expected_target_captured", table: "save_file_operations");
        migrationBuilder.DropColumn(name: "expected_target_exists", table: "save_file_operations");
        migrationBuilder.DropColumn(name: "expected_target_identity_json", table: "save_file_operations");
    }
}

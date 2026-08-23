using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260823143000_AddSessionWidthModes")]
public partial class AddSessionWidthModes : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeBackslashToYenSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "width_mode",
            table: "sessions",
            type: "TEXT",
            nullable: false,
            defaultValue: "ORIGIN");
        migrationBuilder.AddColumn<int>(
            name: "custom_width",
            table: "sessions",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_width_configuration",
            table: "sessions",
            sql: "width_mode IN ('ORIGIN', 'MAX', 'CUSTOM') AND ((width_mode = 'CUSTOM' AND custom_width BETWEEN 240 AND 16384) OR (width_mode <> 'CUSTOM' AND custom_width IS NULL))");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_sessions_width_configuration", "sessions");
        migrationBuilder.DropColumn(name: "custom_width", table: "sessions");
        migrationBuilder.DropColumn(name: "width_mode", table: "sessions");
    }
}

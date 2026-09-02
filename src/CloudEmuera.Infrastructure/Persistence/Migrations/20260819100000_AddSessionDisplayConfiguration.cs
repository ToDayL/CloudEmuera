using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260819100000_AddSessionDisplayConfiguration")]
public partial class AddSessionDisplayConfiguration : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeFontSizeLineHeightModeSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeBackslashToYenSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeFontFaceSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeWidthSchemaAnnotation, true);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
        modelBuilder.Entity<SessionRow>().Ignore(row => row.FontFaceId);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "font_size", table: "sessions", type: "INTEGER", nullable: false, defaultValue: 18);
        migrationBuilder.AddColumn<int>(name: "line_height", table: "sessions", type: "INTEGER", nullable: false, defaultValue: 19);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "font_size", table: "sessions");
        migrationBuilder.DropColumn(name: "line_height", table: "sessions");
    }
}

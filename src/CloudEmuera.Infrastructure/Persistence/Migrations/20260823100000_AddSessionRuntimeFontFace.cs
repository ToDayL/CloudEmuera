using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260823100000_AddSessionRuntimeFontFace")]
public partial class AddSessionRuntimeFontFace : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludePathRevisionIdentitySchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeBackslashToYenSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeWidthSchemaAnnotation, true);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "font_face_id",
            table: "sessions",
            type: "TEXT",
            maxLength: 128,
            nullable: false,
            defaultValue: "sarasa-fixed-sc-1.0.40-regular");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_font_face_id",
            table: "sessions",
            sql: "length(font_face_id) BETWEEN 1 AND 128 AND instr(font_face_id, char(0)) = 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_sessions_font_face_id", "sessions");
        migrationBuilder.DropColumn(name: "font_face_id", table: "sessions");
    }
}

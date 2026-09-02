using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

/// <summary>
/// Persists whether a Session supplies its own text metrics or preserves the
/// values loaded from the copied game's emuera.config.
/// </summary>
[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260902100000_AddSessionFontSizeLineHeightMode")]
public partial class AddSessionFontSizeLineHeightMode : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "font_size_line_height_mode",
            table: "sessions",
            type: "TEXT",
            nullable: false,
            defaultValue: "OVERRIDE");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_font_size_line_height_mode",
            table: "sessions",
            sql: "font_size_line_height_mode IN ('OVERRIDE', 'CONFIG')");

        migrationBuilder.Sql("""
            UPDATE users
            SET preferences_json = json_set(
                preferences_json,
                '$.sessionStartupDefaults.fontSizeLineHeightMode',
                'OVERRIDE')
            WHERE json_type(preferences_json, '$.sessionStartupDefaults') = 'object'
              AND json_type(preferences_json, '$.sessionStartupDefaults.fontSizeLineHeightMode') IS NULL;
            """);

        migrationBuilder.Sql("""
            UPDATE idempotency_records
            SET response_json = json_set(response_json, '$.fontSizeLineHeightMode', 'OVERRIDE')
            WHERE scope = 'SESSION_CREATE'
              AND json_valid(response_json) = 1
              AND json_type(response_json) = 'object'
              AND json_type(response_json, '$.fontSizeLineHeightMode') IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_sessions_font_size_line_height_mode", "sessions");
        migrationBuilder.DropColumn(name: "font_size_line_height_mode", table: "sessions");
    }
}

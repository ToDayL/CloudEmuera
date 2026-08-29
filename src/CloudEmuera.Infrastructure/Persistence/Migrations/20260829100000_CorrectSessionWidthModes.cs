using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

/// <summary>
/// Splits the former Origin behavior from the game's original width. Existing
/// ORIGIN rows remain adaptive after being rewritten to ADAPTIVE.
/// </summary>
[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260829100000_CorrectSessionWidthModes")]
public partial class CorrectSessionWidthModes : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite rebuilds a table for DropCheckConstraint. Disable evaluation
        // only for this controlled compatibility rewrite so the old ORIGIN
        // rows can be changed before the new CHECK is materialized.
        migrationBuilder.Sql("PRAGMA ignore_check_constraints = ON; UPDATE sessions SET width_mode = 'ADAPTIVE' WHERE width_mode = 'ORIGIN'; PRAGMA ignore_check_constraints = OFF;");
        migrationBuilder.DropCheckConstraint("ck_sessions_width_configuration", "sessions");
        migrationBuilder.AlterColumn<string>(
            name: "width_mode",
            table: "sessions",
            type: "TEXT",
            nullable: false,
            defaultValue: "ADAPTIVE",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldDefaultValue: "ORIGIN");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_width_configuration",
            table: "sessions",
            sql: "width_mode IN ('ORIGINAL', 'MAX', 'ADAPTIVE', 'CUSTOM') AND ((width_mode = 'CUSTOM' AND custom_width BETWEEN 240 AND 16384) OR (width_mode <> 'CUSTOM' AND custom_width IS NULL))");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA ignore_check_constraints = ON; UPDATE sessions SET width_mode = 'ORIGIN' WHERE width_mode IN ('ADAPTIVE', 'ORIGINAL'); PRAGMA ignore_check_constraints = OFF;");
        migrationBuilder.DropCheckConstraint("ck_sessions_width_configuration", "sessions");
        // The old schema has no representation for Original, so a downgrade
        // necessarily preserves the adaptive-compatible behavior instead.
        migrationBuilder.AlterColumn<string>(
            name: "width_mode",
            table: "sessions",
            type: "TEXT",
            nullable: false,
            defaultValue: "ORIGIN",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldDefaultValue: "ADAPTIVE");
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_width_configuration",
            table: "sessions",
            sql: "width_mode IN ('ORIGIN', 'MAX', 'CUSTOM') AND ((width_mode = 'CUSTOM' AND custom_width BETWEEN 240 AND 16384) OR (width_mode <> 'CUSTOM' AND custom_width IS NULL))");
    }
}

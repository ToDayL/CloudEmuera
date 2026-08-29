using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260824130000_AddSessionBackslashToYenOption")]
public partial class AddSessionBackslashToYenOption : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludePathRevisionIdentitySchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.LegacyRuntimeWidthSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "convert_backslash_to_yen",
            table: "sessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);
        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_convert_backslash_to_yen",
            table: "sessions",
            sql: "convert_backslash_to_yen IN (0, 1)");

        migrationBuilder.Sql("""
            UPDATE users
            SET preferences_json = json_set(
                preferences_json,
                '$.sessionStartupDefaults.convertBackslashToYen',
                json('true'))
            WHERE json_type(preferences_json, '$.sessionStartupDefaults') = 'object'
              AND json_type(preferences_json, '$.sessionStartupDefaults.convertBackslashToYen') IS NULL;
            """);

        migrationBuilder.Sql("""
            UPDATE idempotency_records
            SET response_json = json_set(response_json, '$.convertBackslashToYen', json('true'))
            WHERE scope = 'SESSION_CREATE'
              AND json_valid(response_json) = 1
              AND json_type(response_json) = 'object'
              AND json_type(response_json, '$.convertBackslashToYen') IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_sessions_convert_backslash_to_yen", "sessions");
        migrationBuilder.DropColumn(name: "convert_backslash_to_yen", table: "sessions");
    }
}

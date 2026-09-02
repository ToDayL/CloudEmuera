using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260824100000_ReplaceLxgwWenKaiWithBrightCode")]
public partial class ReplaceLxgwWenKaiWithBrightCode : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeFontSizeLineHeightModeSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeBackslashToYenSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.LegacyRuntimeWidthSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ReplaceFaceIds(
            migrationBuilder,
            "lxgw-wenkai-mono-1.522-light", "lxgw-bright-code-2.922-extralight",
            "lxgw-wenkai-mono-1.522-regular", "lxgw-bright-code-2.922-light",
            "lxgw-wenkai-mono-1.522-medium", "lxgw-bright-code-2.922-regular");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ReplaceFaceIds(
            migrationBuilder,
            "lxgw-bright-code-2.922-extralight", "lxgw-wenkai-mono-1.522-light",
            "lxgw-bright-code-2.922-light", "lxgw-wenkai-mono-1.522-regular",
            "lxgw-bright-code-2.922-regular", "lxgw-wenkai-mono-1.522-medium");
    }

    private static void ReplaceFaceIds(
        MigrationBuilder migrationBuilder,
        string firstSource,
        string firstTarget,
        string secondSource,
        string secondTarget,
        string thirdSource,
        string thirdTarget)
    {
        string mapping = $"CASE {{0}} " +
            $"WHEN '{firstSource}' THEN '{firstTarget}' " +
            $"WHEN '{secondSource}' THEN '{secondTarget}' " +
            $"WHEN '{thirdSource}' THEN '{thirdTarget}' ELSE {{0}} END";

        migrationBuilder.Sql($$"""
            UPDATE sessions
            SET font_face_id = {{string.Format(System.Globalization.CultureInfo.InvariantCulture, mapping, "font_face_id")}}
            WHERE font_face_id IN ('{{firstSource}}', '{{secondSource}}', '{{thirdSource}}');

            UPDATE users
            SET preferences_json = json_set(
                preferences_json,
                '$.sessionStartupDefaults.fontFaceId',
                {{string.Format(System.Globalization.CultureInfo.InvariantCulture, mapping, "json_extract(preferences_json, '$.sessionStartupDefaults.fontFaceId')")}})
            WHERE json_valid(preferences_json) = 1
              AND json_extract(preferences_json, '$.sessionStartupDefaults.fontFaceId')
                  IN ('{{firstSource}}', '{{secondSource}}', '{{thirdSource}}');

            UPDATE idempotency_records
            SET response_json = json_set(
                response_json,
                '$.fontFaceId',
                {{string.Format(System.Globalization.CultureInfo.InvariantCulture, mapping, "json_extract(response_json, '$.fontFaceId')")}})
            WHERE json_valid(response_json) = 1
              AND json_extract(response_json, '$.fontFaceId')
                  IN ('{{firstSource}}', '{{secondSource}}', '{{thirdSource}}');
            """);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260817120000_IncreaseSessionRuntimeManifestLimit")]
public partial class IncreaseSessionRuntimeManifestLimit : Migration
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
        // Retained as a migration-history marker for databases that saw the
        // short-lived JSON-column limit change. The subsequent migration
        // removes that column, so rebuilding the sessions table here would
        // only create an unnecessary SQLite compatibility hazard.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // See Up: the next schema migration owns the old column's removal.
    }
}

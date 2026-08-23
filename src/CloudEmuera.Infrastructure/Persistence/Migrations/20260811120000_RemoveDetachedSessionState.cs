using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260811120000_RemoveDetachedSessionState")]
public partial class RemoveDetachedSessionState : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeBackslashToYenSchemaAnnotation, true);
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeFontFaceSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeWidthSchemaAnnotation, true);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
        modelBuilder.Entity<SessionRow>().Ignore(row => row.FontFaceId);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE sessions SET state = 'RUNNING', state_version = state_version + 1 WHERE state = 'DETACHED';");
        // The state CHECK is refreshed by the final schema migration. Keeping
        // this transition free of SQLite table rebuilds lets older databases
        // cross the migration while the current model has newer Session
        // columns that are not present yet.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The state CHECK is owned by the final schema migration.
    }
}

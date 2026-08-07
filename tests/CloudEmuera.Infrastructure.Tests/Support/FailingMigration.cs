using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CloudEmuera.Infrastructure.Tests.Support;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("99990101000000_FailingMigration")]
public sealed class FailingMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "failing_partial",
            columns: table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_failing_partial", x => x.id));
        migrationBuilder.Sql("CREATE TABLE failing_partial (id INTEGER NOT NULL);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "failing_partial");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
    }
}

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
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE sessions SET state = 'RUNNING', state_version = state_version + 1 WHERE state = 'DETACHED';");

        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_state",
            table: "sessions");

        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_state",
            table: "sessions",
            sql: "state IN ('CREATING', 'STARTING', 'RUNNING', 'STOPPING', 'CLOSED', 'CRASHED')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_sessions_state",
            table: "sessions");

        migrationBuilder.AddCheckConstraint(
            name: "ck_sessions_state",
            table: "sessions",
            sql: "state IN ('CREATING', 'STARTING', 'RUNNING', 'DETACHED', 'STOPPING', 'CLOSED', 'CRASHED')");
    }
}

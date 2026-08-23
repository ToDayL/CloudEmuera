using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260811130000_AddApiOwnedWorkerLifecycle")]
public partial class AddApiOwnedWorkerLifecycle : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeFontFaceSchemaAnnotation, true);
        modelBuilder.HasAnnotation(SessionConfiguration.ExcludeRuntimeWidthSchemaAnnotation, true);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEmueraDbContext).Assembly);
        modelBuilder.Entity<SessionRow>().Ignore(row => row.FontFaceId);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A lease from the pre-API topology is never allowed to remain an
        // ACTIVE fact after this migration. STARTING keeps the row available
        // for the new API's reconciliation barrier without claiming that its
        // old process identity is trustworthy.
        migrationBuilder.Sql("UPDATE worker_leases SET status = 'STARTING', pid = NULL WHERE status IN ('ACTIVE', 'STOPPING', 'EXPIRED');");
        migrationBuilder.Sql("UPDATE sessions SET closed_at = last_activity_at WHERE state = 'CRASHED' AND closed_at IS NULL;");

        // The Session CHECK is refreshed by the final schema migration. Do
        // not rebuild this table before the later compact binding columns are
        // introduced on old databases.
        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_sessions_owner_state ON sessions (owner_user_id, state);");

        migrationBuilder.AddColumn<string>(
            "control_plane_instance_id",
            "worker_leases",
            type: "TEXT",
            maxLength: 128,
            nullable: false,
            defaultValue: "ctl_migration");
        migrationBuilder.AddColumn<string>(
            "process_boot_id",
            "worker_leases",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
        migrationBuilder.AddColumn<long>(
            "process_start_ticks",
            "worker_leases",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.DropCheckConstraint("ck_worker_leases_status", "worker_leases");
        migrationBuilder.DropCheckConstraint("ck_worker_leases_pid", "worker_leases");
        migrationBuilder.AddCheckConstraint(
            "ck_worker_leases_status",
            "worker_leases",
            "status IN ('STARTING', 'ACTIVE', 'STOPPING')");
        migrationBuilder.AddCheckConstraint(
            "ck_worker_leases_pid",
            "worker_leases",
            "pid IS NULL OR pid > 0");
        migrationBuilder.AddCheckConstraint(
            "ck_worker_leases_control_plane",
            "worker_leases",
            "substr(control_plane_instance_id, 1, 4) = 'ctl_' AND length(control_plane_instance_id) BETWEEN 5 AND 128 AND instr(control_plane_instance_id, char(0)) = 0");
        migrationBuilder.AddCheckConstraint(
            "ck_worker_leases_process_identity",
            "worker_leases",
            "((pid IS NULL AND process_boot_id IS NULL AND process_start_ticks IS NULL) OR (pid IS NOT NULL AND process_boot_id IS NOT NULL AND process_start_ticks IS NOT NULL AND process_start_ticks > 0)) AND (status = 'STARTING' OR (pid IS NOT NULL AND process_boot_id IS NOT NULL AND process_start_ticks IS NOT NULL))");
        migrationBuilder.AddCheckConstraint(
            "ck_worker_leases_process_boot_id",
            "worker_leases",
            "process_boot_id IS NULL OR (length(process_boot_id) = 36 AND instr(process_boot_id, char(0)) = 0)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_worker_leases_process_boot_id", "worker_leases");
        migrationBuilder.DropCheckConstraint("ck_worker_leases_process_identity", "worker_leases");
        migrationBuilder.DropCheckConstraint("ck_worker_leases_control_plane", "worker_leases");
        migrationBuilder.DropCheckConstraint("ck_worker_leases_pid", "worker_leases");
        migrationBuilder.DropCheckConstraint("ck_worker_leases_status", "worker_leases");
        migrationBuilder.AddCheckConstraint("ck_worker_leases_pid", "worker_leases", "pid IS NULL OR pid > 0");
        migrationBuilder.AddCheckConstraint("ck_worker_leases_status", "worker_leases", "status IN ('STARTING', 'ACTIVE', 'STOPPING', 'EXPIRED')");
        migrationBuilder.DropColumn("process_start_ticks", "worker_leases");
        migrationBuilder.DropColumn("process_boot_id", "worker_leases");
        migrationBuilder.DropColumn("control_plane_instance_id", "worker_leases");

        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_sessions_owner_state;");
        // The Session CHECK is owned by the final schema migration.
    }
}

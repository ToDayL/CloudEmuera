using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260809090000_AddLocalIdentitySessions")]
public sealed class AddLocalIdentitySessions : Migration
{
    private static readonly string[] AuthSessionUserActiveColumns = ["user_id", "revoked_at", "absolute_expires_at"];

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        FreezeIdentityModel.BuildFrozenTargetModel(modelBuilder);
    }
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite supports these nullable/additive columns without rebuilding the P1-01 users table.
        migrationBuilder.AddColumn<string>(name: "email", table: "users", type: "TEXT", maxLength: 254, nullable: true);
        migrationBuilder.AddColumn<string>(name: "normalized_email", table: "users", type: "TEXT", maxLength: 254, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "must_change_password", table: "users", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<long>(name: "password_changed_at", table: "users", type: "INTEGER", nullable: true);
        migrationBuilder.Sql("UPDATE users SET password_changed_at = updated_at, must_change_password = 1 WHERE password_hash IS NOT NULL;");
        migrationBuilder.CreateIndex(name: "ux_users_normalized_email", table: "users", column: "normalized_email", unique: true, filter: "normalized_email IS NOT NULL");

        migrationBuilder.CreateTable(
            name: "instance_state",
            columns: table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
                bootstrap_status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                initialized_at = table.Column<long>(type: "INTEGER", nullable: true),
                initial_admin_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_instance_state", x => x.id);
                table.ForeignKey("fk_instance_state_initial_admin", x => x.initial_admin_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("ck_instance_state_id", "id = 1");
                table.CheckConstraint("ck_instance_state_status", "bootstrap_status IN ('BOOTSTRAP_REQUIRED', 'COMPLETED')");
                table.CheckConstraint("ck_instance_state_shape", "(bootstrap_status = 'BOOTSTRAP_REQUIRED' AND initialized_at IS NULL AND initial_admin_user_id IS NULL) OR (bootstrap_status = 'COMPLETED' AND initialized_at IS NOT NULL AND initial_admin_user_id IS NOT NULL)");
                table.CheckConstraint("ck_instance_state_version", "state_version >= 0");
            });
        migrationBuilder.Sql(@"INSERT INTO instance_state (id, bootstrap_status, initialized_at, initial_admin_user_id, state_version)
SELECT 1, 'COMPLETED', created_at, id, 0 FROM users
 WHERE role = 'ADMIN' AND status = 'ACTIVE' AND normalized_email IS NOT NULL AND password_hash IS NOT NULL
 ORDER BY created_at, id LIMIT 1;");
        migrationBuilder.Sql("INSERT INTO instance_state (id, bootstrap_status, initialized_at, initial_admin_user_id, state_version) SELECT 1, 'BOOTSTRAP_REQUIRED', NULL, NULL, 0 WHERE NOT EXISTS (SELECT 1 FROM instance_state WHERE id = 1);");
        migrationBuilder.Sql("""
CREATE TRIGGER trg_users_identity_insert BEFORE INSERT ON users
WHEN NOT ((NEW.email IS NULL AND NEW.normalized_email IS NULL) OR (NEW.email IS NOT NULL AND NEW.normalized_email IS NOT NULL AND length(NEW.email) BETWEEN 3 AND 254 AND length(NEW.normalized_email) BETWEEN 3 AND 254 AND instr(NEW.email, char(0)) = 0 AND instr(NEW.normalized_email, char(0)) = 0))
 OR NOT (NEW.must_change_password IN (0, 1) AND ((NEW.password_hash IS NULL AND NEW.password_changed_at IS NULL) OR (NEW.password_hash IS NOT NULL AND NEW.password_changed_at IS NOT NULL)))
BEGIN SELECT RAISE(ABORT, 'users_identity_constraint'); END;
""");
        migrationBuilder.Sql("""
CREATE TRIGGER trg_users_identity_update BEFORE UPDATE OF email, normalized_email, must_change_password, password_hash, password_changed_at ON users
WHEN NOT ((NEW.email IS NULL AND NEW.normalized_email IS NULL) OR (NEW.email IS NOT NULL AND NEW.normalized_email IS NOT NULL AND length(NEW.email) BETWEEN 3 AND 254 AND length(NEW.normalized_email) BETWEEN 3 AND 254 AND instr(NEW.email, char(0)) = 0 AND instr(NEW.normalized_email, char(0)) = 0))
 OR NOT (NEW.must_change_password IN (0, 1) AND ((NEW.password_hash IS NULL AND NEW.password_changed_at IS NULL) OR (NEW.password_hash IS NOT NULL AND NEW.password_changed_at IS NOT NULL)))
BEGIN SELECT RAISE(ABORT, 'users_identity_constraint'); END;
""");

        migrationBuilder.CreateTable(
            name: "auth_sessions",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                security_stamp = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                created_at = table.Column<long>(type: "INTEGER", nullable: false),
                last_seen_at = table.Column<long>(type: "INTEGER", nullable: false),
                idle_expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                absolute_expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                revoked_at = table.Column<long>(type: "INTEGER", nullable: true),
                revoke_reason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                is_persistent = table.Column<bool>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_auth_sessions", x => x.id);
                table.ForeignKey("fk_auth_sessions_user", x => x.user_id, "users", "id", onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("ck_auth_sessions_id", "substr(id, 1, 6) = 'auths_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                table.CheckConstraint("ck_auth_sessions_times", "created_at >= 0 AND created_at <= last_seen_at AND last_seen_at <= idle_expires_at AND idle_expires_at <= absolute_expires_at");
                table.CheckConstraint("ck_auth_sessions_revocation", "(revoked_at IS NULL AND revoke_reason IS NULL) OR (revoked_at IS NOT NULL AND revoke_reason IS NOT NULL AND revoked_at >= created_at)");
            });
        migrationBuilder.CreateIndex(name: "ix_auth_sessions_user_active", table: "auth_sessions", columns: AuthSessionUserActiveColumns);
        migrationBuilder.CreateIndex(name: "ix_auth_sessions_idle_expiry", table: "auth_sessions", column: "idle_expires_at");
        migrationBuilder.CreateIndex(name: "ix_instance_state_initial_admin", table: "instance_state", column: "initial_admin_user_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_users_identity_update;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_users_identity_insert;");
        migrationBuilder.DropTable(name: "auth_sessions");
        migrationBuilder.DropTable(name: "instance_state");
        migrationBuilder.DropIndex(name: "ux_users_normalized_email", table: "users");
        migrationBuilder.DropColumn(name: "email", table: "users");
        migrationBuilder.DropColumn(name: "normalized_email", table: "users");
        migrationBuilder.DropColumn(name: "must_change_password", table: "users");
        migrationBuilder.DropColumn(name: "password_changed_at", table: "users");
    }
}

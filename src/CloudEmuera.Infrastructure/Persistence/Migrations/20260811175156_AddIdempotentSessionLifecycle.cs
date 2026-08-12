using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotentSessionLifecycle : Migration
    {
        private static readonly string[] SessionOwnerCreatedColumns = ["owner_user_id", "created_at", "id"];
        private static readonly bool[] SessionOwnerCreatedDescending = [false, true, true];
        private static readonly string[] IdempotencyStatusUpdatedColumns = ["status", "updated_at"];
        private static readonly string[] SessionCreationStatusUpdatedColumns = ["status", "updated_at"];
        private static readonly string[] LegacySessionOwnerCreatedColumns = ["owner_user_id", "created_at"];
        private static readonly bool[] LegacySessionOwnerCreatedDescending = [false, true];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_owner_created",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_time_order",
                table: "idempotency_records");

            migrationBuilder.AddColumn<long>(
                name: "completed_at",
                table: "idempotency_records",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error_code",
                table: "idempotency_records",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "idempotency_records",
                type: "TEXT",
                nullable: false,
                defaultValue: "IN_PROGRESS");

            migrationBuilder.AddColumn<long>(
                name: "updated_at",
                table: "idempotency_records",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // P1-01/P1-04 used an empty JSON object as the in-progress
            // sentinel. Preserve that durable fact while classifying all
            // completed legacy responses as successful commands.
            migrationBuilder.Sql(
                "UPDATE idempotency_records SET updated_at = created_at, status = CASE WHEN response_json = '{}' THEN 'IN_PROGRESS' ELSE 'SUCCEEDED' END, completed_at = CASE WHEN response_json = '{}' THEN NULL ELSE created_at END;");

            migrationBuilder.CreateTable(
                name: "session_creation_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    actor_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    staging_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    reserved_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    expected_file_count = table.Column<long>(type: "INTEGER", nullable: false),
                    expected_content_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    last_error_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_creation_operations", x => x.id);
                    table.CheckConstraint("ck_session_creation_operations_actor_id", "substr(actor_user_id, 1, 4) = 'usr_' AND length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0");
                    table.CheckConstraint("ck_session_creation_operations_completion", "(status IN ('COMMITTED', 'FAILED') AND completed_at IS NOT NULL) OR (status NOT IN ('COMMITTED', 'FAILED') AND completed_at IS NULL)");
                    table.CheckConstraint("ck_session_creation_operations_counters", "reserved_bytes >= 0 AND expected_file_count >= 0 AND expected_content_bytes >= 0 AND attempt_count >= 0 AND state_version >= 0");
                    table.CheckConstraint("ck_session_creation_operations_error", "last_error_code IS NULL OR (length(last_error_code) BETWEEN 1 AND 128 AND instr(last_error_code, char(0)) = 0)");
                    table.CheckConstraint("ck_session_creation_operations_id", "substr(id, 1, 5) = 'scop_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_session_creation_operations_path", "length(staging_path) BETWEEN 1 AND 512 AND substr(staging_path, 1, 1) <> '/' AND instr(staging_path, char(92)) = 0 AND instr(staging_path, char(0)) = 0 AND instr(staging_path, '//') = 0 AND instr('/' || staging_path || '/', '/./') = 0 AND instr('/' || staging_path || '/', '/../') = 0");
                    table.CheckConstraint("ck_session_creation_operations_session_id", "substr(session_id, 1, 5) = 'sess_' AND length(session_id) BETWEEN 5 AND 64 AND instr(session_id, char(0)) = 0");
                    table.CheckConstraint("ck_session_creation_operations_status", "status IN ('PREPARED', 'COPYING', 'ROOT_PUBLISHED', 'COMMITTED', 'FAILED')");
                    table.CheckConstraint("ck_session_creation_operations_time", "created_at >= 0 AND updated_at >= created_at AND (completed_at IS NULL OR completed_at >= updated_at)");
                    table.ForeignKey(
                        name: "fk_session_creation_operations_actor_user",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_creation_operations_session",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_root_mutation_leases",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    operation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    actor_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    purpose = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    acquired_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_root_mutation_leases", x => x.session_id);
                    table.CheckConstraint("ck_session_root_mutation_leases_actor_id", "substr(actor_user_id, 1, 4) = 'usr_' AND length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0");
                    table.CheckConstraint("ck_session_root_mutation_leases_operation_id", "substr(operation_id, 1, 4) = 'mut_' AND length(operation_id) BETWEEN 5 AND 64 AND instr(operation_id, char(0)) = 0");
                    table.CheckConstraint("ck_session_root_mutation_leases_purpose", "purpose IN ('SAVE_IMPORT', 'SAVE_RENAME', 'SAVE_DELETE', 'SAVE_COPY')");
                    table.CheckConstraint("ck_session_root_mutation_leases_session_id", "substr(session_id, 1, 5) = 'sess_' AND length(session_id) BETWEEN 5 AND 64 AND instr(session_id, char(0)) = 0");
                    table.CheckConstraint("ck_session_root_mutation_leases_time", "acquired_at >= 0 AND expires_at > acquired_at");
                    table.ForeignKey(
                        name: "fk_session_root_mutation_leases_actor_user",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_root_mutation_leases_session",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_owner_created",
                table: "sessions",
                columns: SessionOwnerCreatedColumns,
                descending: SessionOwnerCreatedDescending);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_status_updated",
                table: "idempotency_records",
                columns: IdempotencyStatusUpdatedColumns);

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_error_code",
                table: "idempotency_records",
                sql: "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 128 AND instr(error_code, char(0)) = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_status",
                table: "idempotency_records",
                sql: "status IN ('IN_PROGRESS', 'SUCCEEDED', 'FAILED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_terminal_fields",
                table: "idempotency_records",
                sql: "(status = 'IN_PROGRESS' AND error_code IS NULL AND completed_at IS NULL) OR (status = 'SUCCEEDED' AND error_code IS NULL AND completed_at IS NOT NULL) OR (status = 'FAILED' AND error_code IS NOT NULL AND completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_time_order",
                table: "idempotency_records",
                sql: "created_at >= 0 AND updated_at >= created_at AND expires_at > created_at AND (completed_at IS NULL OR completed_at >= updated_at)");

            migrationBuilder.CreateIndex(
                name: "IX_session_creation_operations_actor_user_id",
                table: "session_creation_operations",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_creation_operations_status_updated",
                table: "session_creation_operations",
                columns: SessionCreationStatusUpdatedColumns);

            migrationBuilder.CreateIndex(
                name: "ux_session_creation_operations_session",
                table: "session_creation_operations",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_session_creation_operations_staging_path",
                table: "session_creation_operations",
                column: "staging_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_root_mutation_leases_actor_user_id",
                table: "session_root_mutation_leases",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_session_root_mutation_leases_operation",
                table: "session_root_mutation_leases",
                column: "operation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_creation_operations");

            migrationBuilder.DropTable(
                name: "session_root_mutation_leases");

            migrationBuilder.DropIndex(
                name: "ix_sessions_owner_created",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_idempotency_records_status_updated",
                table: "idempotency_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_error_code",
                table: "idempotency_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_status",
                table: "idempotency_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_terminal_fields",
                table: "idempotency_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_idempotency_time_order",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "error_code",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "status",
                table: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "idempotency_records");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_owner_created",
                table: "sessions",
                columns: LegacySessionOwnerCreatedColumns,
                descending: LegacySessionOwnerCreatedDescending);

            migrationBuilder.AddCheckConstraint(
                name: "ck_idempotency_time_order",
                table: "idempotency_records",
                sql: "created_at >= 0 AND expires_at > created_at");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace CloudEmuera.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(CloudEmueraDbContext))]
[Migration("20260814120000_AddSessionSaveFileOperations")]
public partial class AddSessionSaveFileOperations : Migration
{
    private static readonly string[] StatusUpdatedColumns = ["status", "updated_at", "id"];
    private static readonly string[] SessionStatusColumns = ["session_id", "status"];
    private static readonly string[] IdempotencyColumns = ["actor_user_id", "idempotency_scope", "idempotency_key_hash"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "save_file_operations",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                session_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                actor_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                idempotency_scope = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                idempotency_key_hash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                source_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                target_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                payload_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                payload_size = table.Column<long>(type: "INTEGER", nullable: true),
                payload_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                expected_source_identity_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: true),
                result_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false),
                error_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                created_at = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_save_file_operations", x => x.id);
                table.CheckConstraint("ck_save_file_operations_id", "substr(id, 1, 5) = 'sfop_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                table.CheckConstraint("ck_save_file_operations_session_id", "substr(session_id, 1, 5) = 'sess_' AND length(session_id) BETWEEN 5 AND 64 AND instr(session_id, char(0)) = 0");
                table.CheckConstraint("ck_save_file_operations_actor_id", "substr(actor_user_id, 1, 4) = 'usr_' AND length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0");
                table.CheckConstraint("ck_save_file_operations_scope", "idempotency_scope IN ('SAVE_IMPORT', 'SAVE_RENAME', 'SAVE_DELETE')");
                table.CheckConstraint("ck_save_file_operations_type", "type IN ('IMPORT', 'RENAME', 'DELETE')");
                table.CheckConstraint("ck_save_file_operations_status", "status IN ('PREPARED', 'STAGED', 'PUBLISHED', 'COMMITTED', 'FAILED')");
                table.CheckConstraint("ck_save_file_operations_key_hash", "length(idempotency_key_hash) = 71 AND substr(idempotency_key_hash, 1, 7) = 'sha256:' AND lower(idempotency_key_hash) = idempotency_key_hash AND substr(idempotency_key_hash, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(idempotency_key_hash, 8)) = 64");
                table.CheckConstraint("ck_save_file_operations_source_path", "source_path IS NULL OR (length(source_path) BETWEEN 1 AND 512 AND substr(source_path, 1, 1) <> '/' AND instr(source_path, char(92)) = 0 AND instr(source_path, char(0)) = 0 AND instr(source_path, '//') = 0)");
                table.CheckConstraint("ck_save_file_operations_target_path", "length(target_path) BETWEEN 1 AND 512 AND substr(target_path, 1, 1) <> '/' AND instr(target_path, char(92)) = 0 AND instr(target_path, char(0)) = 0 AND instr(target_path, '//') = 0 AND instr('/' || target_path || '/', '/./') = 0 AND instr('/' || target_path || '/', '/../') = 0");
                table.CheckConstraint("ck_save_file_operations_payload_path", "payload_path IS NULL OR (length(payload_path) BETWEEN 1 AND 512 AND substr(payload_path, 1, 1) <> '/' AND instr(payload_path, char(92)) = 0 AND instr(payload_path, char(0)) = 0 AND instr(payload_path, '//') = 0)");
                table.CheckConstraint("ck_save_file_operations_payload", "payload_size IS NULL OR payload_size >= 0");
                table.CheckConstraint("ck_save_file_operations_digest", "payload_digest IS NULL OR (length(payload_digest) = 71 AND substr(payload_digest, 1, 7) = 'sha256:' AND lower(payload_digest) = payload_digest AND substr(payload_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(payload_digest, 8)) = 64)");
                table.CheckConstraint("ck_save_file_operations_expected_identity", "expected_source_identity_json IS NULL OR (length(expected_source_identity_json) BETWEEN 2 AND 1048576 AND json_valid(expected_source_identity_json) = 1)");
                table.CheckConstraint("ck_save_file_operations_result", "length(result_json) BETWEEN 2 AND 1048576 AND json_valid(result_json) = 1 AND result_json <> ''");
                table.CheckConstraint("ck_save_file_operations_error", "error_code IS NULL OR (length(error_code) BETWEEN 1 AND 128 AND instr(error_code, char(0)) = 0)");
                table.CheckConstraint("ck_save_file_operations_completion", "(status IN ('COMMITTED', 'FAILED') AND completed_at IS NOT NULL) OR (status NOT IN ('COMMITTED', 'FAILED') AND completed_at IS NULL)");
                table.CheckConstraint("ck_save_file_operations_time", "created_at >= 0 AND updated_at >= created_at AND (completed_at IS NULL OR completed_at >= updated_at)");
                table.CheckConstraint("ck_save_file_operations_state", "state_version >= 0");
                table.ForeignKey("fk_save_file_operations_actor_user", x => x.actor_user_id, "users", "id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("fk_save_file_operations_session", x => x.session_id, "sessions", "id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("ix_save_file_operations_status_updated", "save_file_operations", StatusUpdatedColumns);
        migrationBuilder.CreateIndex("ix_save_file_operations_session_status", "save_file_operations", SessionStatusColumns);
        migrationBuilder.CreateIndex("ux_save_file_operations_idempotency", "save_file_operations", IdempotencyColumns, unique: true);

        migrationBuilder.Sql("DROP INDEX IF EXISTS ux_session_root_mutation_leases_operation;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS IX_session_root_mutation_leases_actor_user_id;");
        migrationBuilder.Sql("ALTER TABLE session_root_mutation_leases RENAME TO session_root_mutation_leases_old;");
        migrationBuilder.Sql("CREATE TABLE session_root_mutation_leases (session_id TEXT NOT NULL, operation_id TEXT NOT NULL, actor_user_id TEXT NOT NULL, purpose TEXT NOT NULL, acquired_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, CONSTRAINT pk_session_root_mutation_leases PRIMARY KEY (session_id), CONSTRAINT ck_session_root_mutation_leases_actor_id CHECK (substr(actor_user_id, 1, 4) = 'usr_' AND length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0), CONSTRAINT ck_session_root_mutation_leases_operation_id CHECK ((substr(operation_id, 1, 4) = 'mut_' OR substr(operation_id, 1, 5) = 'sfop_') AND length(operation_id) BETWEEN 5 AND 64 AND instr(operation_id, char(0)) = 0), CONSTRAINT ck_session_root_mutation_leases_purpose CHECK (purpose IN ('SAVE_IMPORT', 'SAVE_RENAME', 'SAVE_DELETE', 'SAVE_COPY')), CONSTRAINT ck_session_root_mutation_leases_session_id CHECK (substr(session_id, 1, 5) = 'sess_' AND length(session_id) BETWEEN 5 AND 64 AND instr(session_id, char(0)) = 0), CONSTRAINT ck_session_root_mutation_leases_time CHECK (acquired_at >= 0 AND expires_at > acquired_at), CONSTRAINT fk_session_root_mutation_leases_actor_user FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE RESTRICT, CONSTRAINT fk_session_root_mutation_leases_session FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE RESTRICT);");
        migrationBuilder.Sql("INSERT INTO session_root_mutation_leases (session_id, operation_id, actor_user_id, purpose, acquired_at, expires_at) SELECT session_id, operation_id, actor_user_id, purpose, acquired_at, expires_at FROM session_root_mutation_leases_old;");
        migrationBuilder.Sql("DROP TABLE session_root_mutation_leases_old;");
        migrationBuilder.CreateIndex("IX_session_root_mutation_leases_actor_user_id", "session_root_mutation_leases", "actor_user_id");
        migrationBuilder.CreateIndex("ux_session_root_mutation_leases_operation", "session_root_mutation_leases", "operation_id", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "save_file_operations");
        migrationBuilder.DropIndex("ux_session_root_mutation_leases_operation", "session_root_mutation_leases");
        migrationBuilder.DropIndex("IX_session_root_mutation_leases_actor_user_id", "session_root_mutation_leases");
        migrationBuilder.Sql("ALTER TABLE session_root_mutation_leases RENAME TO session_root_mutation_leases_new;");
        migrationBuilder.Sql("CREATE TABLE session_root_mutation_leases (session_id TEXT NOT NULL, operation_id TEXT NOT NULL, actor_user_id TEXT NOT NULL, purpose TEXT NOT NULL, acquired_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, CONSTRAINT pk_session_root_mutation_leases PRIMARY KEY (session_id), CONSTRAINT ck_session_root_mutation_leases_operation_id CHECK (substr(operation_id, 1, 4) = 'mut_' AND length(operation_id) BETWEEN 5 AND 64 AND instr(operation_id, char(0)) = 0), CONSTRAINT ck_session_root_mutation_leases_actor_id CHECK (substr(actor_user_id, 1, 4) = 'usr_' AND length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0), CONSTRAINT ck_session_root_mutation_leases_purpose CHECK (purpose IN ('SAVE_IMPORT', 'SAVE_RENAME', 'SAVE_DELETE', 'SAVE_COPY')), CONSTRAINT ck_session_root_mutation_leases_session_id CHECK (substr(session_id, 1, 5) = 'sess_' AND length(session_id) BETWEEN 5 AND 64 AND instr(session_id, char(0)) = 0), CONSTRAINT ck_session_root_mutation_leases_time CHECK (acquired_at >= 0 AND expires_at > acquired_at), CONSTRAINT fk_session_root_mutation_leases_actor_user FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE RESTRICT, CONSTRAINT fk_session_root_mutation_leases_session FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE RESTRICT);");
        migrationBuilder.Sql("INSERT INTO session_root_mutation_leases SELECT session_id, operation_id, actor_user_id, purpose, acquired_at, expires_at FROM session_root_mutation_leases_new;");
        migrationBuilder.Sql("DROP TABLE session_root_mutation_leases_new;");
        migrationBuilder.CreateIndex("IX_session_root_mutation_leases_actor_user_id", "session_root_mutation_leases", "actor_user_id");
        migrationBuilder.CreateIndex("ux_session_root_mutation_leases_operation", "session_root_mutation_leases", "operation_id", unique: true);
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace CloudEmuera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    occurred_at = table.Column<long>(type: "INTEGER", nullable: false),
                    actor_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    actor_type = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    resource_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    resource_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    request_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    reason_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    metadata_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_action", "length(action) BETWEEN 1 AND 128 AND instr(action, char(0)) = 0");
                    table.CheckConstraint("ck_audit_events_actor_type", "actor_type IN ('USER', 'ADMIN', 'SYSTEM')");
                    table.CheckConstraint("ck_audit_events_actor_user_id", "actor_user_id IS NULL OR (length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0)");
                    table.CheckConstraint("ck_audit_events_id", "substr(id, 1, 6) = 'audit_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_audit_events_metadata_json", "length(metadata_json) BETWEEN 2 AND 1048576 AND json_valid(metadata_json) = 1 AND metadata_json <> ''");
                    table.CheckConstraint("ck_audit_events_occurred_at", "occurred_at >= 0");
                    table.CheckConstraint("ck_audit_events_reason_code", "reason_code IS NULL OR (length(reason_code) BETWEEN 1 AND 128 AND instr(reason_code, char(0)) = 0)");
                    table.CheckConstraint("ck_audit_events_request_id", "request_id IS NULL OR (length(request_id) BETWEEN 1 AND 128 AND instr(request_id, char(0)) = 0)");
                    table.CheckConstraint("ck_audit_events_resource_id", "length(resource_id) BETWEEN 1 AND 128 AND instr(resource_id, char(0)) = 0");
                    table.CheckConstraint("ck_audit_events_resource_type", "length(resource_type) BETWEEN 1 AND 64 AND instr(resource_type, char(0)) = 0");
                    table.CheckConstraint("ck_audit_events_result", "result IN ('SUCCEEDED', 'FAILED')");
                });

            migrationBuilder.CreateTable(
                name: "quota_profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    max_active_sessions = table.Column<long>(type: "INTEGER", nullable: false),
                    max_game_package_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    max_session_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    max_output_bytes_per_second = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_profiles", x => x.id);
                    table.CheckConstraint("ck_quota_profiles_id", "substr(id, 1, 4) = 'qtp_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_quota_profiles_limits_positive", "max_active_sessions > 0 AND max_game_package_bytes > 0 AND max_session_bytes > 0 AND max_output_bytes_per_second > 0");
                    table.CheckConstraint("ck_quota_profiles_name", "length(name) BETWEEN 1 AND 200 AND instr(name, char(0)) = 0");
                    table.CheckConstraint("ck_quota_profiles_state_version", "state_version >= 0");
                    table.CheckConstraint("ck_quota_profiles_time_order", "created_at >= 0 AND updated_at >= created_at");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    login_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    normalized_login_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    quota_profile_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    preferences_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    security_stamp = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    lockout_end = table.Column<long>(type: "INTEGER", nullable: true),
                    access_failed_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_access_failed_count", "access_failed_count >= 0");
                    table.CheckConstraint("ck_users_id", "substr(id, 1, 4) = 'usr_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_users_login_names", "length(login_name) BETWEEN 1 AND 128 AND length(normalized_login_name) BETWEEN 1 AND 128 AND instr(login_name, char(0)) = 0 AND instr(normalized_login_name, char(0)) = 0");
                    table.CheckConstraint("ck_users_preferences_json", "length(preferences_json) BETWEEN 2 AND 1048576 AND json_valid(preferences_json) = 1 AND preferences_json <> ''");
                    table.CheckConstraint("ck_users_role", "role IN ('PLAYER', 'ADMIN')");
                    table.CheckConstraint("ck_users_state_version", "state_version >= 0");
                    table.CheckConstraint("ck_users_status", "status IN ('ACTIVE', 'DISABLED')");
                    table.CheckConstraint("ck_users_string_lengths", "(password_hash IS NULL OR length(password_hash) BETWEEN 1 AND 512) AND length(security_stamp) BETWEEN 1 AND 128 AND instr(security_stamp, char(0)) = 0");
                    table.CheckConstraint("ck_users_time_order", "created_at >= 0 AND updated_at >= created_at AND (lockout_end IS NULL OR lockout_end >= 0)");
                    table.ForeignKey(
                        name: "fk_users_quota_profiles",
                        column: x => x.quota_profile_id,
                        principalTable: "quota_profiles",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    visibility = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_games", x => x.id);
                    table.CheckConstraint("ck_games_id", "substr(id, 1, 5) = 'game_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_games_name", "length(name) BETWEEN 1 AND 200 AND instr(name, char(0)) = 0");
                    table.CheckConstraint("ck_games_owner_id", "substr(owner_user_id, 1, 4) = 'usr_' AND length(owner_user_id) BETWEEN 5 AND 64 AND instr(owner_user_id, char(0)) = 0");
                    table.CheckConstraint("ck_games_state_version", "state_version >= 0");
                    table.CheckConstraint("ck_games_status", "status IN ('ACTIVE', 'DELETED')");
                    table.CheckConstraint("ck_games_time_order", "created_at >= 0 AND updated_at >= created_at");
                    table.CheckConstraint("ck_games_visibility", "visibility IN ('PRIVATE', 'SERVER_SHARED')");
                    table.ForeignKey(
                        name: "fk_games_owner_user",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    actor_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    scope = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    request_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    response_status = table.Column<int>(type: "INTEGER", nullable: false),
                    response_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}"),
                    resource_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.actor_user_id, x.scope, x.idempotency_key });
                    table.CheckConstraint("ck_idempotency_actor_id", "substr(actor_user_id, 1, 4) = 'usr_' AND length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0");
                    table.CheckConstraint("ck_idempotency_key", "length(idempotency_key) BETWEEN 1 AND 256 AND instr(idempotency_key, char(0)) = 0");
                    table.CheckConstraint("ck_idempotency_request_digest", "length(request_digest) = 71 AND substr(request_digest, 1, 7) = 'sha256:' AND lower(request_digest) = request_digest AND substr(request_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(request_digest, 8)) = 64");
                    table.CheckConstraint("ck_idempotency_resource_id", "resource_id IS NULL OR (length(resource_id) BETWEEN 1 AND 128 AND instr(resource_id, char(0)) = 0)");
                    table.CheckConstraint("ck_idempotency_response_json", "length(response_json) BETWEEN 2 AND 1048576 AND json_valid(response_json) = 1 AND response_json <> ''");
                    table.CheckConstraint("ck_idempotency_response_status", "response_status BETWEEN 100 AND 599");
                    table.CheckConstraint("ck_idempotency_scope", "length(scope) BETWEEN 1 AND 100 AND instr(scope, char(0)) = 0");
                    table.CheckConstraint("ck_idempotency_time_order", "created_at >= 0 AND expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_idempotency_records_actor_user",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "game_versions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    game_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    version_label = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    content_digest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    content_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    manifest_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}"),
                    runtime_config_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}"),
                    compatibility_summary_json = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false, defaultValue: "{}"),
                    created_by = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    published_at = table.Column<long>(type: "INTEGER", nullable: true),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_versions", x => x.id);
                    table.UniqueConstraint("ak_game_versions_id_game_id", x => new { x.id, x.game_id });
                    table.CheckConstraint("ck_game_versions_compatibility_json", "length(compatibility_summary_json) BETWEEN 2 AND 1048576 AND json_valid(compatibility_summary_json) = 1 AND compatibility_summary_json <> ''");
                    table.CheckConstraint("ck_game_versions_content_path", "length(content_path) BETWEEN 1 AND 512 AND substr(content_path, 1, 1) <> '/' AND instr(content_path, char(92)) = 0 AND instr(content_path, char(0)) = 0 AND instr(content_path, '//') = 0 AND instr('/' || content_path || '/', '/./') = 0 AND instr('/' || content_path || '/', '/../') = 0");
                    table.CheckConstraint("ck_game_versions_created_by", "substr(created_by, 1, 4) = 'usr_' AND length(created_by) BETWEEN 5 AND 64 AND instr(created_by, char(0)) = 0");
                    table.CheckConstraint("ck_game_versions_digest", "content_digest IS NULL OR (length(content_digest) = 71 AND substr(content_digest, 1, 7) = 'sha256:' AND lower(content_digest) = content_digest AND substr(content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(content_digest, 8)) = 64)");
                    table.CheckConstraint("ck_game_versions_game_id", "substr(game_id, 1, 5) = 'game_' AND length(game_id) BETWEEN 5 AND 64 AND instr(game_id, char(0)) = 0");
                    table.CheckConstraint("ck_game_versions_id", "substr(id, 1, 5) = 'gver_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_game_versions_manifest_json", "length(manifest_json) BETWEEN 2 AND 1048576 AND json_valid(manifest_json) = 1 AND manifest_json <> ''");
                    table.CheckConstraint("ck_game_versions_published_fields", "status NOT IN ('PUBLISHED', 'BLOCKED') OR (content_digest IS NOT NULL AND published_at IS NOT NULL)");
                    table.CheckConstraint("ck_game_versions_runtime_config_json", "length(runtime_config_json) BETWEEN 2 AND 1048576 AND json_valid(runtime_config_json) = 1 AND runtime_config_json <> ''");
                    table.CheckConstraint("ck_game_versions_state_version", "state_version >= 0");
                    table.CheckConstraint("ck_game_versions_status", "status IN ('DRAFT', 'VALIDATING', 'PUBLISHED', 'BLOCKED', 'DELETED')");
                    table.CheckConstraint("ck_game_versions_time_order", "created_at >= 0 AND (published_at IS NULL OR published_at >= created_at)");
                    table.CheckConstraint("ck_game_versions_version_label", "length(version_label) BETWEEN 1 AND 100 AND instr(version_label, char(0)) = 0");
                    table.ForeignKey(
                        name: "fk_game_versions_creator",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_game_versions_game",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_user_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    game_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    game_version_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    runtime_version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    session_root_path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    worker_epoch = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    waiting_for_input = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    current_prompt_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    last_output_sequence = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    close_reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_activity_at = table.Column<long>(type: "INTEGER", nullable: false),
                    closed_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.UniqueConstraint("ak_sessions_id_worker_epoch", x => new { x.id, x.worker_epoch });
                    table.CheckConstraint("ck_sessions_close_reason", "close_reason IS NULL OR (length(close_reason) BETWEEN 1 AND 256 AND instr(close_reason, char(0)) = 0)");
                    table.CheckConstraint("ck_sessions_closed_fields", "(state = 'CLOSED' AND closed_at IS NOT NULL) OR (state <> 'CLOSED' AND closed_at IS NULL)");
                    table.CheckConstraint("ck_sessions_counters", "state_version >= 0 AND worker_epoch >= 0 AND last_output_sequence >= 0");
                    table.CheckConstraint("ck_sessions_game_id", "substr(game_id, 1, 5) = 'game_' AND length(game_id) BETWEEN 5 AND 64 AND instr(game_id, char(0)) = 0");
                    table.CheckConstraint("ck_sessions_game_version_id", "substr(game_version_id, 1, 5) = 'gver_' AND length(game_version_id) BETWEEN 5 AND 64 AND instr(game_version_id, char(0)) = 0");
                    table.CheckConstraint("ck_sessions_id", "substr(id, 1, 5) = 'sess_' AND length(id) BETWEEN 5 AND 64 AND instr(id, char(0)) = 0");
                    table.CheckConstraint("ck_sessions_name", "length(name) BETWEEN 1 AND 200 AND instr(name, char(0)) = 0");
                    table.CheckConstraint("ck_sessions_owner_id", "substr(owner_user_id, 1, 4) = 'usr_' AND length(owner_user_id) BETWEEN 5 AND 64 AND instr(owner_user_id, char(0)) = 0");
                    table.CheckConstraint("ck_sessions_root_path", "length(session_root_path) BETWEEN 1 AND 512 AND substr(session_root_path, 1, 1) <> '/' AND instr(session_root_path, char(92)) = 0 AND instr(session_root_path, char(0)) = 0 AND instr(session_root_path, '//') = 0 AND instr('/' || session_root_path || '/', '/./') = 0 AND instr('/' || session_root_path || '/', '/../') = 0");
                    table.CheckConstraint("ck_sessions_runtime_version", "length(runtime_version) BETWEEN 1 AND 128 AND instr(runtime_version, char(0)) = 0");
                    table.CheckConstraint("ck_sessions_state", "state IN ('CREATING', 'STARTING', 'RUNNING', 'DETACHED', 'STOPPING', 'CLOSED', 'CRASHED')");
                    table.CheckConstraint("ck_sessions_time_order", "created_at >= 0 AND last_activity_at >= created_at AND (started_at IS NULL OR started_at >= created_at) AND (closed_at IS NULL OR closed_at >= created_at)");
                    table.CheckConstraint("ck_sessions_waiting_prompt", "waiting_for_input IN (0, 1) AND ((waiting_for_input = 1 AND current_prompt_id IS NOT NULL AND length(current_prompt_id) BETWEEN 1 AND 256) OR (waiting_for_input = 0 AND current_prompt_id IS NULL))");
                    table.ForeignKey(
                        name: "fk_sessions_game",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sessions_game_version_game",
                        columns: x => new { x.game_version_id, x.game_id },
                        principalTable: "game_versions",
                        principalColumns: new[] { "id", "game_id" },
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sessions_owner_user",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "worker_leases",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    worker_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    pid = table.Column<long>(type: "INTEGER", nullable: true),
                    ipc_endpoint = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    runtime_version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    protocol_version = table.Column<int>(type: "INTEGER", nullable: false),
                    acquired_at = table.Column<long>(type: "INTEGER", nullable: false),
                    heartbeat_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker_leases", x => x.session_id);
                    table.CheckConstraint("ck_worker_leases_epoch", "epoch > 0");
                    table.CheckConstraint("ck_worker_leases_ipc_endpoint", "length(ipc_endpoint) BETWEEN 1 AND 512 AND substr(ipc_endpoint, 1, 1) <> '/' AND instr(ipc_endpoint, char(92)) = 0 AND instr(ipc_endpoint, char(0)) = 0 AND instr(ipc_endpoint, '://') = 0 AND instr(ipc_endpoint, '//') = 0");
                    table.CheckConstraint("ck_worker_leases_pid", "pid IS NULL OR pid > 0");
                    table.CheckConstraint("ck_worker_leases_protocol_version", "protocol_version > 0");
                    table.CheckConstraint("ck_worker_leases_runtime_version", "length(runtime_version) BETWEEN 1 AND 128 AND instr(runtime_version, char(0)) = 0");
                    table.CheckConstraint("ck_worker_leases_session_id", "substr(session_id, 1, 5) = 'sess_' AND length(session_id) BETWEEN 5 AND 64 AND instr(session_id, char(0)) = 0");
                    table.CheckConstraint("ck_worker_leases_status", "status IN ('STARTING', 'ACTIVE', 'STOPPING', 'EXPIRED')");
                    table.CheckConstraint("ck_worker_leases_time_order", "acquired_at >= 0 AND heartbeat_at >= acquired_at AND expires_at > heartbeat_at");
                    table.CheckConstraint("ck_worker_leases_worker_id", "substr(worker_id, 1, 4) = 'wrk_' AND length(worker_id) BETWEEN 5 AND 128 AND instr(worker_id, char(0)) = 0");
                    table.ForeignKey(
                        name: "fk_worker_leases_session_epoch",
                        columns: x => new { x.session_id, x.epoch },
                        principalTable: "sessions",
                        principalColumns: new[] { "id", "worker_epoch" },
                        onUpdate: ReferentialAction.Restrict,
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("CREATE TRIGGER \"trg_audit_events_append_only_update\" BEFORE UPDATE ON \"audit_events\" BEGIN SELECT RAISE(ABORT, 'audit_events is append-only'); END;");
            migrationBuilder.Sql("CREATE TRIGGER \"trg_audit_events_append_only_delete\" BEFORE DELETE ON \"audit_events\" BEGIN SELECT RAISE(ABORT, 'audit_events is append-only'); END;");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_time",
                table: "audit_events",
                columns: new[] { "actor_user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_resource_time",
                table: "audit_events",
                columns: new[] { "resource_type", "resource_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_game_versions_created_by",
                table: "game_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ux_game_versions_content_digest",
                table: "game_versions",
                column: "content_digest",
                unique: true,
                filter: "content_digest IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_game_versions_content_path",
                table: "game_versions",
                column: "content_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_game_versions_game_label",
                table: "game_versions",
                columns: new[] { "game_id", "version_label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_games_owner_name",
                table: "games",
                columns: new[] { "owner_user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_quota_profiles_name",
                table: "quota_profiles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_game",
                table: "sessions",
                column: "game_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_game_version",
                table: "sessions",
                column: "game_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_game_version_game",
                table: "sessions",
                columns: new[] { "game_version_id", "game_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_owner_created",
                table: "sessions",
                columns: new[] { "owner_user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_state_activity",
                table: "sessions",
                columns: new[] { "state", "last_activity_at" });

            migrationBuilder.CreateIndex(
                name: "ix_users_quota_profile",
                table: "users",
                column: "quota_profile_id");

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_login_name",
                table: "users",
                column: "normalized_login_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_worker_leases_session_epoch",
                table: "worker_leases",
                columns: new[] { "session_id", "epoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_worker_leases_worker_id",
                table: "worker_leases",
                column: "worker_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"trg_audit_events_append_only_update\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"trg_audit_events_append_only_delete\";");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "worker_leases");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "game_versions");

            migrationBuilder.DropTable(
                name: "games");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "quota_profiles");
        }
    }
}

#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source "$repo_root/scripts/lib/dev-env.sh"

command -v curl >/dev/null || { echo "test-instance-limits.sh requires curl" >&2; exit 127; }

temp_root="$(mktemp -d "${TMPDIR:-/tmp}/cloudemuera-limits.XXXXXX")"
project_name="cloudemuera-limits-${RANDOM}-${RANDOM}"
http_port="${CLOUDEMUERA_LIMITS_TEST_PORT:-$((29000 + RANDOM % 1000))}"
env_file="$temp_root/limits.env"
bad_env_file="$temp_root/invalid.env"
base_url="http://127.0.0.1:${http_port}"
compose=(docker compose --env-file "$env_file" --project-name "$project_name" --file "$repo_root/docker/compose.dev.yml")

cleanup() {
  "${compose[@]}" down --remove-orphans --volumes >/dev/null 2>&1 || true
  rm -rf "$temp_root"
}
trap cleanup EXIT

# These values are deliberately small but keep every documented cross-group
# relationship valid. Exact boundary cases remain in xUnit; this script checks
# the deployment binder, the real API startup, and a temporary cross-process
# instance.
printf '%s\n' \
  "CLOUDEMUERA_UID=$CLOUDEMUERA_UID" \
  "CLOUDEMUERA_GID=$CLOUDEMUERA_GID" \
  "CLOUDEMUERA_DEV_HTTP_PORT=$http_port" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=limits-admin" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=limits-admin@example.test" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password" \
  "CLOUDEMUERA_CAPACITY_MAX_ACTIVE_WORKERS=1" \
  "CLOUDEMUERA_CAPACITY_MAX_INACTIVE_SESSIONS=1" \
  "CLOUDEMUERA_CAPACITY_MAX_ARCHIVE_BYTES=1024" \
  "CLOUDEMUERA_CAPACITY_MAX_EXPANDED_BYTES=4096" \
  "CLOUDEMUERA_CAPACITY_MAX_ARCHIVE_SINGLE_FILE_BYTES=2048" \
  "CLOUDEMUERA_CAPACITY_MAX_ARCHIVE_ENTRY_COUNT=8" \
  "CLOUDEMUERA_CAPACITY_MAX_SESSION_ROOT_BYTES=4096" \
  "CLOUDEMUERA_CAPACITY_MAX_SESSION_ROOT_FILE_COUNT=8" \
  "CLOUDEMUERA_CAPACITY_MAX_STAGING_RESERVED_BYTES=8192" \
  "CLOUDEMUERA_CAPACITY_MAX_SAVE_FILE_BYTES=1024" \
  "CLOUDEMUERA_CAPACITY_MAX_SAVE_LISTED_FILES=2" \
  "CLOUDEMUERA_CAPACITY_MAX_SAVE_LIST_BYTES=2048" \
  "CLOUDEMUERA_CAPACITY_MIN_DATA_ROOT_FREE_BYTES=0" \
  "CLOUDEMUERA_REALTIME_SNAPSHOT_MAX_BYTES=8192" \
  "CLOUDEMUERA_REALTIME_BATCH_TARGET_BYTES=512" \
  "CLOUDEMUERA_REALTIME_BATCH_MAX_TRANSACTIONS=2" \
  "CLOUDEMUERA_REALTIME_QUEUE_SOFT_BYTES=1024" \
  "CLOUDEMUERA_REALTIME_QUEUE_HARD_BYTES=2048" \
  "CLOUDEMUERA_REALTIME_QUEUE_SOFT_MESSAGES=2" \
  "CLOUDEMUERA_REALTIME_QUEUE_HARD_MESSAGES=4" \
  "CLOUDEMUERA_REALTIME_MAX_CONNECTIONS=2" \
  "CLOUDEMUERA_REALTIME_MAX_CONNECTIONS_PER_SESSION=1" \
  "CLOUDEMUERA_REALTIME_MAX_PENDING_INPUTS_PER_CONNECTION=2" \
  "CLOUDEMUERA_REALTIME_MAX_PENDING_INPUTS_PER_WORKER=2" \
  "CLOUDEMUERA_REALTIME_CONTROL_QUEUE_MAX_BYTES=1024" \
  "CLOUDEMUERA_REALTIME_CONTROL_QUEUE_MAX_MESSAGES=4" \
  "CLOUDEMUERA_REALTIME_ENVELOPE_MAX_BYTES=512" \
  "CLOUDEMUERA_WORKER_PENDING_EVENT_MAX_BYTES=4096" \
  "CLOUDEMUERA_WORKER_PENDING_EVENT_MAX_MESSAGES=4" \
  "CLOUDEMUERA_ASSETS_MAX_MANIFEST_BYTES=1024" \
  "CLOUDEMUERA_ASSETS_MAX_ASSET_BYTES=1024" \
  "CLOUDEMUERA_ASSETS_MAX_RANGE_BYTES=512" \
  "CLOUDEMUERA_ASSETS_MAX_CONCURRENT_READS=1" \
  "CLOUDEMUERA_ASSETS_MAX_IN_FLIGHT_BYTES=1024" \
  > "$env_file"

"${compose[@]}" config >/dev/null
"${compose[@]}" run --rm api dotnet restore CloudEmuera.slnx --locked-mode
"${compose[@]}" run --rm api dotnet build src/CloudEmuera.Api/CloudEmuera.Api.csproj --configuration Debug
"${compose[@]}" stop api >/dev/null 2>&1 || true
"${compose[@]}" run --rm api dotnet run --project src/CloudEmuera.Migrator -- migrate --data-root /data
"${compose[@]}" up --detach api
wait_for_http() {
  local path="$1"
  for _ in $(seq 1 45); do
    if curl --fail --silent --show-error --max-time 5 "$base_url$path" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  echo "timed out waiting for $path" >&2
  return 1
}
wait_for_http /health/live
wait_for_http /health/ready

# A second startup with an invalid cross-relation must fail before it can
# listen. This catches accidental clamping or a forgotten composition-root
# validator without touching the running temporary project.
printf '%s\n' \
  "CLOUDEMUERA_UID=$CLOUDEMUERA_UID" \
  "CLOUDEMUERA_GID=$CLOUDEMUERA_GID" \
  "CLOUDEMUERA_CAPACITY_MAX_ARCHIVE_BYTES=8192" \
  "CLOUDEMUERA_CAPACITY_MAX_EXPANDED_BYTES=8192" \
  "CLOUDEMUERA_CAPACITY_MAX_SESSION_ROOT_BYTES=4096" \
  "CLOUDEMUERA_CAPACITY_MAX_STAGING_RESERVED_BYTES=16384" \
  "CLOUDEMUERA_CAPACITY_MIN_DATA_ROOT_FREE_BYTES=0" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=invalid-admin" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=invalid-admin@example.test" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password" \
  > "$bad_env_file"
bad_project="${project_name}-invalid"
bad_output="$temp_root/invalid-startup.log"
set +e
docker compose --env-file "$bad_env_file" --project-name "$bad_project" -f "$repo_root/docker/compose.dev.yml" run --rm api \
  dotnet /workspace/src/CloudEmuera.Api/bin/Debug/net10.0/CloudEmuera.Api.dll > "$bad_output" 2>&1
bad_status=$?
set -e
docker compose --env-file "$bad_env_file" --project-name "$bad_project" -f "$repo_root/docker/compose.dev.yml" down --remove-orphans --volumes >/dev/null 2>&1 || true
if [[ "$bad_status" == 0 ]] || ! grep -Eiq 'capacity|sessionroot|inconsistent|invalid' "$bad_output"; then
  echo "invalid capacity configuration unexpectedly started" >&2
  cat "$bad_output" >&2
  exit 1
fi

# Run the exact boundary tests in the required dev image. They use their own
# temporary fixtures, while the live API above proves the deployment values
# were accepted by the actual composition root.
"${compose[@]}" run --rm api dotnet test tests/CloudEmuera.Infrastructure.Tests \
  --no-restore --configuration Release --filter 'Category=InstanceLimits|Category=ArchiveQuota|Category=SavePathSecurity'
"${compose[@]}" run --rm api dotnet test tests/CloudEmuera.Api.IntegrationTests \
  --no-restore --configuration Release --filter 'FullyQualifiedName~WorkerProcessEnvironmentTests'

echo "instance capacity binder, startup cross-check, save/asset gates, and Worker environment limits passed"

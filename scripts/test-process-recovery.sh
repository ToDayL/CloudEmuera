#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source "$repo_root/scripts/lib/dev-env.sh"

command -v curl >/dev/null || { echo "test-process-recovery.sh requires curl" >&2; exit 127; }
command -v jq >/dev/null || { echo "test-process-recovery.sh requires jq" >&2; exit 127; }
command -v sha256sum >/dev/null || { echo "test-process-recovery.sh requires sha256sum" >&2; exit 127; }
command -v python3 >/dev/null || { echo "test-process-recovery.sh requires python3" >&2; exit 127; }

temp_root="$(mktemp -d "${TMPDIR:-/tmp}/cloudemuera-recovery.XXXXXX")"
data_root="$temp_root/data"
backup_root="$temp_root/backup"
restore_root="$temp_root/restore"
mkdir -m 700 "$data_root" "$backup_root" "$restore_root"
project_name="cloudemuera-recovery-${RANDOM}-${RANDOM}"
restore_project_name="${project_name}-restore"
image_name="cloudemuera:recovery-${RANDOM}-${RANDOM}"
http_port="${CLOUDEMUERA_RECOVERY_TEST_PORT:-$((29000 + RANDOM % 1000))}"
restore_http_port="$((http_port + 1))"
env_file="$temp_root/recovery.env"
restore_env_file="$temp_root/recovery-restore.env"
cookie_jar="$temp_root/cookies.txt"
base_url="http://127.0.0.1:${http_port}"

compose=(docker compose --env-file "$env_file" --project-name "$project_name" --file "$repo_root/docker/compose.yml")
restore_compose=(docker compose --env-file "$restore_env_file" --project-name "$restore_project_name" --file "$repo_root/docker/compose.yml")

cleanup() {
  "${restore_compose[@]}" down --remove-orphans --volumes >/dev/null 2>&1 || true
  "${compose[@]}" down --remove-orphans --volumes >/dev/null 2>&1 || true
  docker image rm "$image_name" >/dev/null 2>&1 || true
  chmod -R u+rwX "$data_root" "$backup_root" "$restore_root" >/dev/null 2>&1 || true
  rm -rf "$temp_root"
}
trap cleanup EXIT

write_env() {
  local target="$1" path="$2" port="$3"
  printf '%s\n' \
    "CLOUDEMUERA_PRODUCTION_IMAGE=$image_name" \
    "CLOUDEMUERA_HTTP_PORT=$port" \
    "CLOUDEMUERA_CONTAINER_PORT=28647" \
    "CLOUDEMUERA_UID=$CLOUDEMUERA_UID" \
    "CLOUDEMUERA_GID=$CLOUDEMUERA_GID" \
    "CLOUDEMUERA_DATA_PATH=$path" \
    "CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=recovery-admin" \
    "CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=recovery-admin@example.test" \
    "CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password" \
    > "$target"
}

write_env "$env_file" "$data_root" "$http_port"

wait_for_http() {
  local path="$1"
  for _ in $(seq 1 60); do
    if curl --fail --silent --show-error --max-time 5 "$base_url$path" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  echo "timed out waiting for $base_url$path" >&2
  return 1
}

get_csrf() {
  curl --fail --silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
    "$base_url/api/v1/auth/csrf" | jq -er '.token'
}

request_json() {
  local method="$1" path="$2" body="$3" csrf="$4" output="$5" state_version="${6:-}" idempotency="${7:-}"
  local -a args=(--silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" -o "$output" -w '%{http_code}'
    -X "$method" -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $csrf" --data "$body")
  if [[ -n "$state_version" ]]; then args+=(--header "If-Match: \"$state_version\""); fi
  if [[ -n "$idempotency" ]]; then args+=(--header "Idempotency-Key: $idempotency"); fi
  curl "${args[@]}" "$base_url$path"
}

login() {
  local password="$1"
  local csrf output="$temp_root/login.json"
  csrf="$(get_csrf)"
  local status
  status="$(request_json POST /api/v1/auth/login \
    "{\"email\":\"recovery-admin@example.test\",\"password\":\"$password\",\"rememberMe\":false}" \
    "$csrf" "$output")"
  [[ "$status" == 200 ]] || { cat "$output" >&2; return 1; }
}

wait_for_session_state() {
  local expected="$1"
  local state=''
  for _ in $(seq 1 60); do
    state="$(curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/sessions/$session_id" | jq -r '.state')"
    [[ "$state" == "$expected" ]] && return 0
    sleep 0.25
  done
  echo "Session $session_id did not reach $expected (last=$state)" >&2
  return 1
}

runtime_epoch() {
  curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/admin/workers" |
    jq -er --arg session_id "$session_id" '.workers[] | select(.session.id == $session_id) | .worker.workerEpoch'
}

"${compose[@]}" build api
"${compose[@]}" up --detach
wait_for_http /health/live
wait_for_http /health/ready
login temporary-password

csrf="$(get_csrf)"
status="$(request_json POST /api/v1/auth/change-password \
  '{"currentPassword":"temporary-password","newPassword":"administrator-password"}' \
  "$csrf" "$temp_root/password.json")"
[[ "$status" == 204 ]] || { cat "$temp_root/password.json" >&2; exit 1; }

csrf="$(get_csrf)"
status="$(request_json POST /api/v1/games \
  '{"name":"process-recovery-game","visibility":"PRIVATE"}' \
  "$csrf" "$temp_root/game.json")"
[[ "$status" == 201 ]] || { cat "$temp_root/game.json" >&2; exit 1; }
game_id="$(jq -er '.id' "$temp_root/game.json")"
game_version="$(jq -er '.stateVersion' "$temp_root/game.json")"

fixture_dir="$temp_root/fixture"
mkdir -p "$fixture_dir/CSV" "$fixture_dir/ERB"
printf 'title,process-recovery\n' > "$fixture_dir/CSV/GAMEBASE.CSV"
printf '@SYSTEM_TITLE\nINPUT\nQUIT\n' > "$fixture_dir/ERB/START.ERB"
printf 'Use sav folder:NO\n' > "$fixture_dir/emuera.config"
printf 'P1-14 session root survives process recovery\n' > "$fixture_dir/recovery-marker.txt"
archive="$temp_root/fixture.zip"
python3 - "$fixture_dir" "$archive" <<'PY'
import pathlib
import sys
import zipfile

source = pathlib.Path(sys.argv[1])
target = pathlib.Path(sys.argv[2])
with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(source.rglob("*")):
        if path.is_file():
            archive.write(path, path.relative_to(source).as_posix())
PY

csrf="$(get_csrf)"
ingest_output="$temp_root/ingestion.json"
status="$(curl --silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  -o "$ingest_output" -w '%{http_code}' -X POST "$base_url/api/v1/game-package-ingestions" \
  -H 'Content-Type: application/zip' -H "X-CSRF-TOKEN: $csrf" -H 'Idempotency-Key: recovery-ingest' \
  --data-binary "@$archive")"
[[ "$status" == 201 ]] || { cat "$ingest_output" >&2; exit 1; }
ingestion_id="$(jq -er '.ingestionId' "$ingest_output")"
content_digest="$(jq -er '.manifest.contentDigest' "$ingest_output")"

csrf="$(get_csrf)"
status="$(request_json PUT "/api/v1/games/$game_id/package" \
  "{\"ingestionId\":\"$ingestion_id\",\"contentDigest\":\"$content_digest\"}" \
  "$csrf" "$temp_root/bind.json" "$game_version" recovery-bind)"
[[ "$status" == 200 ]] || { cat "$temp_root/bind.json" >&2; exit 1; }
draft_version="$(jq -er '.stateVersion' "$temp_root/bind.json")"

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/games/$game_id:validate" '{}' "$csrf" "$temp_root/validate.json" "$draft_version" recovery-validate)"
[[ "$status" == 200 ]] || { cat "$temp_root/validate.json" >&2; exit 1; }
validation_version="$(jq -er '.stateVersion' "$temp_root/validate.json")"

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/games/$game_id:activate" '{}' "$csrf" "$temp_root/activate.json" "$validation_version" recovery-activate)"
[[ "$status" == 200 ]] || { cat "$temp_root/activate.json" >&2; exit 1; }

csrf="$(get_csrf)"
status="$(request_json POST /api/v1/sessions \
  "{\"gameId\":\"$game_id\",\"name\":\"process-recovery-session\"}" \
  "$csrf" "$temp_root/session.json" '' recovery-session-create)"
[[ "$status" == 201 ]] || { cat "$temp_root/session.json" >&2; exit 1; }
session_id="$(jq -er '.id' "$temp_root/session.json")"

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/sessions/$session_id:open" \
  '{"browserWidth":1024}' \
  "$csrf" "$temp_root/open.json" '' recovery-session-open)"
[[ "$status" == 200 || "$status" == 202 ]] || { cat "$temp_root/open.json" >&2; exit 1; }
wait_for_session_state RUNNING

session_root="$data_root/sessions/$session_id/root"
marker_file="$session_root/recovery-marker.txt"
test -f "$marker_file"
marker_hash="$(sha256sum "$marker_file" | awk '{print $1}')"
first_epoch="$(runtime_epoch)"

stop_started="$(date +%s%3N)"
"${compose[@]}" stop api
stop_finished="$(date +%s%3N)"
stop_elapsed="$((stop_finished - stop_started))"
[[ "$stop_elapsed" -le 20000 ]] || { echo "API SIGTERM exceeded the 20s Compose grace period: ${stop_elapsed}ms" >&2; exit 1; }
api_container_id="$("${compose[@]}" ps -aq api)"
[[ "$(docker inspect --format '{{.State.Running}}' "$api_container_id")" == false ]]

"${compose[@]}" up --detach api
wait_for_http /health/live
wait_for_http /health/ready
login administrator-password
wait_for_session_state CRASHED
test "$(sha256sum "$marker_file" | awk '{print $1}')" == "$marker_hash"

save_file="$temp_root/global.sav"
printf '0\n0\n' > "$save_file"
csrf="$(get_csrf)"
status="$(curl --silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  -o "$temp_root/save-import.json" -w '%{http_code}' -X PUT \
  "$base_url/api/v1/sessions/$session_id/saves/global.sav" \
  -H 'Content-Type: application/octet-stream' -H "X-CSRF-TOKEN: $csrf" \
  -H 'Idempotency-Key: recovery-save-import' --data-binary "@$save_file")"
[[ "$status" == 201 ]] || { cat "$temp_root/save-import.json" >&2; exit 1; }
save_hash="$(sha256sum "$session_root/global.sav" | awk '{print $1}')"

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/sessions/$session_id:open" \
  '{"browserWidth":1024}' \
  "$csrf" "$temp_root/reopen.json" '' recovery-session-reopen)"
[[ "$status" == 200 || "$status" == 202 ]] || { cat "$temp_root/reopen.json" >&2; exit 1; }
wait_for_session_state RUNNING
second_epoch="$(runtime_epoch)"
(( second_epoch > first_epoch )) || { echo "reopen did not fence with a larger Worker epoch" >&2; exit 1; }

worker_pid="$(curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/admin/workers" | jq -er --arg session_id "$session_id" '.workers[] | select(.session.id == $session_id) | .worker.pid')"
"${compose[@]}" exec --no-TTY api sh -euc 'kill -KILL "$1"' sh "$worker_pid"
wait_for_http /health/live
wait_for_session_state CRASHED

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/sessions/$session_id:open" \
  '{"browserWidth":1024}' \
  "$csrf" "$temp_root/reopen-after-worker-kill.json" '' recovery-session-reopen-worker-kill)"
[[ "$status" == 200 || "$status" == 202 ]] || { cat "$temp_root/reopen-after-worker-kill.json" >&2; exit 1; }
wait_for_session_state RUNNING
third_epoch="$(runtime_epoch)"
(( third_epoch > second_epoch )) || { echo "Worker crash reopen did not advance the epoch" >&2; exit 1; }

api_container_id="$("${compose[@]}" ps -q api)"
docker kill --signal SIGKILL "$api_container_id" >/dev/null
for _ in $(seq 1 30); do
  [[ "$(docker inspect --format '{{.State.Running}}' "$api_container_id")" == false ]] && break
  sleep 0.25
done
[[ "$(docker inspect --format '{{.State.Running}}' "$api_container_id")" == false ]]

"${compose[@]}" up --detach api
wait_for_http /health/live
wait_for_http /health/ready
login administrator-password
wait_for_session_state CRASHED
test "$(sha256sum "$marker_file" | awk '{print $1}')" == "$marker_hash"
test "$(sha256sum "$session_root/global.sav" | awk '{print $1}')" == "$save_hash"

# A cold backup copies the complete DataRoot, including SQLite sidecars,
# Data Protection keys, games, SessionRoots and native saves.
"${compose[@]}" stop api
cp -a "$data_root/." "$backup_root/"
cp -a "$backup_root/." "$restore_root/"
write_env "$restore_env_file" "$restore_root" "$restore_http_port"
base_url="http://127.0.0.1:${restore_http_port}"
cookie_jar="$temp_root/restore-cookies.txt"

# A cold restore changes directory inodes. Rebind protected Game and
# SessionRoot identity fields while the API is offline; all durable
# session/game/content identities must still match the restored database.
"${restore_compose[@]}" run --rm --no-deps api rebind-session-roots
"${restore_compose[@]}" up --detach
wait_for_http /health/live
wait_for_http /health/ready
login administrator-password
restored_session_state="$(curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/sessions/$session_id" | jq -er '.state')"
[[ "$restored_session_state" == CRASHED ]] || { echo "restored Session state is $restored_session_state" >&2; exit 1; }
curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/games" | jq -e --arg game_id "$game_id" '.items[] | select(.id == $game_id)' >/dev/null
curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/sessions/$session_id/saves" |
  jq -e '.items[] | select(.path == "global.sav")' >/dev/null
test "$(sha256sum "$restore_root/sessions/$session_id/root/recovery-marker.txt" | awk '{print $1}')" == "$marker_hash"
test "$(sha256sum "$restore_root/sessions/$session_id/root/global.sav" | awk '{print $1}')" == "$save_hash"

# Startup reconciliation may finish a durable lifecycle command immediately
# after readiness. Retry only its explicit transition response; all other
# failures remain fatal.
restore_open_status=''
for attempt in $(seq 1 20); do
  csrf="$(get_csrf)"
  restore_open_status="$(request_json POST "/api/v1/sessions/$session_id:open" \
    '{"browserWidth":1024}' \
    "$csrf" "$temp_root/restore-reopen.json" '' "recovery-restore-reopen-$attempt")"
  if [[ "$restore_open_status" == 200 || "$restore_open_status" == 202 ]]; then
    break
  fi
  if [[ "$restore_open_status" != 409 ]] ||
    [[ "$(jq -r '.code // empty' "$temp_root/restore-reopen.json")" != "SESSION_TRANSITION_IN_PROGRESS" ]]; then
    cat "$temp_root/restore-reopen.json" >&2
    exit 1
  fi
  wait_for_session_state CRASHED
  sleep 0.25
done
[[ "$restore_open_status" == 200 || "$restore_open_status" == 202 ]] || { cat "$temp_root/restore-reopen.json" >&2; exit 1; }
wait_for_session_state RUNNING
restored_epoch="$(runtime_epoch)"
(( restored_epoch > third_epoch )) || { echo "restored reopen did not advance the Worker epoch" >&2; exit 1; }

echo "process, parent-death, SessionRoot, native save, and cold DataRoot recovery passed (stop=${stop_elapsed}ms)"

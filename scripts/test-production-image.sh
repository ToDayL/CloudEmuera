#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source "$repo_root/scripts/lib/dev-env.sh"

command -v curl >/dev/null || { echo "test-production-image.sh requires curl" >&2; exit 127; }
command -v jq >/dev/null || { echo "test-production-image.sh requires jq" >&2; exit 127; }
command -v python3 >/dev/null || { echo "test-production-image.sh requires python3" >&2; exit 127; }

temp_root="$(mktemp -d "${TMPDIR:-/tmp}/cloudemuera-production.XXXXXX")"
data_root="$temp_root/data"
mkdir -m 700 "$data_root"
project_name="cloudemuera-production-${RANDOM}-${RANDOM}"
named_project_name="${project_name}-named"
image_name="cloudemuera:production-${RANDOM}-${RANDOM}"
http_port="${CLOUDEMUERA_PRODUCTION_TEST_PORT:-$((28000 + RANDOM % 1000))}"
env_file="$temp_root/production.env"
named_env_file="$temp_root/production-named.env"
cookie_jar="$temp_root/cookies.txt"
base_url="http://127.0.0.1:${http_port}"

compose=(docker compose --env-file "$env_file" --project-name "$project_name" --file "$repo_root/docker/compose.yml")
# dev-env.sh exports the host identity for bind mounts. Explicitly remove it
# for the named-volume case so Compose exercises the root-default contract.
named_compose=(env -u CLOUDEMUERA_UID -u CLOUDEMUERA_GID docker compose --env-file "$named_env_file" --project-name "$named_project_name" --file "$repo_root/docker/compose.yml")

cleanup() {
  "${named_compose[@]}" down --remove-orphans --volumes >/dev/null 2>&1 || true
  "${compose[@]}" down --remove-orphans --volumes >/dev/null 2>&1 || true
  docker image rm "$image_name" >/dev/null 2>&1 || true
  # Published Game content is intentionally read-only. Restore owner write
  # bits before removing this explicitly-created temporary bind mount.
  chmod -R u+rwX "$data_root" >/dev/null 2>&1 || true
  rm -rf "$temp_root"
}
trap cleanup EXIT

printf '%s\n' \
  "CLOUDEMUERA_PRODUCTION_IMAGE=$image_name" \
  "CLOUDEMUERA_HTTP_PORT=$http_port" \
  "CLOUDEMUERA_UID=$CLOUDEMUERA_UID" \
  "CLOUDEMUERA_GID=$CLOUDEMUERA_GID" \
  "CLOUDEMUERA_DATA_PATH=$data_root" \
  "CLOUDEMUERA_CONTAINER_PORT=28647" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=production-admin" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=production-admin@example.test" \
  "CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password" \
  "CLOUDEMUERA_TEST_DATA_ROOT=$data_root" \
  > "$env_file"

# Removing the path variable exercises the user-facing default: a Docker
# named volume. Remove the optional UID/GID too: this is the root-default
# path, while the bind-mount smoke below uses the current host identity.
sed '/^CLOUDEMUERA_DATA_PATH=/d; /^CLOUDEMUERA_UID=/d; /^CLOUDEMUERA_GID=/d' "$env_file" > "$named_env_file"

config_file="$temp_root/compose-config.json"
"${compose[@]}" config --format json > "$config_file"
if jq -e '
  (.services.api.privileged == true) or
  ((.services.api.volumes // []) |
    any(.[]; ((.source // "") | test("docker\\.sock|/workspace|/root|/home/|worker-control\\.sock"))))
' "$config_file" >/dev/null; then
  echo "production Compose contains a forbidden host or privileged boundary" >&2
  exit 1
fi
jq -e --arg data_root "$data_root" --arg uid "$CLOUDEMUERA_UID" --arg gid "$CLOUDEMUERA_GID" '
  ((.services | keys) == ["api"]) and
  (.services.api.user == ($uid + ":" + $gid)) and
  (.services.api.init == true) and
  (.services.api.stop_signal == "SIGTERM") and
  (.services.api.stop_grace_period == "20s") and
  ((.services.api | has("cpus")) | not) and
  (([.services.api.environment // {} | keys[] | select(startswith("CloudEmuera__Capacity__"))] | length) == 0) and
  ((.services.api.ports // []) | length == 1 and .[0].host_ip == "127.0.0.1") and
  ((.services.api.volumes // []) | length == 1 and .[0].type == "bind" and .[0].target == "/data" and .[0].source == $data_root)
' "$config_file" >/dev/null

named_config_file="$temp_root/compose-named-config.json"
"${named_compose[@]}" config --format json > "$named_config_file"
jq -e '
  ((.services | keys) == ["api"]) and
  (.services.api.user == "0:0") and
  ((.services.api.ports // []) | length == 1 and .[0].host_ip == "127.0.0.1") and
  ((.services.api.volumes // []) | length == 1 and .[0].type == "volume" and .[0].target == "/data" and .[0].source == "cloudemuera-data")
' "$named_config_file" >/dev/null

"${compose[@]}" build api

image_user="$(docker image inspect "$image_name" --format '{{.Config.User}}')"
[[ -z "$image_user" ]] || { echo "production image must keep the default root identity: $image_user" >&2; exit 1; }
image_stop_signal="$(docker image inspect "$image_name" --format '{{.Config.StopSignal}}')"
[[ "$image_stop_signal" == "SIGTERM" ]] || { echo "production image stop signal is not SIGTERM: $image_stop_signal" >&2; exit 1; }
image_entrypoint="$(docker image inspect "$image_name" --format '{{json .Config.Entrypoint}}')"
[[ "$image_entrypoint" == '["/app/start.sh"]' ]] || { echo "production image entrypoint is not /app/start.sh: $image_entrypoint" >&2; exit 1; }
if grep -Eiq 'supervisor|s6|systemd' <<<"$image_entrypoint"; then
  echo "production image contains a forbidden resident process manager: $image_entrypoint" >&2
  exit 1
fi

# The production image must contain all process artifacts without a checkout
# mount. Verify that the root-default named volume and the bind-mount image
# both remain writable.
"${named_compose[@]}" run --rm --no-deps --entrypoint sh api -c 'touch /data/.cloudemuera-named-volume-probe && rm /data/.cloudemuera-named-volume-probe'
"${compose[@]}" run --rm --no-deps --entrypoint sh api -c 'test -f /app/worker/CloudEmuera.Worker.dll -a -f /app/debugger/CloudEmuera.Debugger.dll -a -f /app/validator/CloudEmuera.Validator.dll -a -f /app/migrator/CloudEmuera.Migrator.dll'

# The only production start command is Compose up. The image entrypoint runs
# Migrator synchronously and then replaces itself with API.
"${compose[@]}" up --detach
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

container_id="$("${compose[@]}" ps -q api)"
api_uid="$("${compose[@]}" exec --no-TTY api id -u | tr -d '\r\n')"
api_gid="$("${compose[@]}" exec --no-TTY api id -g | tr -d '\r\n')"
if [[ "$api_uid" != "$CLOUDEMUERA_UID" || "$api_gid" != "$CLOUDEMUERA_GID" ]]; then
  echo "API is not running as the deployer identity: ${api_uid}:${api_gid}" >&2
  exit 1
fi

inspect_file="$temp_root/container-inspect.json"
docker inspect "$container_id" > "$inspect_file"
jq -e '.[0].HostConfig.Privileged == false and (.[0].HostConfig.PidsLimit == 512) and (.[0].HostConfig.NanoCpus == 0) and (.[0].HostConfig.Memory == 2147483648)' "$inspect_file" >/dev/null
jq -e --arg data_root "$data_root" '.[0].Mounts | length == 1 and .[0].Type == "bind" and .[0].Source == $data_root and .[0].Destination == "/data"' "$inspect_file" >/dev/null
jq -e '.[0].NetworkSettings.Ports | has("28647/tcp") and length == 1' "$inspect_file" >/dev/null

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

csrf="$(get_csrf)"
login_output="$temp_root/login.json"
status="$(request_json POST /api/v1/auth/login \
  '{"email":"production-admin@example.test","password":"temporary-password","rememberMe":false}' \
  "$csrf" "$login_output")"
[[ "$status" == 200 ]] || { cat "$login_output" >&2; exit 1; }

csrf="$(get_csrf)"
status="$(request_json POST /api/v1/auth/change-password \
  '{"currentPassword":"temporary-password","newPassword":"administrator-password"}' \
  "$csrf" "$temp_root/password.json")"
[[ "$status" == 204 ]] || { cat "$temp_root/password.json" >&2; exit 1; }

csrf="$(get_csrf)"
status="$(request_json POST /api/v1/games \
  '{"name":"production-worker-smoke","visibility":"PRIVATE"}' \
  "$csrf" "$temp_root/game.json")"
[[ "$status" == 201 ]] || { cat "$temp_root/game.json" >&2; exit 1; }
game_id="$(jq -er '.id' "$temp_root/game.json")"
game_version="$(jq -er '.stateVersion' "$temp_root/game.json")"

fixture_dir="$temp_root/fixture"
mkdir -p "$fixture_dir/CSV" "$fixture_dir/ERB"
printf 'title,production-smoke\n' > "$fixture_dir/CSV/GAMEBASE.CSV"
printf '@SYSTEM_TITLE\nINPUT\nQUIT\n' > "$fixture_dir/ERB/START.ERB"
printf 'Use sav folder:NO\n' > "$fixture_dir/emuera.config"
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
  -H 'Content-Type: application/zip' -H "X-CSRF-TOKEN: $csrf" -H 'Idempotency-Key: production-ingest' \
  --data-binary "@$archive")"
[[ "$status" == 201 ]] || { cat "$ingest_output" >&2; exit 1; }
ingestion_id="$(jq -er '.ingestionId' "$ingest_output")"
content_digest="$(jq -er '.manifest.contentDigest' "$ingest_output")"

csrf="$(get_csrf)"
status="$(request_json PUT "/api/v1/games/$game_id/package" \
  "{\"ingestionId\":\"$ingestion_id\",\"contentDigest\":\"$content_digest\"}" \
  "$csrf" "$temp_root/bind.json" "$game_version" production-bind)"
[[ "$status" == 200 ]] || { cat "$temp_root/bind.json" >&2; exit 1; }
draft_version="$(jq -er '.stateVersion' "$temp_root/bind.json")"

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/games/$game_id:validate" '{}' "$csrf" "$temp_root/validate.json" "$draft_version" production-validate)"
[[ "$status" == 200 ]] || { cat "$temp_root/validate.json" >&2; exit 1; }
[[ "$(jq -er '.canActivate' "$temp_root/validate.json")" == true ]] || { cat "$temp_root/validate.json" >&2; exit 1; }
validation_version="$(jq -er '.stateVersion' "$temp_root/validate.json")"

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/games/$game_id:activate" '{}' "$csrf" "$temp_root/activate.json" "$validation_version" production-activate)"
[[ "$status" == 200 ]] || { cat "$temp_root/activate.json" >&2; exit 1; }

csrf="$(get_csrf)"
status="$(request_json POST /api/v1/sessions \
  "{\"gameId\":\"$game_id\",\"name\":\"production-worker-session\"}" \
  "$csrf" "$temp_root/session.json" '' production-session-create)"
[[ "$status" == 201 ]] || { cat "$temp_root/session.json" >&2; exit 1; }
session_id="$(jq -er '.id' "$temp_root/session.json")"

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/sessions/$session_id:open" \
  '{"browserWidth":1024}' \
  "$csrf" "$temp_root/open.json" '' production-session-open)"
[[ "$status" == 200 || "$status" == 202 ]] || { cat "$temp_root/open.json" >&2; exit 1; }
for attempt in $(seq 1 40); do
  session_state="$(curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/sessions/$session_id" | jq -r '.state')"
  [[ "$session_state" == RUNNING ]] && break
  sleep 0.25
done
[[ "$session_state" == RUNNING ]] || { echo "production Session did not reach RUNNING" >&2; exit 1; }

runtime_output="$temp_root/admin-runtime.json"
curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/admin/workers" > "$runtime_output"
worker_pid="$(jq -er '.workers[0].worker.pid' "$runtime_output")"
worker_epoch="$(jq -er '.workers[0].worker.workerEpoch' "$runtime_output")"

worker_probe="$temp_root/worker-probe.txt"
"${compose[@]}" exec --no-TTY api sh -c '
  pid="$1"
  test -r "/proc/$pid/status" -a -r "/proc/$pid/cmdline" -a -r "/proc/$pid/environ"
  uid="$(awk "/^Uid:/ {print \$2}" "/proc/$pid/status")"
  cmdline="$(tr "\\0" " " < "/proc/$pid/cmdline")"
  printf "uid=%s\\ncmdline=%s\\n" "$uid" "$cmdline"
  tr "\\0" "\\n" < "/proc/$pid/environ"
' sh "$worker_pid" > "$worker_probe"
worker_uid="$(sed -n 's/^uid=//p' "$worker_probe")"
grep -q '^cmdline=.*\/app\/worker\/CloudEmuera\.Worker\.dll.*--bootstrap-file' "$worker_probe"
[[ "$worker_uid" == "$api_uid" && -n "$worker_uid" ]] || { echo "Worker did not inherit the API identity: $worker_uid vs $api_uid" >&2; exit 1; }
if grep -Eiq 'CLOUDEMUERA_BOOTSTRAP_ADMIN_|CloudEmuera__DataPath|CloudEmuera__DatabasePath|cloudemuera\.db|/workspace' "$worker_probe"; then
  echo "Worker inherited a control-plane secret/path" >&2
  exit 1
fi

# Exercise the formal browser protocol against the real API-owned Worker. The
# Python client is stdlib-only and exists only in this temporary test process;
# it does not add a runtime dependency to the production image.
python3 - "$base_url" "$cookie_jar" "$session_id" "$worker_epoch" <<'PY'
import base64
import hashlib
import json
import os
import pathlib
import secrets
import socket
import sys
import time

from urllib.parse import urlsplit

base_url, cookie_path, session_id, worker_epoch = sys.argv[1:]
parts = urlsplit(base_url)
port = parts.port or 80

def cookie_header(path):
    values = []
    for line in pathlib.Path(path).read_text().splitlines():
        if not line or (line.startswith('#') and not line.startswith('#HttpOnly_')):
            continue
        columns = line.split('\t')
        if len(columns) >= 7:
            values.append(f"{columns[5]}={columns[6]}")
    return '; '.join(values)

class WebSocket:
    def __init__(self):
        self.sock = socket.create_connection((parts.hostname, port), timeout=15)
        self.buffer = bytearray()
        key = base64.b64encode(os.urandom(16)).decode()
        request = (
            f"GET /api/v1/realtime HTTP/1.1\r\nHost: {parts.hostname}:{port}\r\n"
            "Upgrade: websocket\r\nConnection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n"
            "Sec-WebSocket-Protocol: cloudemuera.realtime.v6\r\n"
            "Origin: http://localhost:5173\r\n"
            f"Cookie: {cookie_header(cookie_path)}\r\n\r\n"
        ).encode()
        self.sock.sendall(request)
        header = self.read_until(b"\r\n\r\n").decode("latin1")
        if " 101 " not in header or "Sec-WebSocket-Protocol: cloudemuera.realtime.v6" not in header:
            raise RuntimeError(f"websocket handshake failed: {header}")

    def read_until(self, marker):
        while marker not in self.buffer:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise RuntimeError("websocket closed during handshake")
            self.buffer.extend(chunk)
        index = self.buffer.index(marker) + len(marker)
        result = bytes(self.buffer[:index])
        del self.buffer[:index]
        return result

    def recv_exact(self, count):
        while len(self.buffer) < count:
            chunk = self.sock.recv(max(4096, count - len(self.buffer)))
            if not chunk:
                raise RuntimeError("websocket closed")
            self.buffer.extend(chunk)
        result = bytes(self.buffer[:count])
        del self.buffer[:count]
        return result

    def send(self, value):
        payload = json.dumps(value, separators=(",", ":")).encode()
        mask = secrets.token_bytes(4)
        length = len(payload)
        if length < 126:
            header = bytes([0x81, 0x80 | length])
        elif length < 65536:
            header = bytes([0x81, 0x80 | 126]) + length.to_bytes(2, "big")
        else:
            header = bytes([0x81, 0x80 | 127]) + length.to_bytes(8, "big")
        self.sock.sendall(header + mask + bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload)))

    def recv(self):
        first, second = self.recv_exact(2)
        opcode = first & 0x0F
        masked = second & 0x80
        length = second & 0x7F
        if length == 126:
            length = int.from_bytes(self.recv_exact(2), "big")
        elif length == 127:
            length = int.from_bytes(self.recv_exact(8), "big")
        mask = self.recv_exact(4) if masked else None
        payload = bytearray(self.recv_exact(length))
        if mask:
            for index in range(length):
                payload[index] ^= mask[index % 4]
        if opcode == 9:
            self.send_raw(0xA, bytes(payload))
            return self.recv()
        if opcode == 8:
            raise RuntimeError("websocket closed by server")
        if opcode != 1:
            return self.recv()
        return json.loads(payload.decode())

    def send_raw(self, opcode, payload):
        mask = secrets.token_bytes(4)
        length = len(payload)
        if length < 126:
            header = bytes([0x80 | opcode, 0x80 | length])
        elif length < 65536:
            header = bytes([0x80 | opcode, 0x80 | 126]) + length.to_bytes(2, "big")
        else:
            header = bytes([0x80 | opcode, 0x80 | 127]) + length.to_bytes(8, "big")
        self.sock.sendall(header + mask + bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload)))

    def close(self):
        try:
            self.send_raw(0x8, (1000).to_bytes(2, "big") + b"production-smoke")
        finally:
            self.sock.close()

digest = hashlib.sha256(b"cloudemuera:p1-s10:2175f8a629257efb08214e093704b3a3d3d06d05:structured-console-v8-button-generation").hexdigest()
socket_client = WebSocket()
try:
    socket_client.send({"protocolVersion": 6, "type": "client.hello", "messageId": "production-hello", "payload": {
        "supportedProtocolVersions": [5], "capabilityDigest": digest, "supportedCapabilities": []}})
    hello = socket_client.recv()
    if hello.get("type") != "server.hello":
        raise RuntimeError(f"unexpected server hello: {hello}")
    prompt = None
    accepted = False
    for attempt in range(40):
        socket_client.send({"protocolVersion": 6, "type": "session.resume", "messageId": f"production-resume-{attempt}",
            "sessionId": session_id, "payload": {"capabilityDigest": digest}})
        for _ in range(12):
            message = socket_client.recv()
            if message.get("type") == "session.snapshot":
                prompt = message.get("payload", {}).get("consoleState", {}).get("currentPrompt")
            if message.get("type") == "session.resume.result" and message.get("payload", {}).get("status") == "ACCEPTED":
                accepted = True
            if prompt:
                break
        if accepted and prompt:
            break
        time.sleep(0.1)
    if not accepted or not prompt:
        raise RuntimeError("realtime resume did not expose a current prompt")
    socket_client.send({"protocolVersion": 6, "type": "session.input", "messageId": "production-input-envelope",
        "sessionId": session_id, "workerEpoch": int(worker_epoch), "payload": {
            "clientMessageId": "production-input", "source": "KEYBOARD", "value": "7",
            "key": {"keyCode": 55, "control": False, "alt": False, "shift": False}}})
    for _ in range(40):
        message = socket_client.recv()
        if message.get("type") == "session.input.result":
            status = message.get("payload", {}).get("status")
            if status != "ACCEPTED":
                raise RuntimeError(f"worker rejected production input: {message}")
            break
    else:
        raise RuntimeError("production input result was not received")
finally:
    socket_client.close()
PY

csrf="$(get_csrf)"
status="$(request_json POST "/api/v1/sessions/$session_id:close" '{}' "$csrf" "$temp_root/close.json" '' production-session-close)"
[[ "$status" == 200 || "$status" == 202 ]] || { cat "$temp_root/close.json" >&2; exit 1; }
for attempt in $(seq 1 40); do
  session_state="$(curl --fail --silent --show-error --cookie "$cookie_jar" "$base_url/api/v1/sessions/$session_id" | jq -r '.state')"
  [[ "$session_state" == CLOSED ]] && break
  sleep 0.25
done
[[ "$session_state" == CLOSED ]] || { echo "production Session did not close" >&2; exit 1; }

foreign_owner="$(find "$data_root" -xdev \( ! -uid "$CLOUDEMUERA_UID" -o ! -gid "$CLOUDEMUERA_GID" \) -print -quit)"
[[ -z "$foreign_owner" ]] || { echo "production data contains a file not owned by deployer $CLOUDEMUERA_UID:$CLOUDEMUERA_GID: $foreign_owner" >&2; exit 1; }

echo "production single-container image and API-owned Worker smoke passed (uid=$api_uid:$api_gid, workerUid=$worker_uid)"

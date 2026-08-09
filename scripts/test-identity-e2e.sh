#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/lib/dev-env.sh"

skip_build=false
if [[ "${1:-}" == "--no-build" ]]; then
  skip_build=true
elif [[ $# -ne 0 ]]; then
  echo 'usage: test-identity-e2e.sh [--no-build]' >&2
  exit 64
fi

temp_root="$(mktemp -d)"
mkdir "$temp_root/data"
project_name="cloudemuera-identity-e2e-${RANDOM}-${RANDOM}"
cleanup() {
  docker compose --profile e2e --env-file "$temp_root/identity.env" -p "$project_name" -f "$repo_root/compose.dev.yaml" down --remove-orphans --volumes >/dev/null 2>&1 || true
  rm -rf "$temp_root"
}
trap cleanup EXIT

write_identity_env() {
cat > "$temp_root/identity.env" <<EOF
CLOUDEMUERA_UID=$CLOUDEMUERA_UID
CLOUDEMUERA_GID=$CLOUDEMUERA_GID
CLOUDEMUERA_DATA_PATH=$temp_root/data
CLOUDEMUERA_HTTP_PORT=0
CLOUDEMUERA_WEB_PORT=0
CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=$1
CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=$2
CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=$3
EOF
}
write_identity_env identity-admin identity-admin@example.test temporary-password

compose=(docker compose --profile e2e --env-file "$temp_root/identity.env" -p "$project_name" -f "$repo_root/compose.dev.yaml")
if ! "$skip_build"; then
  "${compose[@]}" run --rm api dotnet restore CloudEmuera.slnx --locked-mode
  "${compose[@]}" run --rm api dotnet run --project src/CloudEmuera.Migrator -- migrate --data-root /data
  "${compose[@]}" build e2e
else
  "${compose[@]}" run --rm api dotnet /workspace/src/CloudEmuera.Migrator/bin/Release/net10.0/CloudEmuera.Migrator.dll migrate --data-root /data
fi
"${compose[@]}" up -d api web

for _ in $(seq 1 60); do
  if "${compose[@]}" exec -T web node -e 'Promise.all([fetch("http://localhost:5173/login"), fetch("http://api:28647/health/ready")]).then(responses => process.exit(responses.every(response => response.ok) ? 0 : 1)).catch(() => process.exit(1))'; then
    break
  fi
  sleep 1
done
"${compose[@]}" exec -T web node -e 'Promise.all([fetch("http://localhost:5173/login"), fetch("http://api:28647/health/ready")]).then(responses => process.exit(responses.every(response => response.ok) ? 0 : 1)).catch(() => process.exit(1))'
"${compose[@]}" run --rm -e CLOUDEMUERA_E2E_URL=http://web:5173 e2e \
  sh -c 'pnpm install --frozen-lockfile && pnpm --dir e2e exec playwright test --project=chromium'

# A completed instance must ignore removed or changed bootstrap configuration.
# Recreate only the API with canary values; the mobile journey then proves the
# persisted administrator and password remain unchanged across the restart.
write_identity_env replacement-admin replacement-admin@example.test replacement-password
"${compose[@]}" up -d --force-recreate api
for _ in $(seq 1 60); do
  if "${compose[@]}" exec -T web node -e 'fetch("http://api:28647/health/ready").then(response => process.exit(response.ok ? 0 : 1)).catch(() => process.exit(1))'; then
    break
  fi
  sleep 1
done
"${compose[@]}" exec -T web node -e 'fetch("http://api:28647/health/ready").then(response => process.exit(response.ok ? 0 : 1)).catch(() => process.exit(1))'
"${compose[@]}" run --rm -e CLOUDEMUERA_E2E_URL=http://web:5173 e2e \
  sh -c 'pnpm install --frozen-lockfile && pnpm --dir e2e exec playwright test --project=mobile-chrome'
echo "P1-02 identity desktop and mobile Chromium E2E passed"

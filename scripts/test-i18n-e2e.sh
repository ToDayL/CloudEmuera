#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/lib/dev-env.sh"

skip_build=false
if [[ "${1:-}" == "--no-build" ]]; then
  skip_build=true
elif [[ $# -ne 0 ]]; then
  echo 'usage: test-i18n-e2e.sh [--no-build]' >&2
  exit 64
fi

temp_parent="$repo_root/.tmp"
mkdir -p "$temp_parent"
temp_root="$(mktemp -d "$temp_parent/i18n-e2e.XXXXXX")"
project_name="cloudemuera-i18n-e2e-${RANDOM}-${RANDOM}"
env_file="$temp_root/i18n.env"
cleanup() {
  docker compose --profile e2e --env-file "$env_file" -p "$project_name" -f "$repo_root/docker/compose.dev.yml" down --remove-orphans --volumes >/dev/null 2>&1 || true
  rm -rf "$temp_root"
}
trap cleanup EXIT

printf '%s\n' \
  "CLOUDEMUERA_UID=$CLOUDEMUERA_UID" \
  "CLOUDEMUERA_GID=$CLOUDEMUERA_GID" \
  'CLOUDEMUERA_DEV_HTTP_PORT=0' \
  'CLOUDEMUERA_WEB_PORT=0' \
  'CLOUDEMUERA_VITE_HMR=true' \
  'CLOUDEMUERA_PUBLIC_ORIGIN=http://web:5173' \
  'CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=i18n-admin' \
  'CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=i18n-admin@example.test' \
  'CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password' > "$env_file"

compose=(docker compose --profile e2e --env-file "$env_file" -p "$project_name" -f "$repo_root/docker/compose.dev.yml")
"${compose[@]}" run --rm api dotnet restore CloudEmuera.slnx --locked-mode
if ! "$skip_build"; then
  "${compose[@]}" run --rm api dotnet run --project src/CloudEmuera.Migrator -- migrate --data-root /data
  "${compose[@]}" build e2e
else
  "${compose[@]}" run --rm api dotnet /workspace/src/CloudEmuera.Migrator/bin/Release/net10.0/CloudEmuera.Migrator.dll migrate --data-root /data
fi
"${compose[@]}" up -d api web

for _ in $(seq 1 60); do
  if "${compose[@]}" exec -T web node -e 'Promise.all([fetch("http://localhost:5173/login"), fetch("http://api:28647/health/ready")]).then(responses => process.exit(responses.every(response => response.ok) ? 0 : 1)).catch(() => process.exit(1))'; then break; fi
  sleep 1
done
if ! "${compose[@]}" exec -T web node -e 'Promise.all([fetch("http://localhost:5173/login"), fetch("http://api:28647/health/ready")]).then(responses => process.exit(responses.every(response => response.ok) ? 0 : 1)).catch(() => process.exit(1))'; then
  "${compose[@]}" ps >&2
  "${compose[@]}" logs --tail=120 api web >&2
  exit 1
fi
"${compose[@]}" run --rm -e CLOUDEMUERA_E2E_URL=http://web:5173 e2e sh -c \
  'pnpm install --frozen-lockfile && pnpm --dir e2e exec playwright test tests/i18n.spec.ts --project=chromium'
echo "P1-S12 i18n Chromium E2E passed"

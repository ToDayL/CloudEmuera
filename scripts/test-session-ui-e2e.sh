#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/lib/dev-env.sh"

skip_build=false
if [[ "${1:-}" == "--no-build" ]]; then
  skip_build=true
elif [[ $# -ne 0 ]]; then
  echo 'usage: test-session-ui-e2e.sh [--no-build]' >&2
  exit 64
fi

temp_parent="$repo_root/.tmp"
mkdir -p "$temp_parent"
temp_root="$(mktemp -d "$temp_parent/session-ui-e2e.XXXXXX")"
project_name="cloudemuera-session-ui-e2e-${RANDOM}-${RANDOM}"
cleanup() {
  docker compose --profile e2e --env-file "$temp_root/session-ui.env" -p "$project_name" -f "$repo_root/docker/compose.dev.yml" down --remove-orphans --volumes >/dev/null 2>&1 || true
  chmod -R u+w "$temp_root" >/dev/null 2>&1 || true
  rm -rf "$temp_root"
}
trap cleanup EXIT

cat > "$temp_root/session-ui.env" <<EOF
CLOUDEMUERA_UID=$CLOUDEMUERA_UID
CLOUDEMUERA_GID=$CLOUDEMUERA_GID
CLOUDEMUERA_DEV_HTTP_PORT=0
CLOUDEMUERA_WEB_PORT=0
CLOUDEMUERA_PUBLIC_ORIGIN=http://web:5173
CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=session-ui-admin
CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=session-ui-admin@example.test
CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=session-ui-temporary-password
EOF

compose=(docker compose --profile e2e --env-file "$temp_root/session-ui.env" -p "$project_name" -f "$repo_root/docker/compose.dev.yml")
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
"${compose[@]}" run --rm -e CLOUDEMUERA_E2E_URL=http://web:5173 -e CLOUDEMUERA_E2E_PROJECTS="${CLOUDEMUERA_E2E_PROJECTS:-}" -e CLOUDEMUERA_E2E_GREP="${CLOUDEMUERA_E2E_GREP:-}" e2e \
  sh -c 'pnpm install --frozen-lockfile && projects="${CLOUDEMUERA_E2E_PROJECTS:-chromium mobile-chrome mobile-safari}" && project_args="" && for project in $projects; do project_args="$project_args --project=$project"; done && if [ -n "${CLOUDEMUERA_E2E_GREP:-}" ]; then pnpm --dir e2e exec playwright test e2e/tests/session-ui.spec.ts $project_args --workers=1 --grep "$CLOUDEMUERA_E2E_GREP"; else pnpm --dir e2e exec playwright test e2e/tests/session-ui.spec.ts $project_args --workers=1; fi'
echo "P1-11 Session/Console/Save, timed prompt, rich renderer, concurrency, authorization, and mobile network E2E passed"

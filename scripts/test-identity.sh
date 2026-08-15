#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/lib/dev-env.sh"
temp_parent="$repo_root/.tmp"
mkdir -p "$temp_parent"
temp_root="$(mktemp -d "$temp_parent/identity.XXXXXX")"
mkdir "$temp_root/data"
project_name="cloudemuera-identity-${RANDOM}-${RANDOM}"
cleanup() { docker compose --env-file "$temp_root/identity.env" -p "$project_name" -f "$repo_root/compose.dev.yaml" down --remove-orphans --volumes >/dev/null 2>&1 || true; rm -rf "$temp_root"; }
trap cleanup EXIT
cat > "$temp_root/identity.env" <<EOF
CLOUDEMUERA_UID=$CLOUDEMUERA_UID
CLOUDEMUERA_GID=$CLOUDEMUERA_GID
CLOUDEMUERA_DATA_PATH=$temp_root/data
CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=identity-admin
CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=identity-admin@example.test
CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password
EOF
suite="${2:-all}"
case "${1:---suite}" in --suite) ;; *) echo 'usage: test-identity.sh --suite application|infrastructure|api|all' >&2; exit 64;; esac
case "$suite" in
  application) project='tests/CloudEmuera.Application.Tests';;
  infrastructure) project='tests/CloudEmuera.Infrastructure.Tests';;
  api) project='tests/CloudEmuera.Api.IntegrationTests';;
  all) project='tests/CloudEmuera.Application.Tests tests/CloudEmuera.Infrastructure.Tests tests/CloudEmuera.Api.IntegrationTests';;
  *) echo 'invalid identity suite' >&2; exit 64;;
esac
docker compose --env-file "$temp_root/identity.env" -p "$project_name" -f "$repo_root/compose.dev.yaml" run --rm api \
  dotnet restore CloudEmuera.slnx --locked-mode
for test_project in $project; do docker compose --env-file "$temp_root/identity.env" -p "$project_name" -f "$repo_root/compose.dev.yaml" run --rm api dotnet test "$test_project" --no-restore --configuration Release; done

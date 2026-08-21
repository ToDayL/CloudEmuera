#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

source "$repo_root/scripts/lib/dev-env.sh"

compose=(docker compose)
if [[ -f "$repo_root/docker/.env" ]]; then
  compose+=(--env-file "$repo_root/docker/.env")
fi
compose+=(--file "$repo_root/docker/compose.dev.yml")

"${compose[@]}" run --rm api dotnet restore CloudEmuera.slnx --locked-mode
"${compose[@]}" run --rm api bash -lc './scripts/verify-runtime-fixtures.sh'
"${compose[@]}" run --rm api dotnet build CloudEmuera.slnx --no-restore --configuration Release
./scripts/test-identity.sh --suite application
./scripts/test-identity.sh --suite api
"${compose[@]}" run --rm api dotnet test CloudEmuera.slnx --no-build --configuration Release
"${compose[@]}" run --rm web sh -c \
  "pnpm install --frozen-lockfile && CLOUDEMUERA_OPENAPI_URL=http://api:28647/openapi/v1.json pnpm verify:contracts && pnpm typecheck:web && pnpm test:web && pnpm build:web"

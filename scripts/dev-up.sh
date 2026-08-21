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

"${compose[@]}" build api web
# The running API and Worker are launched as already-built DLLs. Build both
# Build development assemblies explicitly so the API has no dotnet CLI host
# above it and cannot silently fall back to stale dependencies.
"${compose[@]}" run --rm api \
  dotnet build src/CloudEmuera.Api/CloudEmuera.Api.csproj --configuration Debug
"${compose[@]}" run --rm api \
  dotnet build src/CloudEmuera.Worker/CloudEmuera.Worker.csproj --configuration Debug
# Schema changes are owned exclusively by Migrator. Stop a possibly older API
# before upgrading the persistent development database, then start services
# only after migration and its pre-change backup succeed.
"${compose[@]}" stop api
"${compose[@]}" run --rm api \
  dotnet run --project src/CloudEmuera.Migrator -- migrate --data-root /data
"${compose[@]}" up --detach api web
"${compose[@]}" ps

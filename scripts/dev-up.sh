#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

source "$repo_root/scripts/lib/dev-env.sh"

docker compose -f compose.dev.yaml build api web
# The running API and Worker are launched as already-built DLLs. Build both
# Build development assemblies explicitly so the API has no dotnet CLI host
# above it and cannot silently fall back to stale dependencies.
docker compose -f compose.dev.yaml run --rm api \
  dotnet build src/CloudEmuera.Api/CloudEmuera.Api.csproj --configuration Debug
docker compose -f compose.dev.yaml run --rm api \
  dotnet build src/CloudEmuera.Worker/CloudEmuera.Worker.csproj --configuration Debug
# Schema changes are owned exclusively by Migrator. Stop a possibly older API
# before upgrading the persistent development database, then start services
# only after migration and its pre-change backup succeed.
docker compose -f compose.dev.yaml stop api
docker compose -f compose.dev.yaml run --rm api \
  dotnet run --project src/CloudEmuera.Migrator -- migrate --data-root /data
docker compose -f compose.dev.yaml up --detach api web
docker compose -f compose.dev.yaml ps

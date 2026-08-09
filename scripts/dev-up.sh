#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

source "$repo_root/scripts/lib/dev-env.sh"

docker compose -f compose.dev.yaml build api web
# Schema changes are owned exclusively by Migrator. Stop a possibly older API
# before upgrading the persistent development database, then start services
# only after migration and its pre-change backup succeed.
docker compose -f compose.dev.yaml stop api
docker compose -f compose.dev.yaml run --rm api \
  dotnet run --project src/CloudEmuera.Migrator -- migrate --data-root /data
docker compose -f compose.dev.yaml up --detach api web
docker compose -f compose.dev.yaml ps

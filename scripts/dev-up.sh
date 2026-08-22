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
# Stop the old API before replacing bind-mounted DLLs. This preserves the
# direct API-to-Worker parent relationship across development rebuilds.
"${compose[@]}" stop api
# The running API and its Worker are launched as already-built DLLs. Restore
# once and build every runtime process before replacing the running API so no
# dotnet CLI host remains above the API/Worker parent-child tree.
"${compose[@]}" run --rm api sh -euc '
  dotnet restore CloudEmuera.slnx --locked-mode
  dotnet build src/CloudEmuera.Api/CloudEmuera.Api.csproj --no-restore --configuration Debug
  dotnet build src/CloudEmuera.Worker/CloudEmuera.Worker.csproj --no-restore --configuration Debug
  dotnet build src/CloudEmuera.Validator/CloudEmuera.Validator.csproj --no-restore --configuration Debug
  dotnet build src/CloudEmuera.Migrator/CloudEmuera.Migrator.csproj --no-restore --configuration Debug
'
# The API container entrypoint owns the migration-before-API ordering. This
# keeps development aligned with the production single-container topology.
"${compose[@]}" run --rm web \
  sh -euc 'pnpm install --frozen-lockfile && pnpm --dir src/CloudEmuera.Web build'
"${compose[@]}" up --detach api web
running_services="$("${compose[@]}" ps --services --filter status=running | sed '/^$/d')"
running_service_count="$(printf '%s\n' "$running_services" | wc -l | tr -d ' ')"
if [[ "$running_service_count" != "2" ]] || ! grep -Fxq api <<<"$running_services" || ! grep -Fxq web <<<"$running_services"; then
  echo "default development topology must have api and web running; got: $running_services" >&2
  exit 1
fi
"${compose[@]}" ps

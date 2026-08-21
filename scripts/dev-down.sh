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

"${compose[@]}" down

#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source "$repo_root/scripts/lib/dev-env.sh"

docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests \
  --no-restore --configuration Release --filter 'Category=MigrationProcess'

#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

source "$repo_root/scripts/lib/dev-env.sh"

docker compose -f compose.dev.yaml run --rm api dotnet restore CloudEmuera.slnx --locked-mode
docker compose -f compose.dev.yaml run --rm api dotnet build CloudEmuera.slnx --no-restore --configuration Release
docker compose -f compose.dev.yaml run --rm api dotnet test CloudEmuera.slnx --no-build --configuration Release
docker compose -f compose.dev.yaml run --rm web sh -c \
  "pnpm install --frozen-lockfile && pnpm typecheck:web && pnpm test:web && pnpm build:web"

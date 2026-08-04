#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/tests/CloudEmuera.RuntimeAdapter.Tests/CloudEmuera.RuntimeAdapter.Tests.csproj"

if [[ ! -f "$project" ]]; then
  echo "runtime fixture validator project is missing: $project" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required; install the SDK or use the development container" >&2
  exit 1
fi

assets="$repo_root/tests/CloudEmuera.RuntimeAdapter.Tests/obj/project.assets.json"
if [[ ! -f "$assets" ]]; then
  dotnet restore "$repo_root/CloudEmuera.slnx" --locked-mode
fi

exec dotnet run --project "$project" --no-restore -- "$@"

#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/tests/CloudEmuera.RuntimeCompatibility.Tests/CloudEmuera.RuntimeCompatibility.Tests.csproj"

# The compatibility entry point is intentionally usable from the host. The
# actual .NET commands still run in the UID/GID-mapped dev container; when the
# script is already running there, continue below without recursing.
if [[ "${CLOUDEMUERA_DEV_CONTAINER:-}" != "1" && ! -e /.dockerenv ]]; then
  source "$repo_root/scripts/lib/dev-env.sh"
  exec docker compose -f "$repo_root/docker/compose.dev.yml" run --rm --no-deps api \
    bash /workspace/scripts/test-runtime-compat.sh "$@"
fi

scenario=""
fixture_args=()
restore_args=(--locked-mode)

while (($#)); do
  case "$1" in
    --scenario)
      [[ $# -ge 2 ]] || { echo "--scenario requires a value" >&2; exit 2; }
      scenario="$2"
      shift 2
      ;;
    --fixture)
      [[ $# -ge 2 ]] || { echo "--fixture requires a value" >&2; exit 2; }
      fixture_args=(--fixture "$2")
      shift 2
      ;;
    --no-restore)
      restore_args=()
      shift
      ;;
    *)
      echo "unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ "$scenario" != "input-roundtrip" && "$scenario" != "save-root" && "$scenario" != "save-directory" ]]; then
  echo "unsupported scenario; supported values: input-roundtrip, save-root, save-directory" >&2
  exit 2
fi

unset DISPLAY WAYLAND_DISPLAY
"$repo_root/scripts/verify-runtime-fixtures.sh"
"$repo_root/scripts/verify-third-party.sh"

if ((${#restore_args[@]})); then
  dotnet restore "$project" "${restore_args[@]}"
fi

dotnet build "$project" --no-restore --configuration Release
dotnet run --project "$project" --no-build --configuration Release -- \
  --scenario "$scenario" "${fixture_args[@]}"

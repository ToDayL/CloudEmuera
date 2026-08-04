#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

source "$repo_root/scripts/lib/dev-env.sh"

for service in api web; do
  docker compose -f compose.dev.yaml run --rm --no-deps --build "$service" sh -euc '
    actual_identity="$(id -u):$(id -g)"
    expected_identity="${CLOUDEMUERA_UID}:${CLOUDEMUERA_GID}"
    test "$actual_identity" = "$expected_identity"

    probe="/workspace/.cloudemuera-ownership-probe-$$"
    trap '\''rm -f "$probe"'\'' EXIT
    touch "$probe"
    test "$(stat -c "%u:%g" "$probe")" = "$expected_identity"
  '
  printf '%s runs and writes as %s:%s\n' "$service" "$CLOUDEMUERA_UID" "$CLOUDEMUERA_GID"
done

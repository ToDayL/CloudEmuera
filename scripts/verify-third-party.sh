#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected_commit="2175f8a629257efb08214e093704b3a3d3d06d05"
actual_commit="$(git -C "$repo_root/third_party/emuera-em" rev-parse HEAD)"

if [[ "$actual_commit" != "$expected_commit" ]]; then
  echo "Emuera.EM+EE commit mismatch: expected $expected_commit, got $actual_commit" >&2
  exit 1
fi

git -C "$repo_root/third_party/emuera-em" diff --quiet
git -C "$repo_root/third_party/emuera-em" diff --cached --quiet
echo "Emuera.EM+EE source is pinned at $expected_commit"


#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected_commit="2175f8a629257efb08214e093704b3a3d3d06d05"
expected_tree="a3c96867e3a5b5d5f90877a4e7c6f8056d5f5b9b"
expected_license_sha256="8770a79e679a354cffc4005cee99403d609c31ce88dd2b79e4a50325317beb77"
expected_integration_version="source-v1"
runtime_root="$repo_root/src/CloudEmuera.EmueraRuntime"
source_root="$runtime_root/Upstream"
license_path="$source_root/Readme/License/Emuera.LICENSE.txt"

if [[ -e "$repo_root/.gitmodules" ]]; then
  echo "Git submodules are not allowed for the integrated Emuera source" >&2
  exit 1
fi

while IFS= read -r gitlink_path; do
  if [[ -n "$gitlink_path" && -e "$repo_root/$gitlink_path" ]]; then
    echo "Active Gitlink is not allowed: $gitlink_path" >&2
    exit 1
  fi
done < <(git -C "$repo_root" ls-files -s | awk '$1 == "160000" { print $4 }')

for required_path in \
  "$runtime_root/UPSTREAM.md" \
  "$runtime_root/MODIFICATIONS.md" \
  "$source_root/Emuera/Program.cs" \
  "$source_root/Emuera/Runtime/Script/Process.cs" \
  "$license_path"; do
  if [[ ! -f "$required_path" ]]; then
    echo "Missing integrated Emuera source/provenance file: ${required_path#"$repo_root/"}" >&2
    exit 1
  fi
done

if find "$source_root" -name .git -print -quit | grep -q .; then
  echo "Nested Git metadata is not allowed in the integrated Emuera source" >&2
  exit 1
fi

actual_license_sha256="$(sha256sum "$license_path" | awk '{print $1}')"
if [[ "$actual_license_sha256" != "$expected_license_sha256" ]]; then
  echo "Emuera license mismatch: expected $expected_license_sha256, got $actual_license_sha256" >&2
  exit 1
fi

grep -Fq "$expected_commit" "$runtime_root/UPSTREAM.md"
grep -Fq "$expected_tree" "$runtime_root/UPSTREAM.md"
grep -Fq "$expected_license_sha256" "$runtime_root/UPSTREAM.md"
grep -Fq "UpstreamCommit = \"$expected_commit\"" "$repo_root/src/CloudEmuera.RuntimeAdapter/RuntimeBaseline.cs"
grep -Fq "CloudEmueraIntegrationVersion = \"$expected_integration_version\"" "$repo_root/src/CloudEmuera.RuntimeAdapter/RuntimeBaseline.cs"
grep -Fq "\"upstreamCommit\": \"$expected_commit\"" "$repo_root/tests/fixtures/runtime/manifest.json"
grep -Fq "\"cloudEmueraIntegrationVersion\": \"$expected_integration_version\"" "$repo_root/tests/fixtures/runtime/manifest.json"

echo "Integrated Emuera.EM+EE source provenance verified at $expected_commit ($expected_integration_version)"

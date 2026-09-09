#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Translation resources, tests, generated contracts, and protocol/reducer
# diagnostics are intentionally excluded. Protocol diagnostics are converted
# to stable localized reason-code messages before reaching the UI.
violations="$({
  rg -n --glob '*.{ts,tsx}' '[\p{Han}]' src/CloudEmuera.Web/src \
    --glob '!**/*.test.*' \
    --glob '!**/i18n/**' \
    --glob '!**/api/generated/**' \
    --glob '!**/realtime/codec.ts' \
    --glob '!**/realtime/reducer.ts' \
    --glob '!**/realtime/sessionStore.ts' || true
} | rg -v 'localeNames:|ABC 123.*中文.*日本語' || true)"

if [[ -n "$violations" ]]; then
  echo "Untranslated CloudEmuera Web UI literals found:" >&2
  echo "$violations" >&2
  exit 1
fi

echo "CloudEmuera Web UI literal check passed"

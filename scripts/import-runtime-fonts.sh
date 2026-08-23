#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
destination="$repo_root/assets/runtime-fonts"
source_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --source-dir) [[ $# -ge 2 ]] || { echo "usage: $0 --source-dir DIR" >&2; exit 2; }; source_dir="$2"; shift 2 ;;
    *) echo "usage: $0 --source-dir DIR" >&2; exit 2 ;;
  esac
done
[[ -n "$source_dir" && -d "$source_dir" ]] || { echo "--source-dir must point to a local, reviewed release artifact directory" >&2; exit 2; }
command -v woff2_compress >/dev/null 2>&1 || {
  echo "woff2_compress is required from the locked build toolchain; this script never downloads it." >&2
  exit 1
}

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/runtime-ttf" "$stage/web-woff2" "$stage/licenses"

find_source() {
  local pattern="$1" result
  result="$(find "$source_dir" -type f -name "$pattern" -print | sort | head -n 1)"
  [[ -n "$result" && ! -L "$result" ]] || { echo "missing or linked source artifact: $pattern" >&2; exit 1; }
  echo "$result"
}

declare -a ids=(
  sarasa-fixed-sc-1.0.40-light sarasa-fixed-sc-1.0.40-regular sarasa-fixed-sc-1.0.40-medium
  lxgw-wenkai-mono-1.522-light lxgw-wenkai-mono-1.522-regular lxgw-wenkai-mono-1.522-medium
)
declare -a names=(
  SarasaFixedSC-Light.ttf SarasaFixedSC-Regular.ttf SarasaFixedSC-SemiBold.ttf
  LXGWWenKaiMono-Light.ttf LXGWWenKaiMono-Regular.ttf LXGWWenKaiMono-Medium.ttf
)
for index in "${!ids[@]}"; do
  id="${ids[$index]}"
  cp -- "$(find_source "${names[$index]}")" "$stage/runtime-ttf/$id.ttf"
  (cd "$stage/runtime-ttf" && woff2_compress "$id.ttf" >/dev/null)
  generated="$stage/runtime-ttf/$id.woff2"
  [[ -f "$generated" ]] || { echo "woff2_compress did not produce $generated" >&2; exit 1; }
  mv -- "$generated" "$stage/web-woff2/$id.woff2"
done

cp -- "$destination/licenses/sarasa-gothic.txt" "$stage/licenses/sarasa-gothic.txt"
cp -- "$destination/licenses/lxgw-wenkai-ofl.txt" "$stage/licenses/lxgw-wenkai-ofl.txt"

STAGE="$stage" python3 - <<'PY'
import hashlib
import json
import os
from pathlib import Path

stage = Path(os.environ["STAGE"])
rows = [
    ("sarasa-fixed-sc-1.0.40-light", "Sarasa Fixed SC Light", "sarasa-fixed-sc", "1.0.40", 300, "Sarasa Fixed SC", "sarasa-gothic.txt"),
    ("sarasa-fixed-sc-1.0.40-regular", "Sarasa Fixed SC Regular", "sarasa-fixed-sc", "1.0.40", 400, "Sarasa Fixed SC", "sarasa-gothic.txt"),
    ("sarasa-fixed-sc-1.0.40-medium", "Sarasa Fixed SC Medium", "sarasa-fixed-sc", "1.0.40", 600, "Sarasa Fixed SC", "sarasa-gothic.txt"),
    ("lxgw-wenkai-mono-1.522-light", "霞鹜文楷 Mono Light", "lxgw-wenkai-mono", "1.522", 300, "LXGW WenKai Mono", "lxgw-wenkai-ofl.txt"),
    ("lxgw-wenkai-mono-1.522-regular", "霞鹜文楷 Mono Regular", "lxgw-wenkai-mono", "1.522", 400, "LXGW WenKai Mono", "lxgw-wenkai-ofl.txt"),
    ("lxgw-wenkai-mono-1.522-medium", "霞鹜文楷 Mono Medium", "lxgw-wenkai-mono", "1.522", 500, "LXGW WenKai Mono", "lxgw-wenkai-ofl.txt"),
]
def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
items = []
for face_id, display, family, version, weight, runtime_family, license_name in rows:
    ttf = stage / "runtime-ttf" / f"{face_id}.ttf"
    woff = stage / "web-woff2" / f"{face_id}.woff2"
    if not woff.is_file():
        raise SystemExit(f"no WOFF2 output was generated for {face_id}")
    woff_digest = sha(woff)
    renamed = stage / "web-woff2" / f"{woff_digest}.woff2"
    woff.rename(renamed)
    items.append({
        "faceId": face_id, "displayName": display, "family": family, "sourceVersion": version, "weight": weight,
        "runtimeFamilyName": runtime_family, "runtimeTtfPath": f"runtime-ttf/{face_id}.ttf", "runtimeTtfSha256": sha(ttf), "runtimeTtfByteLength": ttf.stat().st_size,
        "webWoff2Path": f"web-woff2/{renamed.name}", "webWoff2Sha256": woff_digest, "webWoff2ByteLength": renamed.stat().st_size,
        "licenseId": "OFL-1.1", "licenseFile": f"licenses/{license_name}",
    })
(stage / "catalog.json").write_text(json.dumps({"schemaVersion": 1, "defaultFaceId": "sarasa-fixed-sc-1.0.40-regular", "items": items}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
PY

echo "Generated candidate catalog and assets in $stage" >&2
echo "Review provenance, run scripts/verify-runtime-fonts.sh, then replace the checked-in assets in a dedicated signed import commit." >&2

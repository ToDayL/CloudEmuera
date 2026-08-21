#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

python3 - <<'PY'
import hashlib
import json
import re
import sys
from pathlib import Path

root = Path.cwd()
matrix_path = root / "docs/runtime-capabilities.json"
schema_path = root / "docs/runtime-capabilities.schema.json"
matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
schema = json.loads(schema_path.read_text(encoding="utf-8"))

expected_upstream = "2175f8a629257efb08214e093704b3a3d3d06d05"
expected_matrix = "p1-07"
expected_runtime = "headless-p0.5.1"
expected_protocol = 5
expected_digest = hashlib.sha256(
    f"cloudemuera:{expected_matrix}:{expected_upstream}:structured-console-v5-display-commit".encode()
).hexdigest()

def fail(message):
    raise SystemExit(f"capability verification failed: {message}")

if schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
    fail("schema draft marker is missing")
if matrix.get("schemaVersion") != 1:
    fail("schemaVersion must be 1")
if matrix.get("matrixVersion") != expected_matrix:
    fail("matrixVersion is not p1-07")
if matrix.get("upstreamCommit") != expected_upstream:
    fail("matrix upstream commit does not match the pinned source")
if matrix.get("runtimeIntegrationVersion") != expected_runtime:
    fail("runtime integration version is not the pinned headless version")
if matrix.get("protocolVersion") != expected_protocol:
    fail("structured protocol version is not 5")
if matrix.get("capabilitySetDigest") != expected_digest:
    fail("capability digest does not match the canonical input")

required = {
    "capabilityId", "upstreamCommit", "upstreamEntrypoints", "category", "classification",
    "reasonCode", "adapterTypes", "ipcTypes", "fixtureScenarios", "testNames", "securityNotes",
}
allowed_classifications = {"Supported", "Compatible", "Experimental", "Blocked"}
blocked_reasons = {"HOST_SHIM", "SECURITY_BOUNDARY"}
seen_ids = set()
seen_entrypoints = {}
capabilities = matrix.get("capabilities")
if not isinstance(capabilities, list) or not capabilities:
    fail("capabilities must be a non-empty array")

for capability in capabilities:
    if not isinstance(capability, dict):
        fail("capability entries must be objects")
    missing = required - capability.keys()
    if missing:
        fail(f"{capability.get('capabilityId', '<unknown>')} misses {sorted(missing)}")
    capability_id = capability["capabilityId"]
    if capability_id in seen_ids:
        fail(f"duplicate capabilityId {capability_id}")
    seen_ids.add(capability_id)
    if capability["upstreamCommit"] != expected_upstream:
        fail(f"{capability_id} has a different upstream commit")
    classification = capability["classification"]
    if classification not in allowed_classifications:
        fail(f"{capability_id} has unknown classification {classification}")
    if classification == "Blocked" and capability["reasonCode"] not in blocked_reasons:
        fail(f"{capability_id} uses an unapproved Blocked reason")
    if classification == "Supported":
        for field in ("adapterTypes", "ipcTypes", "fixtureScenarios", "testNames"):
            if not capability[field]:
                fail(f"Supported capability {capability_id} has no {field} evidence")
    for entrypoint in capability["upstreamEntrypoints"]:
        if not isinstance(entrypoint, str) or not entrypoint:
            fail(f"{capability_id} contains an invalid entrypoint")
        if entrypoint in seen_entrypoints:
            fail(f"entrypoint {entrypoint} maps to both {seen_entrypoints[entrypoint]} and {capability_id}")
        seen_entrypoints[entrypoint] = capability_id

    for test_name in capability["testNames"]:
        if not isinstance(test_name, str) or not test_name:
            fail(f"{capability_id} contains an invalid test name")

upstream_files = [p for p in (root / "src/CloudEmuera.EmueraRuntime/Upstream").rglob("*") if p.is_file()]
headless_files = [p for p in (root / "src/CloudEmuera.EmueraRuntime/UpstreamHeadless").rglob("*") if p.is_file()]
source_text = "\n".join(
    p.read_text(encoding="utf-8", errors="ignore")
    for p in upstream_files + headless_files
    if p.suffix.lower() in {".cs", ".txt", ".xml", ".json", ".erb"}
)
headless_console = (root / "src/CloudEmuera.EmueraRuntime/UpstreamHeadless/HeadlessEmueraConsole.cs").read_text(encoding="utf-8")
headless_platform = (root / "src/CloudEmuera.EmueraRuntime/UpstreamHeadless/HeadlessPlatformStubs.cs").read_text(encoding="utf-8")
all_source = "\n".join(p.read_text(encoding="utf-8", errors="ignore") for p in (root / "src").rglob("*.cs"))
test_source = "\n".join(p.read_text(encoding="utf-8", errors="ignore") for p in (root / "tests").rglob("*.cs"))
test_classes = set(re.findall(r"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)\b", test_source))
test_methods = set(re.findall(
    r"\b(?:public|internal)\s+(?:static\s+)?(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
    test_source,
))

for capability in capabilities:
    for test_name in capability["testNames"]:
        parts = test_name.split(".")
        if parts[0] not in test_classes or len(parts) > 1 and parts[-1] not in test_methods:
            fail(f"{capability['capabilityId']} cites missing test evidence {test_name}")

def has_word(text, value):
    return re.search(r"(?<![A-Za-z0-9_])" + re.escape(value) + r"(?![A-Za-z0-9_])", text, re.IGNORECASE) is not None

for entrypoint, capability_id in seen_entrypoints.items():
    if entrypoint == "HeadlessEmueraConsole:host-shim-surface":
        if "class EmueraConsole" not in headless_console:
            fail("headless Console surface marker has no target class")
        continue
    if entrypoint == "HeadlessPlatformStubs:compile-time-shims":
        if "headless platform boundary" not in headless_platform:
            fail("platform shim marker has no boundary declaration")
        continue
    if entrypoint.startswith("HeadlessEmueraConsole."):
        if not has_word(headless_console, entrypoint.split(".", 1)[1]):
            fail(f"{entrypoint} is not present in HeadlessEmueraConsole")
        continue
    if entrypoint.startswith("HeadlessAudioBridge."):
        if not has_word(headless_platform, entrypoint.split(".", 1)[1]):
            fail(f"{entrypoint} is not present in HeadlessPlatformStubs")
        continue
    if entrypoint in {"RuntimeImageMetadataPort", "RuntimeFilePath", "SessionRootPublishedManifest"}:
        if not has_word(all_source, entrypoint):
            fail(f"{entrypoint} is not present in the runtime source")
        continue
    if entrypoint == "emuera.manifest":
        if not any(p.name == "emuera.manifest" for p in upstream_files):
            fail("pinned emuera.manifest is missing")
        continue
    if not has_word(source_text, entrypoint):
        fail(f"upstream entrypoint {entrypoint} is not present in the pinned source")

baseline = (root / "src/CloudEmuera.RuntimeAdapter/RuntimeBaseline.cs").read_text(encoding="utf-8")
if f'UpstreamCommit = "{expected_upstream}"' not in baseline:
    fail("RuntimeBaseline upstream commit drifted")
if f'CloudEmueraIntegrationVersion = "{expected_runtime}"' not in baseline:
    fail("RuntimeBaseline integration version drifted")
if f'CapabilityMatrixVersion = "{expected_matrix}"' not in baseline:
    fail("RuntimeBaseline capability matrix version is missing")
if f'CapabilitySetDigest = "{expected_digest}"' not in baseline:
    fail("RuntimeBaseline capability digest is missing")

protocol = (root / "src/CloudEmuera.Ipc/StructuredIpcProtocol.cs").read_text(encoding="utf-8")
if "CurrentVersion = 5" not in protocol or "CapabilityMatrixVersion = \"p1-07\"" not in protocol:
    fail("v5 structured IPC constants are not frozen")
proto = (root / "src/CloudEmuera.Ipc/Protos/structured-worker.proto").read_text(encoding="utf-8")
for required_proto in ("package cloudemuera.ipc.v5;", "string capability_set_digest", "message ConsoleSnapshot", "message ConsoleTransaction"):
    if required_proto not in proto:
        fail(f"structured-worker.proto misses {required_proto}")

manifest_source = (root / "src/CloudEmuera.Infrastructure/Sessions/SqliteSessionApplicationService.cs").read_text(encoding="utf-8")
for manifest_field in ("CapabilityMatrixVersion", "CapabilitySetDigest"):
    if manifest_field not in manifest_source:
        fail(f"runtime manifest does not persist {manifest_field}")

worker_controller = (root / "src/CloudEmuera.Worker/WorkerRuntimeController.cs").read_text(encoding="utf-8")
if "NoOpRuntimeAudioPort" in worker_controller:
    fail("production Worker still references NoOpRuntimeAudioPort")
if "StructuredRuntimeAudioPort" not in worker_controller:
    fail("production Worker does not construct StructuredRuntimeAudioPort")

print(f"capability matrix OK: {len(capabilities)} capabilities, {len(seen_entrypoints)} unique entrypoints, digest {expected_digest}")
PY

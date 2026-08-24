#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { generateApiTypes } from "./generate-api-types.mjs";
import { generateRealtimeTypes } from "./generate-realtime-types.mjs";
import { generateCapabilities } from "./generate-capabilities.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const failures = [];

function fail(message) { failures.push(message); }
function requireValue(condition, message) { if (!condition) fail(message); }

async function read(relativePath) {
  return readFile(path.join(root, relativePath), "utf8");
}

function interfaceFields(source, name) {
  const match = source.match(new RegExp(`export interface ${name}\\s*\\{([\\s\\S]*?)\\n\\}`, "m"));
  if (!match) { fail(`missing generated interface ${name}`); return []; }
  return [...match[1].matchAll(/^\s*([A-Za-z][A-Za-z0-9_]*)\??\s*:/gm)].map(item => item[1]).sort();
}

function assertFields(actual, expected, name) {
  const sorted = [...expected].sort();
  requireValue(JSON.stringify(actual) === JSON.stringify(sorted), `${name} fields drifted: expected ${sorted.join(",")}, got ${actual.join(",")}`);
}

const schema = JSON.parse(await read("src/CloudEmuera.Contracts/Realtime/realtime-v4.schema.json"));
const protocolCs = await read("src/CloudEmuera.Contracts/Realtime/RealtimeProtocol.cs");
const realtimeGenerated = await read("src/CloudEmuera.Web/src/realtime/generated.ts");
const apiGenerated = await read("src/CloudEmuera.Web/src/api/generated.ts");
const capabilityMatrix = JSON.parse(await read("src/CloudEmuera.Contracts/Realtime/runtime-capabilities.json"));
const capabilityTs = await read("src/CloudEmuera.Web/src/realtime/capabilities.ts");

const payloadSchemaVersion = schema?.$defs?.serverHello?.properties?.payloadSchemaVersion?.const;
requireValue(typeof payloadSchemaVersion === "string", "realtime schema must declare a serverHello payloadSchemaVersion const");
requireValue(protocolCs.includes(`PayloadSchemaVersion = \"${payloadSchemaVersion}\"`), "C# realtime payload schema version drifted");
requireValue(realtimeGenerated.includes("GENERATED CONTRACT SNAPSHOT"), "realtime generated snapshot header is missing");
requireValue(realtimeGenerated.includes(`REALTIME_PAYLOAD_SCHEMA_VERSION = \"${payloadSchemaVersion}\"`), "TypeScript realtime payload schema version drifted");
requireValue(apiGenerated.includes("GENERATED CONTRACT SNAPSHOT"), "API generated snapshot header is missing");
requireValue(realtimeGenerated === generateRealtimeTypes(schema), "realtime/generated.ts is not reproducible from realtime-v4.schema.json");
requireValue(capabilityTs === generateCapabilities(capabilityMatrix), "realtime/capabilities.ts is not reproducible from the runtime capability matrix");
for (const generatedType of ["RealtimeSnapshotPayload", "RealtimeNode", "RealtimeDrawable", "RealtimeOperation", "ConsoleState"]) {
  requireValue(new RegExp(`(?:interface|type) ${generatedType}\\b`).test(realtimeGenerated), `realtime generated type ${generatedType} is missing`);
}
requireValue(!realtimeGenerated.includes('from "./protocol"'), "realtime generated types must not delegate to protocol.ts");

function unionMembers(source, name) {
  const match = source.match(new RegExp(`export type ${name}\\s*=([\\s\\S]*?);`));
  return match ? [...match[1].matchAll(/"([^"\n]+)"/g)].map(item => item[1]).sort() : [];
}
const messageTypes = schema?.properties?.type?.enum ?? [];
const clientMessageTypes = new Set(["client.hello", "connection.pong", "session.resume", "session.unsubscribe", "session.input"]);
assertFields(unionMembers(realtimeGenerated, "RealtimeClientType"), messageTypes.filter(item => clientMessageTypes.has(item)), "RealtimeClientType");
assertFields(unionMembers(realtimeGenerated, "RealtimeServerType"), messageTypes.filter(item => !clientMessageTypes.has(item)), "RealtimeServerType");

assertFields(interfaceFields(apiGenerated, "SessionPresentationManifestDto"), ["schemaVersion", "assets", "fonts", "fontDiagnostics"], "SessionPresentationManifestDto");
assertFields(interfaceFields(apiGenerated, "SessionPresentationFontDto"), ["family", "assetId", "fallback", "cssFamily", "aliases"], "SessionPresentationFontDto");
assertFields(interfaceFields(apiGenerated, "SessionPresentationAssetDto"), ["assetId", "mediaType", "byteLength", "contentDigest", "eTag"], "SessionPresentationAssetDto");

const capabilityBlock = capabilityTs.match(/SUPPORTED_CAPABILITIES\s*=\s*\[([\s\S]*?)\]\s+as const/);
requireValue(Boolean(capabilityBlock), "capabilities.ts generated capability list is missing");
if (capabilityBlock) {
  const generatedCapabilities = [...capabilityBlock[1].matchAll(/"([^"\n]+)"/g)].map(item => item[1]);
  const knownCapabilities = new Set(capabilityMatrix.capabilities.map(item => item.capabilityId));
  requireValue(new Set(generatedCapabilities).size === generatedCapabilities.length, "SUPPORTED_CAPABILITIES contains duplicates");
  requireValue(generatedCapabilities.every(item => knownCapabilities.has(item)), "SUPPORTED_CAPABILITIES contains an unknown runtime capability");
}
requireValue(capabilityTs.includes(`CAPABILITY_DIGEST = \"${capabilityMatrix.capabilitySetDigest}\"`), "capability digest drifted from the runtime capability matrix");

const openApiUrl = process.env.CLOUDEMUERA_OPENAPI_URL;
if (openApiUrl) {
  try {
    const response = await fetch(openApiUrl);
    requireValue(response.ok, `OpenAPI endpoint returned ${response.status}`);
    if (response.ok) {
      const document = await response.json();
      requireValue(apiGenerated === generateApiTypes(document), "api/generated.ts is not reproducible from the live OpenAPI document");
      const schemas = document?.components?.schemas ?? {};
      const expectedSchemas = {
        SessionPresentationManifest: ["schemaVersion", "assets", "fonts", "fontDiagnostics"],
        SessionPresentationFont: ["family", "assetId", "fallback", "cssFamily", "aliases"],
        SessionPresentationAsset: ["assetId", "mediaType", "byteLength", "contentDigest", "eTag"],
      };
      for (const [name, fields] of Object.entries(expectedSchemas)) {
        const actual = Object.keys(schemas[name]?.properties ?? {}).sort();
        assertFields(actual, fields, `OpenAPI ${name}`);
      }
      for (const route of [
        "/api/v1/sessions/{sessionId}/presentation-manifest",
        "/api/v1/sessions/{sessionId}/assets/{assetId}",
      ]) requireValue(Boolean(document?.paths?.[route]), `OpenAPI route missing: ${route}`);
    }
  } catch (error) {
    fail(`OpenAPI drift check failed: ${error instanceof Error ? error.message : String(error)}`);
  }
}

if (failures.length > 0) {
  console.error(`generated contract verification failed:\n- ${failures.join("\n- ")}`);
  process.exitCode = 1;
} else {
  console.log(`generated contract verification passed (payload schema ${payloadSchemaVersion})`);
}

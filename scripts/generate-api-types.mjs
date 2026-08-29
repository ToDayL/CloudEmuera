#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outputPath = path.join(root, "src/CloudEmuera.Web/src/api/generated.ts");
const apiUrl = process.env.CLOUDEMUERA_OPENAPI_URL ?? "http://api:28647/openapi/v1.json";
const schemaOrder = [
  ["SessionGameResponse", "SessionGameSummaryDto"],
  ["SessionResponse", "SessionResponseDto"],
  ["SessionListResponse", "SessionListResponseDto"],
  ["SaveItemResponse", "SaveItemResponseDto"],
  ["SaveListResponse", "SaveListResponseDto"],
  ["SessionPresentationAsset", "SessionPresentationAssetDto"],
  ["SessionPresentationFont", "SessionPresentationFontDto"],
  ["SessionPresentationManifest", "SessionPresentationManifestDto"],
];
const aliases = [
  ["SessionStateDto", ["CREATING", "STARTING", "RUNNING", "STOPPING", "CLOSED", "CRASHED"]],
  ["RuntimeWidthModeDto", ["ORIGINAL", "MAX", "ADAPTIVE", "CUSTOM"]],
  ["SaveLayoutDto", ["ROOT", "SAV_DIRECTORY"]],
];
const fieldTypeOverrides = {
  SessionResponse: { state: "SessionStateDto", widthMode: "RuntimeWidthModeDto", customWidth: "number | null" },
  SaveListResponse: { layout: "SaveLayoutDto" },
};

export function generateApiTypes(document) {
  const schemas = document?.components?.schemas;
  if (!schemas || typeof schemas !== "object") throw new Error("OpenAPI components.schemas is missing.");
  const schemaNames = new Map(schemaOrder.map(([schema, dto]) => [schema, dto]));
  const lines = [
    "/*",
    " * GENERATED CONTRACT SNAPSHOT — do not add UI behavior here.",
    " *",
    " * Source: /openapi/v1.json exposed by CloudEmuera.Api.",
    " * Regenerate: node scripts/generate-api-types.mjs",
    " */",
    "",
  ];
  for (const [name, values] of aliases) {
    lines.push(`export type ${name} = ${values.map(value => JSON.stringify(value)).join(" | ")};`, "");
  }
  for (const [schemaName, dtoName] of schemaOrder) {
    const schema = schemas[schemaName];
    if (!schema) throw new Error(`OpenAPI schema ${schemaName} is missing.`);
    if (schema.type !== "object" || !schema.properties) throw new Error(`OpenAPI schema ${schemaName} is not an object.`);
    const required = new Set(schema.required ?? []);
    lines.push(`export interface ${dtoName} {`);
    for (const [property, propertySchema] of Object.entries(schema.properties)) {
      const optional = required.has(property) ? "" : "?";
      const override = fieldTypeOverrides[schemaName]?.[property];
      lines.push(`  ${property}${optional}: ${override ?? typeScriptType(propertySchema, schemaNames)};`);
    }
    lines.push("}", "");
  }
  return `${lines.join("\n").trimEnd()}\n`;
}

function typeScriptType(schema, schemaNames) {
  if (schema?.$ref) {
    const name = schema.$ref.split("/").pop();
    return schemaNames.get(name) ?? name;
  }
  if (Array.isArray(schema?.type)) {
    if (schema.type.includes("integer") || schema.type.includes("number")) return "number";
    const types = schema.type.map(type => typeScriptType({ ...schema, type }, schemaNames));
    return [...new Set(types)].sort((left, right) => left === "null" ? 1 : right === "null" ? -1 : left.localeCompare(right)).join(" | ");
  }
  if (schema?.type === "array") return `${typeScriptType(schema.items ?? {}, schemaNames)}[]`;
  if (schema?.type === "null") return "null";
  if (schema?.format === "date-time") return "string";
  if (schema?.type === "integer" || schema?.type === "number") return "number";
  if (schema?.type === "boolean") return "boolean";
  if (schema?.type === "string") return "string";
  return "unknown";
}

async function loadDocument() {
  const response = await fetch(apiUrl);
  if (!response.ok) throw new Error(`OpenAPI endpoint returned ${response.status}`);
  return response.json();
}

async function main() {
  const check = process.argv.includes("--check");
  const generated = generateApiTypes(await loadDocument());
  if (check) {
    const current = await readFile(outputPath, "utf8");
    if (current !== generated) {
      console.error(`Generated API types drifted: ${path.relative(root, outputPath)}`);
      process.exitCode = 1;
      return;
    }
    console.log(`API types are up to date: ${path.relative(root, outputPath)}`);
    return;
  }
  await writeFile(outputPath, generated, "utf8");
  console.log(`Generated ${path.relative(root, outputPath)}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();

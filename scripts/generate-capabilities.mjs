#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourcePath = path.join(root, "src/CloudEmuera.Contracts/Realtime/runtime-capabilities.json");
const outputPath = path.join(root, "src/CloudEmuera.Web/src/realtime/capabilities.ts");

export function generateCapabilities(matrix) {
  const capabilities = matrix?.capabilities;
  const digest = matrix?.capabilitySetDigest;
  if (!Array.isArray(capabilities) || typeof digest !== "string") throw new Error("runtime capability matrix is incomplete.");
  const supported = capabilities
    .filter(item => item?.classification === "Supported" && typeof item.capabilityId === "string")
    .map(item => item.capabilityId);
  if (new Set(supported).size !== supported.length) throw new Error("runtime capability matrix contains duplicate supported IDs.");
  return [
    "/** GENERATED CONTRACT SNAPSHOT — source: src/CloudEmuera.Contracts/Realtime/runtime-capabilities.json. */",
    `export const CAPABILITY_DIGEST = ${JSON.stringify(digest)};`,
    "export const SUPPORTED_CAPABILITIES = [",
    ...supported.map(capability => `  ${JSON.stringify(capability)},`),
    "] as const;",
    "",
  ].join("\n");
}

async function main() {
  const check = process.argv.includes("--check");
  const matrix = JSON.parse(await readFile(sourcePath, "utf8"));
  const generated = generateCapabilities(matrix);
  if (check) {
    const current = await readFile(outputPath, "utf8");
    if (current !== generated) {
      console.error(`Generated capabilities drifted: ${path.relative(root, outputPath)}`);
      process.exitCode = 1;
      return;
    }
    console.log(`Capabilities are up to date: ${path.relative(root, outputPath)}`);
    return;
  }
  await writeFile(outputPath, generated, "utf8");
  console.log(`Generated ${path.relative(root, outputPath)}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();

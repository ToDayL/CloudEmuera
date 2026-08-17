#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const schemaPath = path.join(root, "src/CloudEmuera.Contracts/Realtime/realtime-v1.schema.json");
const outputPath = path.join(root, "src/CloudEmuera.Web/src/realtime/generated.ts");

const refTypes = {
  identifier: "string",
  digest: "string",
  empty: "EmptyPayload",
  clientHello: "ClientHelloPayload",
  serverHello: "ServerHelloPayload",
  ping: "PingPayload",
  pong: "PongPayload",
  resume: "ResumePayload",
  resumeResult: "ResumeResultPayload",
  snapshot: "RealtimeSnapshotPayload",
  batch: "RealtimeTransactionBatchPayload",
  resync: "ResyncRequiredPayload",
  streamEnded: "StreamEndedPayload",
  input: "InputPayload",
  pointer: "InputPointer",
  key: "InputKey",
  inputResult: "InputResultPayload",
  protocolError: "ProtocolErrorPayload",
  color: "RealtimeColor",
  point: "RealtimePoint",
  rect: "RealtimeRect",
  insets: "RealtimeInsets",
  box: "RealtimeBoxModel",
  style: "RealtimeTextStyle",
  html: "RealtimeHtmlNode",
  node: "RealtimeNode",
  animationFrame: "SpriteAnimationFrame",
  background: "BackgroundLayer",
  drawable: "RealtimeDrawable",
  hitRegion: "HitRegion",
  scene: "CanvasScene",
  media: "MediaChannel",
  mediaState: "MediaState",
  constraints: "InputConstraints",
  prompt: "Prompt",
  window: "WindowMetadata",
  truncation: "Truncation",
  line: "RealtimeLine",
  consoleState: "ConsoleState",
  transaction: "RealtimeTransaction",
  operation: "RealtimeOperation",
};

const objectDefinitions = [
  ["clientHello", "ClientHelloPayload"],
  ["serverHello", "ServerHelloPayload"],
  ["ping", "PingPayload"],
  ["pong", "PongPayload"],
  ["resume", "ResumePayload"],
  ["resumeResult", "ResumeResultPayload"],
  ["snapshot", "RealtimeSnapshotPayload"],
  ["batch", "RealtimeTransactionBatchPayload"],
  ["resync", "ResyncRequiredPayload"],
  ["streamEnded", "StreamEndedPayload"],
  ["input", "InputPayload"],
  ["pointer", "InputPointer"],
  ["key", "InputKey"],
  ["inputResult", "InputResultPayload"],
  ["protocolError", "ProtocolErrorPayload"],
  ["color", "RealtimeColor"],
  ["point", "RealtimePoint"],
  ["rect", "RealtimeRect"],
  ["insets", "RealtimeInsets"],
  ["box", "RealtimeBoxModel"],
  ["style", "RealtimeTextStyle"],
  ["animationFrame", "SpriteAnimationFrame"],
  ["background", "BackgroundLayer"],
  ["hitRegion", "HitRegion"],
  ["scene", "CanvasScene"],
  ["media", "MediaChannel"],
  ["mediaState", "MediaState"],
  ["constraints", "InputConstraints"],
  ["prompt", "Prompt"],
  ["window", "WindowMetadata"],
  ["truncation", "Truncation"],
  ["line", "RealtimeLine"],
  ["consoleState", "ConsoleState"],
  ["transaction", "RealtimeTransaction"],
];

const variantDefinitions = [
  ["html", "RealtimeHtmlNode", [
    ["text", "HtmlTextNode"],
    ["break", "HtmlBreakNode"],
    ["element", "HtmlElementNode"],
  ]],
  ["node", "RealtimeNode", [
    ["text", "TextNode"],
    ["lineBreak", "LineBreakNode"],
    ["button", "ButtonNode"],
    ["image", "ImageNode"],
    ["sprite", "SpriteNode"],
    ["shape", "ShapeNode"],
    ["htmlIsland", "HtmlIslandNode"],
    ["div", "DivNode"],
  ]],
  ["drawable", "RealtimeDrawable", [
    ["sprite", "SpriteDrawable"],
    ["shape", "ShapeDrawable"],
    ["htmlIsland", "HtmlIslandDrawable"],
    ["raster", "RasterDrawable"],
  ]],
  ["operation", "RealtimeOperation", [
    ["appendNodes", "AppendNodesOperation"],
    ["clear", "ClearOperation"],
    ["openPrompt", "OpenPromptOperation"],
    ["closePrompt", "ClosePromptOperation"],
    ["line", "LineOperation"],
    ["appendInline", "AppendInlineOperation"],
    ["deleteLines", "DeleteLinesOperation"],
    ["setWindowMetadata", "SetWindowMetadataOperation"],
    ["upsertBackground", "UpsertBackgroundOperation"],
    ["removeBackground", "RemoveBackgroundOperation"],
    ["upsertDrawable", "UpsertDrawableOperation"],
    ["removeDrawable", "RemoveDrawableOperation"],
    ["clearSceneRange", "ClearSceneRangeOperation"],
    ["upsertHitRegion", "UpsertHitRegionOperation"],
    ["removeHitRegion", "RemoveHitRegionOperation"],
    ["setMediaChannel", "SetMediaChannelOperation"],
    ["stopMediaChannel", "StopMediaChannelOperation"],
  ]],
];

const clientMessageTypes = new Set(["client.hello", "connection.pong", "session.resume", "session.unsubscribe", "session.input"]);

export function generateRealtimeTypes(schema) {
  const defs = schema?.$defs;
  const messageTypes = schema?.properties?.type?.enum;
  const protocolVersion = schema?.properties?.protocolVersion?.const;
  const payloadSchemaVersion = schema?.$defs?.serverHello?.properties?.payloadSchemaVersion?.const;
  if (!defs || !Array.isArray(messageTypes) || typeof protocolVersion !== "number" || typeof payloadSchemaVersion !== "string") {
    throw new Error("realtime schema discriminator, protocol version, or payload version is missing.");
  }

  const lines = [
    "/* GENERATED CONTRACT SNAPSHOT — source: realtime-v1.schema.json. */",
    `export const REALTIME_SCHEMA_ID = ${JSON.stringify(schema.$id)} as const;`,
    `export const REALTIME_PROTOCOL_VERSION = ${JSON.stringify(protocolVersion)} as const;`,
    `export const REALTIME_PAYLOAD_SCHEMA_VERSION = ${JSON.stringify(payloadSchemaVersion)} as const;`,
    `export const REALTIME_MESSAGE_TYPES = ${JSON.stringify(messageTypes)} as const;`,
    "",
    "export type EmptyPayload = Record<never, never>;",
    "",
  ];

  for (const [schemaName, typeName] of objectDefinitions) {
    lines.push(renderDefinition(typeName, defs[schemaName], defs), "");
  }

  for (const [schemaName, unionName, variants] of variantDefinitions) {
    const schemaVariants = defs[schemaName]?.oneOf;
    if (!Array.isArray(schemaVariants) || schemaVariants.length !== variants.length) throw new Error(`realtime schema ${schemaName} variants changed; update generator mapping.`);
    for (let index = 0; index < variants.length; index++) {
      lines.push(renderDefinition(variants[index][1], schemaVariants[index], defs), "");
    }
    lines.push(`export type ${unionName} = ${variants.map(([, typeName]) => typeName).join(" | ")};`, "");
  }

  lines.push(
    `export type InputSource = ${typeScriptType(defs.input.properties.source, defs)};`,
    `export type ResumeStatus = ${typeScriptType(defs.resumeResult.properties.status, defs)};`,
    `export type InputResultStatus = ${typeScriptType(defs.inputResult.properties.status, defs)};`,
    `export type ShapeKind = ${typeScriptType(findVariantProperty(defs.node, "shape"), defs)};`,
    `export type InputType = ${typeScriptType(defs.prompt.properties.inputType, defs)};`,
    "",
    "export interface RealtimeEnvelope<TType extends string, TPayload> {",
    "  protocolVersion: 1;",
    "  type: TType;",
    "  messageId: string;",
    "  correlationId?: string;",
    "  sessionId?: string;",
    "  workerEpoch?: number;",
    "  sequence?: number;",
    "  payload: TPayload;",
    "}",
    "",
  );

  const messageDefinitions = [
    ["client.hello", "ClientHelloPayload", "ClientHelloMessage"],
    ["connection.pong", "PongPayload", "PongMessage"],
    ["session.resume", "ResumePayload", "ResumeMessage"],
    ["session.unsubscribe", "EmptyPayload", "UnsubscribeMessage"],
    ["session.input", "InputPayload", "InputMessage"],
    ["server.hello", "ServerHelloPayload", null],
    ["connection.ping", "PingPayload", null],
    ["session.resume.result", "ResumeResultPayload", null],
    ["session.snapshot", "RealtimeSnapshotPayload", null],
    ["display.batch", "RealtimeTransactionBatchPayload", null],
    ["resync.required", "ResyncRequiredPayload", null],
    ["session.stream.ended", "StreamEndedPayload", null],
    ["session.input.result", "InputResultPayload", null],
    ["protocol.error", "ProtocolErrorPayload", null],
  ];
  for (const [messageType, payloadType, messageName] of messageDefinitions) {
    if (messageName) lines.push(`export type ${messageName} = RealtimeEnvelope<${JSON.stringify(messageType)}, ${payloadType}>;`);
  }
  lines.push(
    "",
    `export type RealtimeClientType = ${messageTypes.filter(type => clientMessageTypes.has(type)).map(JSON.stringify).join(" | ")};`,
    `export type RealtimeServerType = ${messageTypes.filter(type => !clientMessageTypes.has(type)).map(JSON.stringify).join(" | ")};`,
    "",
    `export type RealtimeClientMessage = ${messageDefinitions.filter(([messageType]) => clientMessageTypes.has(messageType)).map(([, , messageName]) => messageName).join(" | ")};`,
    `export type RealtimeServerMessage = ${messageDefinitions.filter(([messageType]) => !clientMessageTypes.has(messageType)).map(([messageType, payloadType]) => `RealtimeEnvelope<${JSON.stringify(messageType)}, ${payloadType}>`).join(" | ")};`,
    "",
  );
  return `${lines.join("\n").trimEnd()}\n`;
}

function renderDefinition(typeName, schema, defs) {
  if (!schema) throw new Error(`realtime schema definition for ${typeName} is missing.`);
  if (schema.type === "object" && !schema.oneOf) return `export interface ${typeName} ${renderObject(schema, defs)}`;
  return `export type ${typeName} = ${typeScriptType(schema, defs)};`;
}

function renderObject(schema, defs) {
  const required = new Set(schema.required ?? []);
  const properties = Object.entries(schema.properties ?? {});
  if (properties.length === 0) return "{ }";
  return [
    "{",
    ...properties.map(([property, propertySchema]) => `  ${property}${required.has(property) ? "" : "?"}: ${typeScriptType(propertySchema, defs)};`),
    "}",
  ].join("\n");
}

function typeScriptType(schema, defs) {
  if (!schema || typeof schema !== "object") return "unknown";
  if (schema.$ref) {
    const name = schema.$ref.split("/").pop();
    if (name && refTypes[name]) return refTypes[name];
    throw new Error(`unmapped realtime schema reference: ${schema.$ref}`);
  }
  if (Object.hasOwn(schema, "const")) return JSON.stringify(schema.const);
  if (Array.isArray(schema.enum)) return schema.enum.map(value => JSON.stringify(value)).join(" | ");
  if (Array.isArray(schema.anyOf)) return union(schema.anyOf.map(item => typeScriptType(item, defs)));
  if (Array.isArray(schema.oneOf)) return union(schema.oneOf.map(item => typeScriptType(item, defs)));
  if (Array.isArray(schema.type)) return union(schema.type.map(type => primitiveType(type)));
  if (schema.type === "array") {
    const itemType = typeScriptType(schema.items ?? {}, defs);
    return itemType.includes(" | ") ? `Array<${itemType}>` : `${itemType}[]`;
  }
  if (schema.type === "object") return renderObject(schema, defs);
  return primitiveType(schema.type);
}

function primitiveType(type) {
  return ({ string: "string", integer: "number", number: "number", boolean: "boolean", null: "null" })[type] ?? "unknown";
}

function union(types) {
  return [...new Set(types)].join(" | ");
}

function findVariantProperty(schema, property) {
  const variant = schema?.oneOf?.find(item => item.properties?.[property]);
  if (!variant) throw new Error(`realtime schema discriminator property ${property} is missing.`);
  return variant.properties[property];
}

async function main() {
  const check = process.argv.includes("--check");
  const schema = JSON.parse(await readFile(schemaPath, "utf8"));
  const generated = generateRealtimeTypes(schema);
  if (check) {
    const current = await readFile(outputPath, "utf8");
    if (current !== generated) {
      console.error(`Generated realtime types drifted: ${path.relative(root, outputPath)}`);
      process.exitCode = 1;
      return;
    }
    console.log(`Realtime types are up to date: ${path.relative(root, outputPath)}`);
    return;
  }
  await writeFile(outputPath, generated, "utf8");
  console.log(`Generated ${path.relative(root, outputPath)}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();

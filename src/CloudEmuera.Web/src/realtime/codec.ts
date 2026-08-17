import {
  MAX_REALTIME_JSON_DEPTH,
  MAX_REALTIME_MESSAGE_BYTES,
  REALTIME_PAYLOAD_SCHEMA_VERSION,
  RealtimeDrawable,
  RealtimeEnvelope,
  RealtimeHtmlNode,
  BackgroundLayer,
  ConsoleState,
  InputConstraints,
  MediaChannel,
  Prompt,
  RealtimeLine,
  RealtimeNode,
  RealtimeOperation,
  RealtimeServerMessage,
  RealtimeSnapshotPayload,
  RealtimeTextStyle,
  SpriteAnimationFrame,
  RealtimeTransactionBatchPayload,
  Truncation,
  WindowMetadata,
} from "./protocol";

type JsonObject = { [key: string]: JsonValue };
type JsonValue = null | boolean | number | string | JsonValue[] | JsonObject;

export class RealtimeDecodeError extends Error {
  readonly reasonCode: string;

  constructor(reasonCode: string, message: string) {
    super(message);
    this.name = "RealtimeDecodeError";
    this.reasonCode = reasonCode;
  }
}

const identifier = /^[A-Za-z0-9._~-]{1,128}$/;
const digest = /^[A-Za-z0-9:_-]{1,256}$/;
const pngSignature = "89504e470d0a1a0a";
const safeHtmlTags = ["span", "div", "p", "b", "strong", "i", "em", "u", "s", "strike", "img"] as const;

export function decodeRealtimeMessage(input: string | ArrayBuffer): RealtimeServerMessage {
  const text = typeof input === "string" ? input : new TextDecoder().decode(input);
  if (new TextEncoder().encode(text).byteLength > MAX_REALTIME_MESSAGE_BYTES)
    throw new RealtimeDecodeError("message_too_large", "实时消息超过浏览器上限。");
  scanJson(text);
  let value: JsonValue;
  try { value = JSON.parse(text) as JsonValue; }
  catch { throw new RealtimeDecodeError("invalid_json", "实时消息不是有效 JSON。"); }
  const envelope = object(value, "invalid_envelope");
  ensureKeys(envelope, ["protocolVersion", "type", "messageId", "correlationId", "sessionId", "workerEpoch", "sequence", "payload"], "envelope");
  if (integer(envelope.protocolVersion, "protocolVersion") !== 1)
    throw new RealtimeDecodeError("unsupported_protocol_version", "实时协议版本不兼容。");
  const type = string(envelope.type, "type");
  const messageId = string(envelope.messageId, "messageId");
  if (!identifier.test(messageId)) throw new RealtimeDecodeError("invalid_identifier", "实时消息 ID 无效。");
  if (envelope.correlationId !== undefined) checkIdentifier(envelope.correlationId, "correlationId");
  if (envelope.sessionId !== undefined) checkIdentifier(envelope.sessionId, "sessionId");
  if (envelope.workerEpoch !== undefined && positiveInteger(envelope.workerEpoch, "workerEpoch") === 0) throw new RealtimeDecodeError("invalid_envelope", "workerEpoch 无效。");
  if (envelope.sequence !== undefined && integer(envelope.sequence, "sequence") < 0) throw new RealtimeDecodeError("invalid_envelope", "sequence 无效。");
  const payload = object(envelope.payload, "missing_payload");
  const base = { protocolVersion: 1 as const, type, messageId, ...(envelope.correlationId === undefined ? {} : { correlationId: string(envelope.correlationId, "correlationId") }), ...(envelope.sessionId === undefined ? {} : { sessionId: string(envelope.sessionId, "sessionId") }), ...(envelope.workerEpoch === undefined ? {} : { workerEpoch: positiveInteger(envelope.workerEpoch, "workerEpoch") }), ...(envelope.sequence === undefined ? {} : { sequence: integer(envelope.sequence, "sequence") }) };

  switch (type) {
    case "server.hello": return { ...base, type, payload: decodeServerHello(payload) } as RealtimeServerMessage;
    case "connection.ping": return { ...base, type, payload: decodePing(payload) } as RealtimeServerMessage;
    case "session.resume.result": return { ...requireSession(base), type, payload: decodeResumeResult(payload) } as RealtimeServerMessage;
    case "session.snapshot": return { ...requireSession(base), type, payload: decodeSnapshot(payload) } as RealtimeServerMessage;
    case "display.batch": return { ...requireSession(base), type, payload: decodeBatch(payload) } as RealtimeServerMessage;
    case "resync.required": return { ...requireSession(base), type, payload: decodeResync(payload) } as RealtimeServerMessage;
    case "session.stream.ended": ensureKeys(payload, ["reasonCode"], "stream.ended"); return { ...requireSession(base), type, payload: { reasonCode: string(payload.reasonCode, "reasonCode") } } as RealtimeServerMessage;
    case "session.input.result": return { ...requireSession(base), type, payload: decodeInputResult(payload) } as RealtimeServerMessage;
    case "protocol.error": ensureKeys(payload, ["code", "message"], "protocol.error"); return { ...base, type, payload: { code: string(payload.code, "code"), message: string(payload.message, "message") } } as RealtimeServerMessage;
    default: throw new RealtimeDecodeError("unknown_type", `实时消息类型不受支持：${type}`);
  }
}

function requireSession<T extends { sessionId?: string }>(value: T): T & { sessionId: string } {
  if (!value.sessionId) throw new RealtimeDecodeError("missing_session_id", "Session 消息缺少 sessionId。");
  return value as T & { sessionId: string };
}

function decodeServerHello(value: JsonObject) {
  ensureKeys(value, ["protocolVersion", "payloadSchemaVersion", "connectionId", "serverNowUnixMilliseconds", "heartbeatIntervalMilliseconds", "heartbeatTimeoutMilliseconds", "maxSubscriptionsPerConnection", "maxPendingInputsPerConnection", "serverMessageMaxBytes", "capabilityDigest"], "server.hello");
  const protocolVersion = positiveInteger(value.protocolVersion, "protocolVersion");
  if (protocolVersion !== 1) throw new RealtimeDecodeError("unsupported_protocol_version", "服务端协议版本不兼容。");
  const payloadSchemaVersion = string(value.payloadSchemaVersion, "payloadSchemaVersion");
  if (payloadSchemaVersion !== REALTIME_PAYLOAD_SCHEMA_VERSION)
    throw new RealtimeDecodeError("unsupported_payload_schema_version", "实时消息内容版本不兼容。");
  return {
    protocolVersion: 1 as const,
    payloadSchemaVersion,
    connectionId: checkedIdentifier(value.connectionId, "connectionId"),
    serverNowUnixMilliseconds: finiteNumber(value.serverNowUnixMilliseconds, "serverNowUnixMilliseconds"),
    heartbeatIntervalMilliseconds: positiveInteger(value.heartbeatIntervalMilliseconds, "heartbeatIntervalMilliseconds"),
    heartbeatTimeoutMilliseconds: positiveInteger(value.heartbeatTimeoutMilliseconds, "heartbeatTimeoutMilliseconds"),
    maxSubscriptionsPerConnection: positiveInteger(value.maxSubscriptionsPerConnection, "maxSubscriptionsPerConnection"),
    maxPendingInputsPerConnection: positiveInteger(value.maxPendingInputsPerConnection, "maxPendingInputsPerConnection"),
    serverMessageMaxBytes: positiveInteger(value.serverMessageMaxBytes, "serverMessageMaxBytes"),
    capabilityDigest: checkedDigest(value.capabilityDigest),
  };
}

function decodePing(value: JsonObject) {
  ensureKeys(value, ["nonce", "serverNowUnixMilliseconds"], "connection.ping");
  return { nonce: checkedIdentifier(value.nonce, "nonce"), serverNowUnixMilliseconds: finiteNumber(value.serverNowUnixMilliseconds, "serverNowUnixMilliseconds") };
}

function decodeResumeResult(value: JsonObject) {
  ensureKeys(value, ["status", "workerEpoch", "reasonCode"], "session.resume.result");
  const status = string(value.status, "status");
  if (!["ACCEPTED", "CAPABILITY_MISMATCH", "SESSION_NOT_FOUND", "SESSION_NOT_RUNNING", "SNAPSHOT_NOT_READY", "SUBSCRIPTION_LIMIT_EXCEEDED"].includes(status))
    throw new RealtimeDecodeError("invalid_payload", "resume result 状态无效。");
  return { status, workerEpoch: value.workerEpoch === null || value.workerEpoch === undefined ? value.workerEpoch : positiveInteger(value.workerEpoch, "workerEpoch"), reasonCode: value.reasonCode === null || value.reasonCode === undefined ? value.reasonCode : string(value.reasonCode, "reasonCode") };
}

function decodeSnapshot(value: JsonObject): RealtimeSnapshotPayload {
  ensureKeys(value, ["workerEpoch", "snapshotSequence", "consoleState"], "session.snapshot");
  return { workerEpoch: positiveInteger(value.workerEpoch, "workerEpoch"), snapshotSequence: nonNegativeInteger(value.snapshotSequence, "snapshotSequence"), consoleState: decodeConsoleState(value.consoleState) };
}

function decodeBatch(value: JsonObject): RealtimeTransactionBatchPayload {
  ensureKeys(value, ["workerEpoch", "firstSequence", "lastSequence", "transactions"], "display.batch");
  const transactions = array(value.transactions, "transactions").map(item => decodeTransaction(object(item, "transaction")));
  if (transactions.length === 0) throw new RealtimeDecodeError("invalid_payload", "display.batch 不能为空。");
  const firstSequence = positiveInteger(value.firstSequence, "firstSequence");
  const lastSequence = positiveInteger(value.lastSequence, "lastSequence");
  if (lastSequence < firstSequence || transactions[0].sequence !== firstSequence || transactions.at(-1)?.sequence !== lastSequence)
    throw new RealtimeDecodeError("invalid_payload", "display.batch 序号不连续。");
  for (let i = 1; i < transactions.length; i++) if (transactions[i].sequence !== transactions[i - 1].sequence + 1)
    throw new RealtimeDecodeError("invalid_payload", "display.batch 内部序号不连续。");
  return { workerEpoch: positiveInteger(value.workerEpoch, "workerEpoch"), firstSequence, lastSequence, transactions };
}

function decodeResync(value: JsonObject) {
  ensureKeys(value, ["workerEpoch", "observedSequence", "reason"], "resync.required");
  return { workerEpoch: positiveInteger(value.workerEpoch, "workerEpoch"), observedSequence: nonNegativeInteger(value.observedSequence, "observedSequence"), reason: string(value.reason, "reason") };
}

function decodeInputResult(value: JsonObject) {
  ensureKeys(value, ["promptId", "clientMessageId", "status", "reasonCode", "normalizedValue"], "session.input.result");
  const status = string(value.status, "status");
  const statuses = ["ACCEPTED", "DUPLICATE", "CONFLICT", "STALE_PROMPT", "NO_ACTIVE_PROMPT", "INVALID_FORMAT", "INVALID_COMMAND", "CANCELLED", "TIMED_OUT", "SESSION_NOT_ACCEPTING_INPUT", "STALE_EPOCH", "SESSION_NOT_RUNNING", "INPUT_BACKPRESSURE", "WORKER_UNAVAILABLE", "FORBIDDEN"];
  if (!statuses.includes(status)) throw new RealtimeDecodeError("invalid_payload", "输入回执状态无效。");
  return { promptId: checkedIdentifier(value.promptId, "promptId"), clientMessageId: checkedIdentifier(value.clientMessageId, "clientMessageId"), status, reasonCode: string(value.reasonCode, "reasonCode"), normalizedValue: value.normalizedValue === null || value.normalizedValue === undefined ? value.normalizedValue : string(value.normalizedValue, "normalizedValue") };
}

function decodeConsoleState(value: JsonValue): ConsoleState {
  const state = object(value, "console_state");
  ensureKeys(state, ["scrollback", "backgroundLayers", "canvasScene", "mediaState", "currentPrompt", "windowMetadata", "truncation"], "consoleState");
  const mediaState = object(state.mediaState, "mediaState");
  ensureKeys(mediaState, ["channels"], "mediaState");
  return {
    scrollback: array(state.scrollback, "scrollback").map(item => decodeLine(object(item, "line"))),
    backgroundLayers: array(state.backgroundLayers, "backgroundLayers").map(item => decodeBackground(object(item, "backgroundLayer"))),
    canvasScene: decodeScene(object(state.canvasScene, "canvasScene")),
    mediaState: { channels: array(mediaState.channels, "channels").map(item => decodeMedia(object(item, "mediaChannel"))) },
    currentPrompt: state.currentPrompt === null || state.currentPrompt === undefined ? state.currentPrompt : decodePrompt(object(state.currentPrompt, "prompt")),
    windowMetadata: decodeWindow(object(state.windowMetadata, "windowMetadata")),
    truncation: decodeTruncation(object(state.truncation, "truncation")),
  };
}

function decodeLine(value: JsonObject): RealtimeLine {
  ensureKeys(value, ["lineId", "nodes", "alignment", "temporary", "noWrap"], "line");
  const alignment = string(value.alignment, "alignment");
  if (!["left", "center", "right"].includes(alignment)) throw new RealtimeDecodeError("invalid_payload", "line alignment 无效。");
  return { lineId: checkedIdentifier(value.lineId, "lineId"), nodes: array(value.nodes, "nodes").map(item => decodeNode(item)), alignment: alignment as RealtimeLine["alignment"], temporary: bool(value.temporary, "temporary"), noWrap: value.noWrap === undefined ? false : bool(value.noWrap, "noWrap") };
}

function decodeNode(value: JsonValue): RealtimeNode {
  const node = object(value, "node");
  const type = string(node.type, "node.type");
  const allowed = {
    text: ["type", "text", "style"],
    lineBreak: ["type"],
    button: ["type", "children", "value", "tooltip", "enabled", "generation", "positionX"],
    image: ["type", "assetId", "sourceRect", "destination", "altText", "decorative", "zIndex"],
    sprite: ["type", "assetId", "sourceRect", "destination", "frame", "zIndex", "opacity", "altText", "hoverAssetId", "hoverSourceRect", "mappingAssetId", "mappingSourceRect", "animationFrames"],
    shape: ["type", "shape", "bounds", "fill", "stroke", "zIndex", "points", "buttonColor"],
    htmlIsland: ["type", "root", "nodes", "layout"],
    div: ["type", "children", "bounds", "zIndex", "background", "isRelative", "box"],
  } as Record<string, string[]>;
  if (allowed[type]) ensureKeys(node, allowed[type], `node.${type}`);
  switch (type) {
    case "text": return { type: "text", text: string(node.text, "text"), style: decodeStyle(object(node.style, "style")) };
    case "lineBreak": return { type: "lineBreak" };
    case "button": return { type: "button", children: array(node.children, "children").map(decodeNode), value: string(node.value, "value"), tooltip: nullableString(node.tooltip, "tooltip"), enabled: bool(node.enabled, "enabled"), generation: nonNegativeInteger(node.generation, "generation"), positionX: optionalBoundedInteger(node.positionX, "positionX", -1_000_000, 1_000_000) };
    case "image": return { type: "image", assetId: checkedIdentifier(node.assetId, "assetId"), sourceRect: optionalRect(node.sourceRect, "sourceRect"), destination: optionalRect(node.destination, "destination"), altText: nullableString(node.altText, "altText"), decorative: bool(node.decorative, "decorative"), zIndex: integer(node.zIndex, "zIndex") };
    case "sprite": return { type: "sprite", assetId: checkedIdentifier(node.assetId, "assetId"), sourceRect: decodeRect(object(node.sourceRect, "sourceRect")), destination: decodeRect(object(node.destination, "destination")), frame: nonNegativeInteger(node.frame, "frame"), zIndex: integer(node.zIndex, "zIndex"), opacity: boundedNumber(node.opacity, "opacity", 0, 1), altText: nullableString(node.altText, "altText"), hoverAssetId: nullableIdentifier(node.hoverAssetId, "hoverAssetId"), hoverSourceRect: optionalRect(node.hoverSourceRect, "hoverSourceRect"), mappingAssetId: nullableIdentifier(node.mappingAssetId, "mappingAssetId"), mappingSourceRect: optionalRect(node.mappingSourceRect, "mappingSourceRect"), animationFrames: array(node.animationFrames, "animationFrames").map(item => decodeAnimationFrame(object(item, "animationFrame"))) };
    case "shape": return { type: "shape", shape: shapeKind(node.shape), bounds: decodeRect(object(node.bounds, "bounds")), fill: optionalColor(node.fill, "fill"), stroke: optionalColor(node.stroke, "stroke"), zIndex: integer(node.zIndex, "zIndex"), points: array(node.points, "points").map(item => decodePoint(object(item, "point"))), buttonColor: optionalColor(node.buttonColor, "buttonColor") };
    case "htmlIsland": {
      const hasRoot = node.root !== undefined && node.root !== null;
      const hasNodes = node.nodes !== undefined && node.nodes !== null;
      if (hasRoot === hasNodes) throw new RealtimeDecodeError("invalid_payload", "HTML Island 必须且只能包含 root 或 nodes。");
      return { type: "htmlIsland", root: hasRoot ? decodeHtml(object(node.root, "root")) : undefined, nodes: hasNodes ? array(node.nodes, "nodes").map(decodeNode) : undefined, layout: optionalRect(node.layout, "layout") };
    }
    case "div": return { type: "div", children: array(node.children, "children").map(decodeNode), bounds: decodeRect(object(node.bounds, "bounds")), zIndex: integer(node.zIndex, "zIndex"), background: optionalColor(node.background, "background"), isRelative: bool(node.isRelative, "isRelative"), box: node.box === null || node.box === undefined ? node.box : decodeBox(object(node.box, "box")) };
    default: throw new RealtimeDecodeError("unknown_node", "实时节点类型不受支持。");
  }
}

function decodeHtml(value: JsonObject): RealtimeHtmlNode {
  const type = string(value.type, "html.type");
  if (type === "text") ensureKeys(value, ["type", "text"], "html.text");
  if (type === "break") ensureKeys(value, ["type"], "html.break");
  if (type === "element") ensureKeys(value, ["type", "tag", "children", "style", "assetId", "altText"], "html.element");
  switch (type) {
    case "text": return { type: "text", text: string(value.text, "html.text") };
    case "break": return { type: "break" };
    case "element": {
      const tag = string(value.tag, "html.tag");
      if (!(safeHtmlTags as readonly string[]).includes(tag)) throw new RealtimeDecodeError("unsupported_html_tag", "HTML 标签不受支持。");
      return { type: "element", tag: tag as typeof safeHtmlTags[number], children: array(value.children, "html.children").map(item => decodeHtml(object(item, "html.child"))), style: value.style === null || value.style === undefined ? value.style : decodeStyle(object(value.style, "html.style")), assetId: nullableIdentifier(value.assetId, "html.assetId"), altText: nullableString(value.altText, "html.altText") };
    }
    default: throw new RealtimeDecodeError("unknown_html_node", "HTML 节点类型不受支持。");
  }
}

function decodeStyle(value: JsonObject): RealtimeTextStyle { ensureKeys(value, ["decorations", "fontFamily", "fontSize", "lineHeight", "foreground", "background", "buttonColor"], "style"); return { decorations: array(value.decorations, "decorations").map(item => string(item, "decoration")), fontFamily: string(value.fontFamily, "fontFamily"), fontSize: boundedNumber(value.fontSize, "fontSize", 1, 256), lineHeight: boundedNumber(value.lineHeight, "lineHeight", 0, 512), foreground: optionalColor(value.foreground, "foreground"), background: optionalColor(value.background, "background"), buttonColor: optionalColor(value.buttonColor, "buttonColor") }; }
function decodeColor(value: JsonObject) { ensureKeys(value, ["red", "green", "blue", "alpha"], "color"); return { red: byte(value.red, "red"), green: byte(value.green, "green"), blue: byte(value.blue, "blue"), alpha: byte(value.alpha, "alpha") }; }
function optionalColor(value: JsonValue | undefined, name: string) { return value === null || value === undefined ? value : decodeColor(object(value, name)); }
function decodeRect(value: JsonObject) { ensureKeys(value, ["x", "y", "width", "height"], "rect"); const rect = { x: integer(value.x, "x"), y: integer(value.y, "y"), width: positiveInteger(value.width, "width"), height: positiveInteger(value.height, "height") }; return rect; }
function optionalRect(value: JsonValue | undefined, name: string) { return value === null || value === undefined ? value : decodeRect(object(value, name)); }
function decodePoint(value: JsonObject) { ensureKeys(value, ["x", "y"], "point"); return { x: integer(value.x, "x"), y: integer(value.y, "y") }; }
function decodeInsets(value: JsonObject, name: string) { ensureKeys(value, ["top", "right", "bottom", "left"], name); return { top: boundedInteger(value.top, `${name}.top`, -1_000_000, 1_000_000), right: boundedInteger(value.right, `${name}.right`, -1_000_000, 1_000_000), bottom: boundedInteger(value.bottom, `${name}.bottom`, -1_000_000, 1_000_000), left: boundedInteger(value.left, `${name}.left`, -1_000_000, 1_000_000) }; }
function decodeBox(value: JsonObject) { ensureKeys(value, ["margin", "padding", "border", "radius", "borderColors"], "box"); const borderColors = array(value.borderColors, "borderColors"); if (borderColors.length !== 4) throw new RealtimeDecodeError("invalid_payload", "box.borderColors 必须包含四个颜色。"); return { margin: decodeInsets(object(value.margin, "box.margin"), "box.margin"), padding: decodeInsets(object(value.padding, "box.padding"), "box.padding"), border: decodeInsets(object(value.border, "box.border"), "box.border"), radius: decodeInsets(object(value.radius, "box.radius"), "box.radius"), borderColors: borderColors.map((color, index) => optionalColor(color, `box.borderColors[${index}]`) ?? null) }; }
function decodeBackground(value: JsonObject): BackgroundLayer { ensureKeys(value, ["layerId", "assetId", "mode", "opacity", "depth"], "background"); const mode = string(value.mode, "mode"); if (!["stretch", "contain", "cover", "center", "repeat"].includes(mode)) throw new RealtimeDecodeError("invalid_payload", "背景模式无效。"); return { layerId: checkedIdentifier(value.layerId, "layerId"), assetId: checkedIdentifier(value.assetId, "assetId"), mode: mode as BackgroundLayer["mode"], opacity: boundedNumber(value.opacity, "opacity", 0, 1), depth: integer(value.depth, "depth") }; }
function decodeScene(value: JsonObject) { ensureKeys(value, ["drawables", "hitRegions"], "canvasScene"); return { drawables: array(value.drawables, "drawables").map(item => decodeDrawable(object(item, "drawable"))), hitRegions: array(value.hitRegions, "hitRegions").map(item => decodeHitRegion(object(item, "hitRegion"))) }; }
function decodeDrawable(value: JsonObject): RealtimeDrawable { const type = string(value.type, "drawable.type"); const allowed = { sprite: ["type", "drawableId", "bounds", "zIndex", "opacity", "assetId", "sourceRect", "frame", "animationFrames"], shape: ["type", "drawableId", "bounds", "zIndex", "opacity", "shape", "fill", "stroke", "points"], htmlIsland: ["type", "drawableId", "bounds", "zIndex", "opacity", "root", "nodes"], raster: ["type", "drawableId", "bounds", "zIndex", "opacity", "pngData", "hoverPngData", "hitTestMap"] } as Record<string, string[]>; if (allowed[type]) ensureKeys(value, allowed[type], `drawable.${type}`); const common = { drawableId: checkedIdentifier(value.drawableId, "drawableId"), bounds: decodeRect(object(value.bounds, "bounds")), zIndex: integer(value.zIndex, "zIndex"), opacity: boundedNumber(value.opacity, "opacity", 0, 1) }; switch (type) { case "sprite": return { type, ...common, assetId: checkedIdentifier(value.assetId, "assetId"), sourceRect: decodeRect(object(value.sourceRect, "sourceRect")), frame: nonNegativeInteger(value.frame, "frame"), animationFrames: array(value.animationFrames, "animationFrames").map(item => decodeAnimationFrame(object(item, "animationFrame"))) }; case "shape": return { type, ...common, shape: shapeKind(value.shape), fill: optionalColor(value.fill, "fill"), stroke: optionalColor(value.stroke, "stroke"), points: array(value.points, "points").map(item => decodePoint(object(item, "point"))) }; case "htmlIsland": { const hasRoot = value.root !== undefined && value.root !== null; const hasNodes = value.nodes !== undefined && value.nodes !== null; if (hasRoot === hasNodes) throw new RealtimeDecodeError("invalid_payload", "HTML Island 必须且只能包含 root 或 nodes。"); return { type, ...common, root: hasRoot ? decodeHtml(object(value.root, "root")) : undefined, nodes: hasNodes ? array(value.nodes, "nodes").map(item => decodeNode(item)) : undefined }; } case "raster": { const pngData = string(value.pngData, "pngData"); validatePngBase64(pngData); if (value.hoverPngData !== undefined && value.hoverPngData !== null) validatePngBase64(string(value.hoverPngData, "hoverPngData")); return { type, ...common, pngData, hoverPngData: nullableString(value.hoverPngData, "hoverPngData"), hitTestMap: value.hitTestMap === null || value.hitTestMap === undefined ? value.hitTestMap : bool(value.hitTestMap, "hitTestMap") }; } default: throw new RealtimeDecodeError("unknown_drawable", "绘图类型不受支持。"); } }
function decodeAnimationFrame(value: JsonObject): SpriteAnimationFrame { ensureKeys(value, ["assetId", "sourceRect", "offset", "durationMilliseconds"], "animationFrame"); return { assetId: checkedIdentifier(value.assetId, "assetId"), sourceRect: decodeRect(object(value.sourceRect, "sourceRect")), offset: decodePoint(object(value.offset, "offset")), durationMilliseconds: positiveInteger(value.durationMilliseconds, "durationMilliseconds") }; }
function decodeHitRegion(value: JsonObject) { ensureKeys(value, ["regionId", "bounds", "inputValue", "enabled", "tooltip"], "hitRegion"); return { regionId: checkedIdentifier(value.regionId, "regionId"), bounds: decodeRect(object(value.bounds, "bounds")), inputValue: string(value.inputValue, "inputValue"), enabled: bool(value.enabled, "enabled"), tooltip: nullableString(value.tooltip, "tooltip") }; }
function decodeMedia(value: JsonObject): MediaChannel { ensureKeys(value, ["channel", "assetId", "playbackState", "loop", "volume", "revision", "startPolicy"], "media"); const playbackState = string(value.playbackState, "playbackState"); if (!["requested", "playing", "stopped"].includes(playbackState)) throw new RealtimeDecodeError("invalid_payload", "媒体状态无效。"); const startPolicy = string(value.startPolicy, "startPolicy"); if (!["immediate", "onUserGesture"].includes(startPolicy)) throw new RealtimeDecodeError("invalid_payload", "媒体启动策略无效。"); return { channel: checkedIdentifier(value.channel, "channel"), assetId: nullableIdentifier(value.assetId, "assetId"), playbackState: playbackState as MediaChannel["playbackState"], loop: bool(value.loop, "loop"), volume: boundedNumber(value.volume, "volume", 0, 1), revision: nonNegativeInteger(value.revision, "revision"), startPolicy: startPolicy as MediaChannel["startPolicy"] }; }
function decodePrompt(value: JsonObject): Prompt { ensureKeys(value, ["promptId", "inputType", "promptText", "defaultValue", "constraints", "timeoutBehavior", "timeoutAction", "allowedSources", "oneInput", "systemInput", "stopMessageSkip", "displayTime", "timeoutMessage", "openedAtUnixMilliseconds", "deadlineUnixMilliseconds", "timeoutMilliseconds"], "prompt"); const inputType = string(value.inputType, "inputType"); if (!["enterKey", "anyKey", "integer", "text", "anyValue", "integerButton", "textButton", "primitivePointerKey", "waitOnly"].includes(inputType)) throw new RealtimeDecodeError("invalid_payload", "输入类型无效。"); const sources = array(value.allowedSources, "allowedSources").map(item => string(item, "allowedSource")); if (sources.some(item => !["keyboard", "button", "pointer", "system"].includes(item))) throw new RealtimeDecodeError("invalid_payload", "输入来源无效。"); return { promptId: checkedIdentifier(value.promptId, "promptId"), inputType: inputType as Prompt["inputType"], promptText: nullableString(value.promptText, "promptText"), defaultValue: nullableString(value.defaultValue, "defaultValue"), constraints: decodeConstraints(object(value.constraints, "constraints")), timeoutBehavior: string(value.timeoutBehavior, "timeoutBehavior"), timeoutAction: string(value.timeoutAction, "timeoutAction"), allowedSources: sources as Prompt["allowedSources"], oneInput: bool(value.oneInput, "oneInput"), systemInput: bool(value.systemInput, "systemInput"), stopMessageSkip: bool(value.stopMessageSkip, "stopMessageSkip"), displayTime: bool(value.displayTime, "displayTime"), timeoutMessage: nullableString(value.timeoutMessage, "timeoutMessage"), openedAtUnixMilliseconds: finiteNumber(value.openedAtUnixMilliseconds, "openedAtUnixMilliseconds"), deadlineUnixMilliseconds: finiteNumber(value.deadlineUnixMilliseconds, "deadlineUnixMilliseconds"), timeoutMilliseconds: value.timeoutMilliseconds === null || value.timeoutMilliseconds === undefined ? value.timeoutMilliseconds : finiteNumber(value.timeoutMilliseconds, "timeoutMilliseconds") }; }
function decodeConstraints(value: JsonObject): InputConstraints { ensureKeys(value, ["type", "maxLength", "minimum", "maximum", "allowSign", "allowControlCharacters"], "constraints"); const type = string(value.type, "constraints.type"); if (!["text", "integer", "anyValue"].includes(type)) throw new RealtimeDecodeError("invalid_payload", "输入约束类型无效。"); return { type: type as InputConstraints["type"], maxLength: optionalInteger(value.maxLength, "maxLength"), minimum: optionalInteger(value.minimum, "minimum"), maximum: optionalInteger(value.maximum, "maximum"), allowSign: optionalBool(value.allowSign, "allowSign"), allowControlCharacters: optionalBool(value.allowControlCharacters, "allowControlCharacters") }; }
function decodeWindow(value: JsonObject): WindowMetadata { ensureKeys(value, ["title", "viewportWidth", "viewportHeight", "defaultForeground", "defaultBackground", "defaultFont"], "window"); const defaultFont = object(value.defaultFont, "defaultFont"); ensureKeys(defaultFont, ["family", "size", "lineHeight"], "defaultFont"); return { title: string(value.title, "title"), viewportWidth: positiveInteger(value.viewportWidth, "viewportWidth"), viewportHeight: positiveInteger(value.viewportHeight, "viewportHeight"), defaultForeground: optionalColor(value.defaultForeground, "defaultForeground"), defaultBackground: optionalColor(value.defaultBackground, "defaultBackground"), defaultFont: { family: string(defaultFont.family, "family"), size: positiveInteger(defaultFont.size, "size"), lineHeight: nonNegativeInteger(defaultFont.lineHeight, "lineHeight") } }; }
function decodeTruncation(value: JsonObject): Truncation { ensureKeys(value, ["wasTruncated", "droppedNodeCount", "droppedLineCount", "droppedTextLength"], "truncation"); return { wasTruncated: bool(value.wasTruncated, "wasTruncated"), droppedNodeCount: nonNegativeInteger(value.droppedNodeCount, "droppedNodeCount"), droppedLineCount: nonNegativeInteger(value.droppedLineCount, "droppedLineCount"), droppedTextLength: nonNegativeInteger(value.droppedTextLength, "droppedTextLength") }; }
function decodeTransaction(value: JsonObject) { ensureKeys(value, ["sequence", "operations"], "transaction"); return { sequence: positiveInteger(value.sequence, "sequence"), operations: array(value.operations, "operations").map(item => decodeOperation(object(item, "operation"))) }; }
function decodeOperation(value: JsonObject): RealtimeOperation {
  const type = string(value.type, "operation.type");
  const allowed = {
    appendNodes: ["type", "nodes"], clearConsole: ["type"], clearScrollback: ["type"], openPrompt: ["type", "prompt"], closePrompt: ["type", "promptId", "reason"],
    appendLine: ["type", "line"], appendInline: ["type", "lineId", "nodes"], replaceLine: ["type", "line"], deleteLines: ["type", "lineIds"], setWindowMetadata: ["type", "windowMetadata"],
    upsertBackground: ["type", "backgroundLayer"], removeBackground: ["type", "layerId"], clearBackgrounds: ["type"], upsertDrawable: ["type", "drawable"], removeDrawable: ["type", "drawableId"],
    clearSceneRange: ["type", "minimumZIndex", "maximumZIndex"], clearScene: ["type"], upsertHitRegion: ["type", "hitRegion"], removeHitRegion: ["type", "regionId"], clearHitRegions: ["type"],
    setMediaChannel: ["type", "mediaChannel"], stopMediaChannel: ["type", "channel"], stopAllMedia: ["type"],
  } as Record<string, string[]>;
  if (allowed[type]) ensureKeys(value, allowed[type], `operation.${type}`);
  switch (type) {
    case "appendNodes": return { type, nodes: array(value.nodes, "nodes").map(decodeNode) };
    case "clearConsole": case "clearScrollback": case "clearBackgrounds": case "clearScene": case "clearHitRegions": case "stopAllMedia": return { type };
    case "openPrompt": return { type, prompt: decodePrompt(object(value.prompt, "prompt")) };
    case "closePrompt": return { type, promptId: checkedIdentifier(value.promptId, "promptId"), reason: string(value.reason, "reason") };
    case "appendLine": return { type, line: decodeLine(object(value.line, "line")) };
    case "appendInline": return { type, lineId: checkedIdentifier(value.lineId, "lineId"), nodes: array(value.nodes, "nodes").map(decodeNode) };
    case "replaceLine": return { type, line: decodeLine(object(value.line, "line")) };
    case "deleteLines": return { type, lineIds: array(value.lineIds, "lineIds").map(item => checkedIdentifier(item, "lineId")) };
    case "setWindowMetadata": return { type, windowMetadata: decodeWindow(object(value.windowMetadata, "windowMetadata")) };
    case "upsertBackground": return { type, backgroundLayer: decodeBackground(object(value.backgroundLayer, "backgroundLayer")) };
    case "removeBackground": return { type, layerId: checkedIdentifier(value.layerId, "layerId") };
    case "upsertDrawable": return { type, drawable: decodeDrawable(object(value.drawable, "drawable")) };
    case "removeDrawable": return { type, drawableId: checkedIdentifier(value.drawableId, "drawableId") };
    case "clearSceneRange": return { type, minimumZIndex: integer(value.minimumZIndex, "minimumZIndex"), maximumZIndex: integer(value.maximumZIndex, "maximumZIndex") };
    case "upsertHitRegion": return { type, hitRegion: decodeHitRegion(object(value.hitRegion, "hitRegion")) };
    case "removeHitRegion": return { type, regionId: checkedIdentifier(value.regionId, "regionId") };
    case "setMediaChannel": return { type, mediaChannel: decodeMedia(object(value.mediaChannel, "mediaChannel")) };
    case "stopMediaChannel": return { type, channel: checkedIdentifier(value.channel, "channel") };
    default: throw new RealtimeDecodeError("unknown_operation", "实时操作类型不受支持。");
  }
}

function shapeKind(value: JsonValue) { const result = string(value, "shape"); if (!["rectangle", "ellipse", "line", "polygon", "space"].includes(result)) throw new RealtimeDecodeError("invalid_payload", "图形类型无效。"); return result as "rectangle" | "ellipse" | "line" | "polygon" | "space"; }
function valueError(name: string): never { throw new RealtimeDecodeError("invalid_payload", `${name} 字段无效。`); }
function object(value: JsonValue | undefined, name: string): JsonObject { if (!value || typeof value !== "object" || Array.isArray(value)) return valueError(name); return value as JsonObject; }
function array(value: JsonValue | undefined, name: string): JsonValue[] { if (!Array.isArray(value)) return valueError(name); return value; }
function string(value: JsonValue | undefined, name: string): string { if (typeof value !== "string") return valueError(name); return value; }
function nullableString(value: JsonValue | undefined, name: string): string | null | undefined { if (value === null || value === undefined) return value; return string(value, name); }
function bool(value: JsonValue | undefined, name: string): boolean { if (typeof value !== "boolean") return valueError(name); return value; }
function finiteNumber(value: JsonValue | undefined, name: string): number { if (typeof value !== "number" || !Number.isFinite(value)) return valueError(name); return value; }
function integer(value: JsonValue | undefined, name: string): number { const result = finiteNumber(value, name); if (!Number.isSafeInteger(result)) return valueError(name); return result; }
function positiveInteger(value: JsonValue | undefined, name: string): number { const result = integer(value, name); if (result <= 0) return valueError(name); return result; }
function nonNegativeInteger(value: JsonValue | undefined, name: string): number { const result = integer(value, name); if (result < 0) return valueError(name); return result; }
function boundedInteger(value: JsonValue | undefined, name: string, minimum: number, maximum: number): number { const result = integer(value, name); if (result < minimum || result > maximum) return valueError(name); return result; }
function optionalBoundedInteger(value: JsonValue | undefined, name: string, minimum: number, maximum: number): number | undefined { if (value === undefined) return undefined; return boundedInteger(value, name, minimum, maximum); }
function optionalInteger(value: JsonValue | undefined, name: string): number | null | undefined { return value === null || value === undefined ? value : integer(value, name); }
function optionalBool(value: JsonValue | undefined, name: string): boolean | null | undefined { return value === null || value === undefined ? value : bool(value, name); }
function boundedNumber(value: JsonValue | undefined, name: string, minimum: number, maximum: number): number { const result = finiteNumber(value, name); if (result < minimum || result > maximum) return valueError(name); return result; }
function byte(value: JsonValue | undefined, name: string): number { const result = nonNegativeInteger(value, name); if (result > 255) return valueError(name); return result; }
function checkedIdentifier(value: JsonValue | undefined, name: string): string { const result = string(value, name); checkIdentifier(result, name); return result; }
function nullableIdentifier(value: JsonValue | undefined, name: string): string | null | undefined { return value === null || value === undefined ? value : checkedIdentifier(value, name); }
function checkIdentifier(value: JsonValue | undefined, name: string): void { const result = string(value, name); if (!identifier.test(result)) throw new RealtimeDecodeError("invalid_identifier", `${name} 不是有效标识符。`); }
function checkedDigest(value: JsonValue | undefined): string { const result = string(value, "capabilityDigest"); if (!digest.test(result)) throw new RealtimeDecodeError("invalid_payload", "capabilityDigest 无效。"); return result; }

function ensureKeys(value: JsonObject, allowed: readonly string[], name: string): void {
  const permitted = new Set(allowed);
  for (const key of Object.keys(value)) if (!permitted.has(key)) throw new RealtimeDecodeError("unknown_property", `${name} 包含不受支持的字段：${key}`);
}

function validatePngBase64(value: string): void {
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(value) || value.length % 4 !== 0) throw new RealtimeDecodeError("invalid_raster", "Raster 不是有效 base64。");
  let bytes: string;
  try { bytes = atob(value); } catch { throw new RealtimeDecodeError("invalid_raster", "Raster 不是有效 base64。"); }
  let signature = "";
  for (let index = 0; index < Math.min(bytes.length, 8); index++) signature += bytes.charCodeAt(index).toString(16).padStart(2, "0");
  if (signature !== pngSignature) throw new RealtimeDecodeError("invalid_raster", "Raster 不是 PNG。");
}

/** Detect duplicate object keys before JSON.parse would silently collapse them. */
function scanJson(text: string): void {
  let index = 0;
  const whitespace = () => { while (/\s/.test(text[index] ?? "")) index++; };
  const parseString = () => { if (text[index++] !== '"') throw new RealtimeDecodeError("invalid_json", "字符串无效。"); while (index < text.length) { const char = text[index++]; if (char === '"') return; if (char === "\\") { if (index >= text.length) throw new RealtimeDecodeError("invalid_json", "字符串转义无效。"); const escaped = text[index++]; if (escaped === "u") index += 4; } else if (char < " ") throw new RealtimeDecodeError("invalid_json", "字符串包含控制字符。"); } throw new RealtimeDecodeError("invalid_json", "字符串未闭合。"); };
  const parseValue = (depth: number) => { if (depth > MAX_REALTIME_JSON_DEPTH) throw new RealtimeDecodeError("json_too_deep", "实时 JSON 层级过深。"); whitespace(); const char = text[index]; if (char === "{") { index++; const names = new Set<string>(); whitespace(); if (text[index] === "}") { index++; return; } while (true) { whitespace(); const start = index; parseString(); let key: string; try { key = JSON.parse(text.slice(start, index)) as string; } catch { throw new RealtimeDecodeError("invalid_json", "对象键无效。"); } if (!names.add(key)) throw new RealtimeDecodeError("duplicate_property", "实时 JSON 包含重复字段。"); whitespace(); if (text[index++] !== ":") throw new RealtimeDecodeError("invalid_json", "对象缺少冒号。"); parseValue(depth + 1); whitespace(); if (text[index] === "}") { index++; return; } if (text[index++] !== ",") throw new RealtimeDecodeError("invalid_json", "对象分隔符无效。"); } } else if (char === "[") { index++; whitespace(); if (text[index] === "]") { index++; return; } while (true) { parseValue(depth + 1); whitespace(); if (text[index] === "]") { index++; return; } if (text[index++] !== ",") throw new RealtimeDecodeError("invalid_json", "数组分隔符无效。"); } } else if (char === '"') parseString(); else { const start = index; while (index < text.length && !/[\s,\]}]/.test(text[index])) index++; const token = text.slice(start, index); if (!/^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)$/.test(token)) throw new RealtimeDecodeError("invalid_json", "JSON 值无效。"); if (!/^(?:true|false|null)$/.test(token) && (token.includes("e") || token.includes("E") || token.includes("."))) { const numberValue = Number(token); if (!Number.isFinite(numberValue)) throw new RealtimeDecodeError("invalid_number", "JSON 数字不是有限数。"); } } };
  parseValue(0); whitespace(); if (index !== text.length) throw new RealtimeDecodeError("invalid_json", "JSON 后存在多余内容。");
}

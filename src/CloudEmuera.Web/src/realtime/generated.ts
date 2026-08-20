/* GENERATED CONTRACT SNAPSHOT — source: realtime-v2.schema.json. */
export const REALTIME_SCHEMA_ID = "https://cloudemuera.invalid/schema/realtime-v2.schema.json" as const;
export const REALTIME_PROTOCOL_VERSION = 2 as const;
export const REALTIME_PAYLOAD_SCHEMA_VERSION = "p1-11" as const;
export const REALTIME_MESSAGE_TYPES = ["client.hello","server.hello","connection.ping","connection.pong","session.resume","session.resume.result","session.unsubscribe","session.snapshot","display.batch","resync.required","session.stream.ended","session.input","session.input.result","protocol.error"] as const;

export type EmptyPayload = Record<never, never>;

export interface ClientHelloPayload {
  supportedProtocolVersions: number[];
  capabilityDigest: string;
  supportedCapabilities: string[];
}

export interface ServerHelloPayload {
  protocolVersion: 2;
  payloadSchemaVersion: "p1-11";
  connectionId: string;
  serverNowUnixMilliseconds: number;
  heartbeatIntervalMilliseconds: number;
  heartbeatTimeoutMilliseconds: number;
  maxSubscriptionsPerConnection: number;
  maxPendingInputsPerConnection: number;
  serverMessageMaxBytes: number;
  capabilityDigest: string;
}

export interface PingPayload {
  nonce: string;
  serverNowUnixMilliseconds: number;
}

export interface PongPayload {
  nonce: string;
}

export interface ResumePayload {
  capabilityDigest: string;
  lastEpoch?: number;
}

export interface ResumeResultPayload {
  status: "ACCEPTED" | "CAPABILITY_MISMATCH" | "SESSION_NOT_FOUND" | "SESSION_NOT_RUNNING" | "SNAPSHOT_NOT_READY" | "SUBSCRIPTION_LIMIT_EXCEEDED";
  workerEpoch?: number | null;
  reasonCode?: string | null;
}

export interface RealtimeSnapshotPayload {
  workerEpoch: number;
  snapshotSequence: number;
  consoleState: ConsoleState;
}

export interface RealtimeTransactionBatchPayload {
  workerEpoch: number;
  firstSequence: number;
  lastSequence: number;
  transactions: RealtimeTransaction[];
}

export interface ResyncRequiredPayload {
  workerEpoch: number;
  observedSequence: number;
  reason: string;
}

export interface StreamEndedPayload {
  reasonCode: string;
}

export interface InputPayload {
  clientMessageId: string;
  source: "KEYBOARD" | "BUTTON" | "POINTER";
  value: string;
  pointer?: InputPointer | null;
  key?: InputKey | null;
}

export interface InputPointer {
  x: number;
  y: number;
  button: number;
  pressed: boolean;
}

export interface InputKey {
  keyCode: number;
  control: boolean;
  alt: boolean;
  shift: boolean;
}

export interface InputResultPayload {
  clientMessageId: string;
  status: "ACCEPTED" | "DUPLICATE" | "CONFLICT" | "NO_ACTIVE_PROMPT" | "INVALID_FORMAT" | "INVALID_COMMAND" | "SESSION_NOT_ACCEPTING_INPUT" | "STALE_EPOCH" | "SESSION_NOT_RUNNING" | "INPUT_BACKPRESSURE" | "WORKER_UNAVAILABLE" | "FORBIDDEN";
  reasonCode: string;
  resolvedPromptId: string | null;
  normalizedValue?: string | null;
}

export interface ProtocolErrorPayload {
  code: string;
  message: string;
}

export interface RealtimeColor {
  red: number;
  green: number;
  blue: number;
  alpha: number;
}

export interface RealtimePoint {
  x: number;
  y: number;
}

export interface RealtimeRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface RealtimeInsets {
  top: number;
  right: number;
  bottom: number;
  left: number;
}

export interface RealtimeBoxModel {
  margin: RealtimeInsets;
  padding: RealtimeInsets;
  border: RealtimeInsets;
  radius: RealtimeInsets;
  borderColors: Array<RealtimeColor | null>;
}

export interface RealtimeTextStyle {
  decorations: string[];
  fontFamily: string;
  fontSize: number;
  lineHeight: number;
  foreground?: RealtimeColor | null;
  background?: RealtimeColor | null;
  buttonColor?: RealtimeColor | null;
}

export interface SpriteAnimationFrame {
  assetId: string;
  sourceRect: RealtimeRect;
  offset: RealtimePoint;
  durationMilliseconds: number;
}

export interface BackgroundLayer {
  layerId: string;
  assetId: string;
  mode: "stretch" | "contain" | "cover" | "center" | "repeat";
  opacity: number;
  depth: number;
}

export interface HitRegion {
  regionId: string;
  bounds: RealtimeRect;
  inputValue: string;
  enabled: boolean;
  tooltip?: string | null;
}

export interface CanvasScene {
  drawables: RealtimeDrawable[];
  hitRegions: HitRegion[];
}

export interface MediaChannel {
  channel: string;
  assetId?: string | null;
  playbackState: "requested" | "playing" | "stopped";
  loop: boolean;
  volume: number;
  revision: number;
  startPolicy: "immediate" | "onUserGesture";
}

export interface MediaState {
  channels: MediaChannel[];
}

export interface InputConstraints {
  type: "text" | "integer" | "anyValue";
  maxLength?: number | null;
  minimum?: number | null;
  maximum?: number | null;
  allowSign?: boolean | null;
  allowControlCharacters?: boolean | null;
}

export interface Prompt {
  promptId: string;
  inputType: "enterKey" | "anyKey" | "integer" | "text" | "anyValue" | "integerButton" | "textButton" | "primitivePointerKey" | "waitOnly";
  promptText?: string | null;
  defaultValue?: string | null;
  constraints: InputConstraints;
  timeoutBehavior: string;
  timeoutAction: string;
  allowedSources: Array<"keyboard" | "button" | "pointer" | "system">;
  oneInput: boolean;
  systemInput: boolean;
  stopMessageSkip: boolean;
  displayTime: boolean;
  timeoutMessage?: string | null;
  openedAtUnixMilliseconds: number;
  deadlineUnixMilliseconds: number;
  timeoutMilliseconds?: number | null;
}

export interface WindowMetadata {
  title: string;
  viewportWidth: number;
  viewportHeight: number;
  defaultForeground?: RealtimeColor | null;
  defaultBackground?: RealtimeColor | null;
  defaultFont: {
  family: string;
  size: number;
  lineHeight: number;
};
}

export interface Truncation {
  wasTruncated: boolean;
  droppedNodeCount: number;
  droppedLineCount: number;
  droppedTextLength: number;
}

export interface RealtimeLine {
  lineId: string;
  nodes: RealtimeNode[];
  alignment: "left" | "center" | "right";
  temporary: boolean;
  noWrap?: boolean;
}

export interface ConsoleState {
  scrollback: RealtimeLine[];
  backgroundLayers: BackgroundLayer[];
  canvasScene: CanvasScene;
  mediaState: MediaState;
  currentPrompt?: Prompt | null;
  windowMetadata: WindowMetadata;
  truncation: Truncation;
}

export interface RealtimeTransaction {
  sequence: number;
  operations: RealtimeOperation[];
}

export interface HtmlTextNode {
  type: "text";
  text: string;
}

export interface HtmlBreakNode {
  type: "break";
}

export interface HtmlElementNode {
  type: "element";
  tag: "span" | "div" | "p" | "b" | "strong" | "i" | "em" | "u" | "s" | "strike" | "img";
  children: RealtimeHtmlNode[];
  style?: RealtimeTextStyle | null;
  assetId?: string | null;
  altText?: string | null;
}

export type RealtimeHtmlNode = HtmlTextNode | HtmlBreakNode | HtmlElementNode;

export interface TextNode {
  type: "text";
  text: string;
  style: RealtimeTextStyle;
}

export interface LineBreakNode {
  type: "lineBreak";
}

export interface ButtonNode {
  type: "button";
  children: RealtimeNode[];
  value: string;
  tooltip?: string | null;
  enabled: boolean;
  generation: number;
  positionX?: number;
}

export interface ImageNode {
  type: "image";
  assetId: string;
  sourceRect?: RealtimeRect | null;
  destination?: RealtimeRect | null;
  altText?: string | null;
  decorative: boolean;
  zIndex: number;
}

export interface SpriteNode {
  type: "sprite";
  assetId: string;
  sourceRect: RealtimeRect;
  destination: RealtimeRect;
  frame: number;
  zIndex: number;
  opacity: number;
  altText?: string | null;
  hoverAssetId?: string | null;
  hoverSourceRect?: RealtimeRect | null;
  mappingAssetId?: string | null;
  mappingSourceRect?: RealtimeRect | null;
  animationFrames: SpriteAnimationFrame[];
}

export interface ShapeNode {
  type: "shape";
  shape: "rectangle" | "ellipse" | "line" | "polygon" | "space";
  bounds: RealtimeRect;
  fill?: RealtimeColor | null;
  stroke?: RealtimeColor | null;
  zIndex: number;
  points: RealtimePoint[];
  buttonColor?: RealtimeColor | null;
}

export interface HtmlIslandNode {
  type: "htmlIsland";
  root?: RealtimeHtmlNode;
  nodes?: RealtimeNode[];
  layout?: RealtimeRect | null;
}

export interface DivNode {
  type: "div";
  children: RealtimeNode[];
  bounds: RealtimeRect;
  zIndex: number;
  background?: RealtimeColor | null;
  isRelative: boolean;
  box?: RealtimeBoxModel | null;
}

export type RealtimeNode = TextNode | LineBreakNode | ButtonNode | ImageNode | SpriteNode | ShapeNode | HtmlIslandNode | DivNode;

export interface SpriteDrawable {
  type: "sprite";
  drawableId: string;
  bounds: RealtimeRect;
  zIndex: number;
  opacity: number;
  assetId: string;
  sourceRect: RealtimeRect;
  frame: number;
  animationFrames: SpriteAnimationFrame[];
}

export interface ShapeDrawable {
  type: "shape";
  drawableId: string;
  bounds: RealtimeRect;
  zIndex: number;
  opacity: number;
  shape: "rectangle" | "ellipse" | "line" | "polygon" | "space";
  fill?: RealtimeColor | null;
  stroke?: RealtimeColor | null;
  points: RealtimePoint[];
}

export interface HtmlIslandDrawable {
  type: "htmlIsland";
  drawableId: string;
  bounds: RealtimeRect;
  zIndex: number;
  opacity: number;
  root?: RealtimeHtmlNode;
  nodes?: RealtimeNode[];
}

export interface RasterDrawable {
  type: "raster";
  drawableId: string;
  bounds: RealtimeRect;
  zIndex: number;
  opacity: number;
  pngData: string;
  hoverPngData?: string | null;
  hitTestMap?: boolean | null;
}

export type RealtimeDrawable = SpriteDrawable | ShapeDrawable | HtmlIslandDrawable | RasterDrawable;

export interface AppendNodesOperation {
  type: "appendNodes";
  nodes: RealtimeNode[];
}

export interface ClearOperation {
  type: "clearConsole" | "clearScrollback" | "clearBackgrounds" | "clearScene" | "clearHitRegions" | "stopAllMedia";
}

export interface OpenPromptOperation {
  type: "openPrompt";
  prompt: Prompt;
}

export interface ClosePromptOperation {
  type: "closePrompt";
  promptId: string;
  reason: string;
}

export interface LineOperation {
  type: "appendLine" | "replaceLine";
  line: RealtimeLine;
}

export interface AppendInlineOperation {
  type: "appendInline";
  lineId: string;
  nodes: RealtimeNode[];
}

export interface DeleteLinesOperation {
  type: "deleteLines";
  lineIds: string[];
}

export interface SetWindowMetadataOperation {
  type: "setWindowMetadata";
  windowMetadata: WindowMetadata;
}

export interface UpsertBackgroundOperation {
  type: "upsertBackground";
  backgroundLayer: BackgroundLayer;
}

export interface RemoveBackgroundOperation {
  type: "removeBackground";
  layerId: string;
}

export interface UpsertDrawableOperation {
  type: "upsertDrawable";
  drawable: RealtimeDrawable;
}

export interface RemoveDrawableOperation {
  type: "removeDrawable";
  drawableId: string;
}

export interface ClearSceneRangeOperation {
  type: "clearSceneRange";
  minimumZIndex: number;
  maximumZIndex: number;
}

export interface UpsertHitRegionOperation {
  type: "upsertHitRegion";
  hitRegion: HitRegion;
}

export interface RemoveHitRegionOperation {
  type: "removeHitRegion";
  regionId: string;
}

export interface SetMediaChannelOperation {
  type: "setMediaChannel";
  mediaChannel: MediaChannel;
}

export interface StopMediaChannelOperation {
  type: "stopMediaChannel";
  channel: string;
}

export type RealtimeOperation = AppendNodesOperation | ClearOperation | OpenPromptOperation | ClosePromptOperation | LineOperation | AppendInlineOperation | DeleteLinesOperation | SetWindowMetadataOperation | UpsertBackgroundOperation | RemoveBackgroundOperation | UpsertDrawableOperation | RemoveDrawableOperation | ClearSceneRangeOperation | UpsertHitRegionOperation | RemoveHitRegionOperation | SetMediaChannelOperation | StopMediaChannelOperation;

export type InputSource = "KEYBOARD" | "BUTTON" | "POINTER";
export type ResumeStatus = "ACCEPTED" | "CAPABILITY_MISMATCH" | "SESSION_NOT_FOUND" | "SESSION_NOT_RUNNING" | "SNAPSHOT_NOT_READY" | "SUBSCRIPTION_LIMIT_EXCEEDED";
export type InputResultStatus = "ACCEPTED" | "DUPLICATE" | "CONFLICT" | "NO_ACTIVE_PROMPT" | "INVALID_FORMAT" | "INVALID_COMMAND" | "SESSION_NOT_ACCEPTING_INPUT" | "STALE_EPOCH" | "SESSION_NOT_RUNNING" | "INPUT_BACKPRESSURE" | "WORKER_UNAVAILABLE" | "FORBIDDEN";
export type ShapeKind = "rectangle" | "ellipse" | "line" | "polygon" | "space";
export type InputType = "enterKey" | "anyKey" | "integer" | "text" | "anyValue" | "integerButton" | "textButton" | "primitivePointerKey" | "waitOnly";

export interface RealtimeEnvelope<TType extends string, TPayload> {
  protocolVersion: 2;
  type: TType;
  messageId: string;
  correlationId?: string;
  sessionId?: string;
  workerEpoch?: number;
  sequence?: number;
  payload: TPayload;
}

export type ClientHelloMessage = RealtimeEnvelope<"client.hello", ClientHelloPayload>;
export type PongMessage = RealtimeEnvelope<"connection.pong", PongPayload>;
export type ResumeMessage = RealtimeEnvelope<"session.resume", ResumePayload>;
export type UnsubscribeMessage = RealtimeEnvelope<"session.unsubscribe", EmptyPayload>;
export type InputMessage = RealtimeEnvelope<"session.input", InputPayload>;

export type RealtimeClientType = "client.hello" | "connection.pong" | "session.resume" | "session.unsubscribe" | "session.input";
export type RealtimeServerType = "server.hello" | "connection.ping" | "session.resume.result" | "session.snapshot" | "display.batch" | "resync.required" | "session.stream.ended" | "session.input.result" | "protocol.error";

export type RealtimeClientMessage = ClientHelloMessage | PongMessage | ResumeMessage | UnsubscribeMessage | InputMessage;
export type RealtimeServerMessage = RealtimeEnvelope<"server.hello", ServerHelloPayload> | RealtimeEnvelope<"connection.ping", PingPayload> | RealtimeEnvelope<"session.resume.result", ResumeResultPayload> | RealtimeEnvelope<"session.snapshot", RealtimeSnapshotPayload> | RealtimeEnvelope<"display.batch", RealtimeTransactionBatchPayload> | RealtimeEnvelope<"resync.required", ResyncRequiredPayload> | RealtimeEnvelope<"session.stream.ended", StreamEndedPayload> | RealtimeEnvelope<"session.input.result", InputResultPayload> | RealtimeEnvelope<"protocol.error", ProtocolErrorPayload>;

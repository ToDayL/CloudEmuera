/** CloudEmuera realtime v1 wire contracts consumed by P1-11. */
export const REALTIME_SUBPROTOCOL = "cloudemuera.realtime.v1" as const;
export const REALTIME_PROTOCOL_VERSION = 1 as const;
export const REALTIME_PAYLOAD_SCHEMA_VERSION = "p1-09" as const;

export type RealtimeClientType =
  | "client.hello"
  | "connection.pong"
  | "session.resume"
  | "session.unsubscribe"
  | "session.input";

export type RealtimeServerType =
  | "server.hello"
  | "connection.ping"
  | "session.resume.result"
  | "session.snapshot"
  | "display.batch"
  | "resync.required"
  | "session.stream.ended"
  | "session.input.result"
  | "protocol.error";

export interface RealtimeEnvelope<TType extends string, TPayload> {
  protocolVersion: 1;
  type: TType;
  messageId: string;
  correlationId?: string;
  sessionId?: string;
  workerEpoch?: number;
  sequence?: number;
  payload: TPayload;
}

export interface ClientHelloPayload {
  supportedProtocolVersions: number[];
  capabilityDigest: string;
  supportedCapabilities: string[];
}

export interface ResumePayload {
  capabilityDigest: string;
  lastEpoch?: number;
}

export interface PongPayload {
  nonce: string;
}

export type EmptyPayload = Record<string, never>;

export interface InputPayload {
  promptId: string;
  clientMessageId: string;
  source: "KEYBOARD" | "BUTTON" | "POINTER";
  value: string;
  pointer?: { x: number; y: number; button: number; pressed: boolean } | null;
  key?: { keyCode: number; control: boolean; alt: boolean; shift: boolean } | null;
}

export type ClientHelloMessage = RealtimeEnvelope<"client.hello", ClientHelloPayload>;
export type PongMessage = RealtimeEnvelope<"connection.pong", PongPayload>;
export type ResumeMessage = RealtimeEnvelope<"session.resume", ResumePayload>;
export type UnsubscribeMessage = RealtimeEnvelope<"session.unsubscribe", EmptyPayload>;
export type InputMessage = RealtimeEnvelope<"session.input", InputPayload>;

export interface ServerHelloPayload {
  protocolVersion: 1;
  payloadSchemaVersion: string;
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

export interface ResumeResultPayload {
  status:
    | "ACCEPTED"
    | "CAPABILITY_MISMATCH"
    | "SESSION_NOT_FOUND"
    | "SESSION_NOT_RUNNING"
    | "SNAPSHOT_NOT_READY"
    | "SUBSCRIPTION_LIMIT_EXCEEDED";
  workerEpoch?: number | null;
  reasonCode?: string | null;
}

export type StructuredSnapshotPayload = Record<string, unknown>;
export type StructuredBatchPayload = Record<string, unknown>;

export interface ResyncRequiredPayload {
  workerEpoch: number;
  observedSequence: number;
  reason: string;
}

export interface StreamEndedPayload {
  reasonCode: string;
}

export interface InputResultPayload {
  promptId: string;
  clientMessageId: string;
  status:
    | "ACCEPTED"
    | "DUPLICATE"
    | "CONFLICT"
    | "STALE_PROMPT"
    | "NO_ACTIVE_PROMPT"
    | "INVALID_FORMAT"
    | "INVALID_COMMAND"
    | "CANCELLED"
    | "TIMED_OUT"
    | "SESSION_NOT_ACCEPTING_INPUT"
    | "STALE_EPOCH"
    | "SESSION_NOT_RUNNING"
    | "INPUT_BACKPRESSURE"
    | "WORKER_UNAVAILABLE"
    | "FORBIDDEN";
  reasonCode: string;
  normalizedValue?: string | null;
}

export interface ProtocolErrorPayload {
  code: string;
  message: string;
}

export type ServerHelloMessage = RealtimeEnvelope<"server.hello", ServerHelloPayload>;
export type PingMessage = RealtimeEnvelope<"connection.ping", PingPayload>;
export type ResumeResultMessage = RealtimeEnvelope<"session.resume.result", ResumeResultPayload>;
export type SnapshotMessage = RealtimeEnvelope<"session.snapshot", StructuredSnapshotPayload>;
export type BatchMessage = RealtimeEnvelope<"display.batch", StructuredBatchPayload>;
export type ResyncRequiredMessage = RealtimeEnvelope<"resync.required", ResyncRequiredPayload>;
export type StreamEndedMessage = RealtimeEnvelope<"session.stream.ended", StreamEndedPayload>;
export type InputResultMessage = RealtimeEnvelope<"session.input.result", InputResultPayload>;
export type ProtocolErrorMessage = RealtimeEnvelope<"protocol.error", ProtocolErrorPayload>;

export type RealtimeClientMessage = ClientHelloMessage | PongMessage | ResumeMessage | UnsubscribeMessage | InputMessage;
export type RealtimeServerMessage =
  | ServerHelloMessage
  | PingMessage
  | ResumeResultMessage
  | SnapshotMessage
  | BatchMessage
  | ResyncRequiredMessage
  | StreamEndedMessage
  | InputResultMessage
  | ProtocolErrorMessage;

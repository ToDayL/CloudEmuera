import { CAPABILITY_DIGEST, SUPPORTED_CAPABILITIES } from "./capabilities";
import { decodeRealtimeMessage } from "./codec";
import { InputPayload, REALTIME_SUBPROTOCOL, RealtimeServerMessage } from "./protocol";
import { beginResume, createPendingInput, createSessionStoreState, handleServerMessage, markInputUnknown, SessionStoreState } from "./sessionStore";

export type ConnectionPhase = "disconnected" | "connecting" | "hello_pending" | "ready" | "backing_off" | "auth_required" | "incompatible" | "disposed";
export type ConnectionListener = (phase: ConnectionPhase, detail?: string) => void;
export type NetworkStatusListener = (online: boolean) => void;
export type SessionListener = (state: SessionStoreState) => void;

interface Subscription {
  sessionId: string;
  state: SessionStoreState;
  listeners: Set<SessionListener>;
  resumeTimer?: number;
  retryCount: number;
}

/** One socket per tab. The manager never closes a Worker when the socket goes away. */
export class RealtimeConnectionManager {
  private socket: WebSocket | null = null;
  private phase: ConnectionPhase = "disconnected";
  private readonly listeners = new Set<ConnectionListener>();
  private readonly networkListeners = new Set<NetworkStatusListener>();
  private readonly subscriptions = new Map<string, Subscription>();
  private reconnectTimer: number | null = null;
  private helloTimer: number | null = null;
  private heartbeatTimer: number | null = null;
  private reconnectAttempt = 0;
  private disposed = false;
  private serverHello: { serverNowUnixMilliseconds: number; heartbeatIntervalMilliseconds: number; heartbeatTimeoutMilliseconds: number; capabilityDigest: string } | null = null;
  private serverTimeOffsetMilliseconds = 0;
  private networkOnline = typeof navigator === "undefined" || navigator.onLine !== false;
  private readonly handleOnlineEvent = () => this.setNetworkOnline(true);
  private readonly handleOfflineEvent = () => this.setNetworkOnline(false);

  constructor() {
    if (typeof window !== "undefined") {
      window.addEventListener("online", this.handleOnlineEvent);
      window.addEventListener("offline", this.handleOfflineEvent);
    }
  }

  get status(): ConnectionPhase { return this.phase; }
  get serverTimeOffset(): number { return this.serverTimeOffsetMilliseconds; }
  get isNetworkOnline(): boolean { return this.networkOnline; }

  onStatus(listener: ConnectionListener): () => void { this.listeners.add(listener); return () => this.listeners.delete(listener); }
  onNetworkStatus(listener: NetworkStatusListener): () => void { this.networkListeners.add(listener); listener(this.networkOnline); return () => this.networkListeners.delete(listener); }

  subscribe(sessionId: string, listener: SessionListener): () => void {
    let subscription = this.subscriptions.get(sessionId);
    if (!subscription) {
      subscription = { sessionId, state: createSessionStoreState(sessionId), listeners: new Set(), retryCount: 0 };
      this.subscriptions.set(sessionId, subscription);
    }
    subscription.listeners.add(listener);
    listener(subscription.state);
    this.ensureConnected();
    if (this.phase === "ready") this.resume(subscription);
    return () => {
      const current = this.subscriptions.get(sessionId);
      if (!current) return;
      current.listeners.delete(listener);
      if (current.listeners.size === 0) {
        this.send("session.unsubscribe", {}, sessionId);
        if (current.resumeTimer !== undefined) window.clearTimeout(current.resumeTimer);
        this.subscriptions.delete(sessionId);
      }
      if (this.subscriptions.size === 0 && this.socket) {
        this.socket.close(1000, "no_subscriptions");
      }
    };
  }

  getSessionState(sessionId: string): SessionStoreState | undefined { return this.subscriptions.get(sessionId)?.state; }

  sendInput(sessionId: string, input: Omit<InputPayload, "clientMessageId"> & { clientMessageId?: string }): string | null {
    const subscription = this.subscriptions.get(sessionId);
    const epoch = subscription?.state.workerEpoch;
    if (!subscription || epoch === null || epoch === undefined || this.phase !== "ready") return null;
    const clientMessageId = input.clientMessageId ?? newClientMessageId();
    const payload: InputPayload = { ...input, clientMessageId };
    subscription.state = createPendingInput(subscription.state, { promptId: payload.promptId, workerEpoch: epoch, clientMessageId, value: payload.value, source: payload.source, pointer: payload.pointer ?? null, key: payload.key ?? null });
    notify(subscription);
    this.send("session.input", payload, sessionId, epoch);
    return clientMessageId;
  }

  retryUnknownInput(sessionId: string): boolean {
    const subscription = this.subscriptions.get(sessionId);
    const pending = subscription?.state.pendingInput;
    if (!subscription || !pending || pending.status !== "unknown" || this.phase !== "ready") return false;
    this.send("session.input", { promptId: pending.promptId, clientMessageId: pending.clientMessageId, source: pending.source, value: pending.value, pointer: pending.pointer ?? null, key: pending.key ?? null }, sessionId, pending.workerEpoch);
    subscription.state = { ...subscription.state, pendingInput: { ...pending, status: "pending" } };
    notify(subscription);
    return true;
  }

  connect(): void {
    if (this.disposed || this.socket) return;
    if (!this.networkOnline) { this.setPhase("disconnected"); return; }
    if (typeof WebSocket === "undefined") { this.setPhase("disconnected", "浏览器不支持 WebSocket。"); return; }
    this.setPhase("connecting");
    const url = realtimeUrl();
    try { this.socket = new WebSocket(url, REALTIME_SUBPROTOCOL); }
    catch { this.socket = null; this.scheduleReconnect(); return; }
    const socket = this.socket;
    this.helloTimer = window.setTimeout(() => {
      if (this.socket === socket && (this.phase === "connecting" || this.phase === "hello_pending")) socket.close(1002, "hello_timeout");
    }, 8_000);
    socket.binaryType = "arraybuffer";
    socket.addEventListener("open", () => {
      if (this.socket !== socket) return;
      this.setPhase("hello_pending");
      this.send("client.hello", { supportedProtocolVersions: [1], capabilityDigest: CAPABILITY_DIGEST, supportedCapabilities: [...SUPPORTED_CAPABILITIES] });
    });
    socket.addEventListener("message", event => { if (this.socket === socket) this.handleMessage(event.data); });
    socket.addEventListener("error", () => { /* close is the reconnect boundary */ });
    socket.addEventListener("close", event => this.handleClose(socket, event.code, event.reason));
  }

  dispose(): void {
    this.disposed = true;
    if (this.reconnectTimer !== null) window.clearTimeout(this.reconnectTimer);
    if (this.helloTimer !== null) window.clearTimeout(this.helloTimer);
    if (this.heartbeatTimer !== null) window.clearTimeout(this.heartbeatTimer);
    for (const subscription of this.subscriptions.values()) {
      if (subscription.resumeTimer !== undefined) window.clearTimeout(subscription.resumeTimer);
    }
    this.subscriptions.clear();
    this.socket?.close(1000, "disposed");
    this.socket = null;
    if (typeof window !== "undefined") {
      window.removeEventListener("online", this.handleOnlineEvent);
      window.removeEventListener("offline", this.handleOfflineEvent);
    }
    this.setPhase("disposed");
  }

  private ensureConnected(): void {
    // Closing a browser WebSocket is asynchronous. A page that leaves and
    // immediately re-enters a Session must not attach to that old socket.
    if (this.socket && (this.socket.readyState === 2 || this.socket.readyState === 3)) this.socket = null;
    if (this.networkOnline && !this.socket && this.phase !== "auth_required" && this.phase !== "incompatible" && !this.disposed) this.connect();
  }

  private handleMessage(raw: unknown): void {
    let message: RealtimeServerMessage;
    try { if (typeof raw !== "string" && !(raw instanceof ArrayBuffer)) throw new Error("binary"); message = decodeRealtimeMessage(raw); }
    catch (error) { this.setPhase("incompatible", error instanceof Error ? error.message : "实时消息无法解析。"); this.socket?.close(1002, "invalid_message"); return; }
    if (message.type === "server.hello") {
      if (message.payload.capabilityDigest !== CAPABILITY_DIGEST) { this.setPhase("incompatible", "客户端与服务端能力版本不一致。"); this.socket?.close(1002, "capability_mismatch"); return; }
      this.serverHello = message.payload;
      this.serverTimeOffsetMilliseconds = message.payload.serverNowUnixMilliseconds - Date.now();
      this.reconnectAttempt = 0;
      if (this.helloTimer !== null) { window.clearTimeout(this.helloTimer); this.helloTimer = null; }
      this.armHeartbeat();
      this.setPhase("ready");
      for (const subscription of this.subscriptions.values()) this.resume(subscription);
      return;
    }
    if (message.type === "connection.ping") {
      this.serverTimeOffsetMilliseconds = smoothOffset(message.payload.serverNowUnixMilliseconds, this.serverTimeOffsetMilliseconds);
      this.armHeartbeat();
      this.send("connection.pong", { nonce: message.payload.nonce });
      return;
    }
    if (message.type === "protocol.error" && ["AUTHENTICATION_EXPIRED", "PASSWORD_CHANGE_REQUIRED"].includes(message.payload.code)) {
      this.setPhase("auth_required", message.payload.code); return;
    }
    if (!message.sessionId) return;
    const subscription = this.subscriptions.get(message.sessionId);
    if (!subscription) return;
    // `unsubscribed` is an acknowledgement for an explicit subscription
    // disposal. It is not a Worker lifecycle event. In particular, never let
    // it mutate the console store or turn a pending input into "unknown".
    if (message.type === "session.stream.ended" && message.payload.reasonCode === "unsubscribed") return;
    if (message.type === "session.resume.result") {
      if (message.payload.status === "ACCEPTED") {
        subscription.retryCount = 0;
        if (subscription.resumeTimer !== undefined) { window.clearTimeout(subscription.resumeTimer); subscription.resumeTimer = undefined; }
      } else if (message.payload.status === "SNAPSHOT_NOT_READY") this.scheduleResume(subscription);
      else if (message.payload.status === "CAPABILITY_MISMATCH") { this.setPhase("incompatible", "客户端与 Session 能力版本不一致。"); subscription.state = { ...subscription.state, phase: "error", fatalRenderError: "能力版本不一致。" }; notify(subscription); }
      else if (message.payload.status === "SESSION_NOT_FOUND" || message.payload.status === "SESSION_NOT_RUNNING") { subscription.state = { ...subscription.state, phase: "ended" }; notify(subscription); }
      return;
    }
    if (message.type === "resync.required") {
      subscription.state = handleServerMessage(subscription.state, message);
      notify(subscription);
      // The API sends `resync.required` and its replacement snapshot as one
      // writer work group. Disposing and reopening here creates a second
      // subscription while the first group is still in flight, which can
      // deliver an old stream-ended event or make a healthy Worker look
      // detached. Wait for the snapshot that immediately follows instead.
      return;
    }
    if (
      message.type === "session.stream.ended" &&
      subscription.state.workerEpoch !== null &&
      message.workerEpoch !== undefined &&
      message.workerEpoch < subscription.state.workerEpoch
    ) return;
    const previousPendingStatus = subscription.state.pendingInput?.status;
    subscription.state = handleServerMessage(subscription.state, message);
    if (message.type === "session.snapshot") {
      subscription.retryCount = 0;
      if (subscription.resumeTimer !== undefined) { window.clearTimeout(subscription.resumeTimer); subscription.resumeTimer = undefined; }
    }
    if (previousPendingStatus === "pending" && message.type === "session.stream.ended") subscription.state = markInputUnknown(subscription.state);
    notify(subscription);
  }

  private resume(subscription: Subscription): void {
    if (this.phase !== "ready" || this.subscriptions.get(subscription.sessionId) !== subscription) return;
    if (subscription.resumeTimer !== undefined) { window.clearTimeout(subscription.resumeTimer); subscription.resumeTimer = undefined; }
    subscription.state = beginResume(subscription.state);
    notify(subscription);
    this.send("session.resume", { capabilityDigest: CAPABILITY_DIGEST, ...(subscription.state.workerEpoch === null ? {} : { lastEpoch: subscription.state.workerEpoch }) }, subscription.sessionId);
  }

  private scheduleResume(subscription: Subscription, delay = Math.min(1_500, 150 * 2 ** subscription.retryCount)): void {
    if (subscription.resumeTimer !== undefined) window.clearTimeout(subscription.resumeTimer);
    subscription.retryCount = Math.min(subscription.retryCount + 1, 5);
    subscription.resumeTimer = window.setTimeout(() => { subscription.resumeTimer = undefined; this.resume(subscription); }, delay);
  }

  private send(type: string, payload: object, sessionId?: string, workerEpoch?: number): void {
    if (this.socket?.readyState !== WebSocket.OPEN) return;
    const message = { protocolVersion: 1, type, messageId: newMessageId(), ...(sessionId ? { sessionId } : {}), ...(workerEpoch ? { workerEpoch } : {}), payload };
    this.socket.send(JSON.stringify(message));
  }

  private handleClose(socket: WebSocket, code: number, reason: string): void {
    if (this.socket !== socket) return;
    this.socket = null;
    this.serverHello = null;
    if (this.helloTimer !== null) { window.clearTimeout(this.helloTimer); this.helloTimer = null; }
    if (this.heartbeatTimer !== null) { window.clearTimeout(this.heartbeatTimer); this.heartbeatTimer = null; }
    if (this.disposed) return;
    // The server currently uses 1008 for its heartbeat timeout as well as
    // authentication failures. A dead/paused browser tab must reconnect and
    // resume its subscriptions; only the authentication variants are
    // terminal for this connection manager.
    if (code === 1008 && reason !== "heartbeat_timeout") {
      this.setPhase("auth_required", reason || "authentication_expired");
      return;
    }
    if (code === 1002) { this.setPhase("incompatible", reason || "protocol_error"); return; }
    for (const subscription of this.subscriptions.values()) {
      if (subscription.state.phase === "live" || subscription.state.phase === "snapshot_ready") subscription.state = markInputUnknown(subscription.state);
      notify(subscription);
    }
    this.scheduleReconnect();
  }

  private scheduleReconnect(): void {
    if (this.disposed || this.reconnectTimer !== null || this.subscriptions.size === 0) return;
    if (!this.networkOnline) { this.setPhase("disconnected"); return; }
    this.reconnectAttempt = Math.min(this.reconnectAttempt + 1, 7);
    const base = Math.min(10_000, 250 * 2 ** (this.reconnectAttempt - 1));
    const jitter = Math.round(base * (0.8 + Math.random() * 0.4));
    this.setPhase("backing_off");
    this.reconnectTimer = window.setTimeout(() => { this.reconnectTimer = null; this.connect(); }, jitter);
  }

  private armHeartbeat(): void {
    if (this.heartbeatTimer !== null) window.clearTimeout(this.heartbeatTimer);
    const intervalMilliseconds = this.serverHello?.heartbeatIntervalMilliseconds ?? 20_000;
    const timeoutMilliseconds = this.serverHello?.heartbeatTimeoutMilliseconds ?? 10_000;
    this.heartbeatTimer = window.setTimeout(() => {
      this.heartbeatTimer = null;
      if (this.phase === "ready") this.socket?.close(1000, "heartbeat_timeout");
    }, Math.max(1_000, intervalMilliseconds + timeoutMilliseconds + 1_000));
  }

  private setPhase(phase: ConnectionPhase, detail?: string): void { this.phase = phase; for (const listener of this.listeners) listener(phase, detail); }

  private setNetworkOnline(online: boolean): void {
    if (this.networkOnline === online) return;
    this.networkOnline = online;
    for (const listener of this.networkListeners) listener(online);
    if (!online || this.disposed || this.subscriptions.size === 0 || this.socket) return;
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.connect();
  }
}

function notify(subscription: Subscription): void { for (const listener of subscription.listeners) listener(subscription.state); }
function newMessageId(): string { return `web-${crypto.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`}`; }
function newClientMessageId(): string { return `client-${crypto.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`}`; }
function realtimeUrl(): string { const protocol = window.location.protocol === "https:" ? "wss:" : "ws:"; return `${protocol}//${window.location.host}/api/v1/realtime`; }
function smoothOffset(serverNow: number, previous: number): number { const candidate = serverNow - Date.now(); return Math.abs(candidate - previous) > 2_000 ? previous + Math.sign(candidate - previous) * 2_000 : previous * 0.8 + candidate * 0.2; }

let sharedManager: RealtimeConnectionManager | null = null;
export function getRealtimeConnectionManager(): RealtimeConnectionManager { return sharedManager ??= new RealtimeConnectionManager(); }

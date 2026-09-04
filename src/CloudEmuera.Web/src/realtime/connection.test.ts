import { describe, expect, it, vi } from "vitest";
import { CAPABILITY_DIGEST } from "./capabilities";
import { RealtimeConnectionManager } from "./connection";

class FakeWebSocket {
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: FakeWebSocket[] = [];
  static deferClose = false;
  readonly sent: string[] = [];
  readyState = 0;
  binaryType = "";
  private readonly handlers = new Map<string, ((event: any) => void)[]>();

  constructor(readonly url: string, readonly protocol: string) { FakeWebSocket.instances.push(this); }
  addEventListener(type: string, handler: (event: any) => void): void { this.handlers.set(type, [...(this.handlers.get(type) ?? []), handler]); }
  send(value: string): void { this.sent.push(value); }
  open(): void { this.readyState = FakeWebSocket.OPEN; this.emit("open", {}); }
  message(value: unknown): void { this.emit("message", { data: JSON.stringify(value) }); }
  close(code = 1000, reason = ""): void {
    this.readyState = FakeWebSocket.deferClose ? FakeWebSocket.CLOSING : FakeWebSocket.CLOSED;
    if (!FakeWebSocket.deferClose) this.emit("close", { code, reason });
  }
  finishClose(code = 1000, reason = ""): void { this.readyState = FakeWebSocket.CLOSED; this.emit("close", { code, reason }); }
  private emit(type: string, event: any): void { for (const handler of this.handlers.get(type) ?? []) handler(event); }
}

const state = {
  scrollback: [], backgroundLayers: [], canvasScene: { drawables: [], hitRegions: [] }, mediaState: { channels: [] }, currentPrompt: null,
  tooltipPresentation: { customEnabled: false, foreground: { red: 0, green: 0, blue: 0, alpha: 255 }, background: { red: 255, green: 255, blue: 225, alpha: 255 }, delayMilliseconds: 500, durationMilliseconds: 0, fontFamily: "session-default", fontSize: 16, textFormat: { horizontal: "left", vertical: "top", wrap: false, trimming: "none", expandTabs: false, rightToLeft: false }, imageMode: false, revision: 0 },
  tooltipResources: [],
  windowMetadata: { title: "Game", viewportWidth: 640, viewportHeight: 480, defaultForeground: null, defaultBackground: null, defaultFont: { family: "game-default", size: 16, lineHeight: 20 } },
  truncation: { wasTruncated: false, droppedNodeCount: 0, droppedLineCount: 0, droppedTextLength: 0 },
};

describe("RealtimeConnectionManager", () => {
  it("uses one socket, resumes by epoch, applies a continuous batch, and fences a gap", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    const updates: string[] = [];
    manager.subscribe("s1", value => updates.push(value.phase));
    const socket = FakeWebSocket.instances[0];
    socket.open();
    expect(FakeWebSocket.instances).toHaveLength(1);
    expect(JSON.parse(socket.sent[0]).type).toBe("client.hello");
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    expect(JSON.parse(socket.sent[1]).type).toBe("session.resume");
    socket.message({ protocolVersion: 6, type: "session.resume.result", messageId: "resume", sessionId: "s1", payload: { status: "ACCEPTED", workerEpoch: 3, reasonCode: null } });
    socket.message({ protocolVersion: 6, type: "session.snapshot", messageId: "snapshot", sessionId: "s1", workerEpoch: 3, sequence: 0, payload: { workerEpoch: 3, snapshotSequence: 0, committedFrameId: 0, consoleState: state } });
    expect(manager.getSessionState("s1")?.phase).toBe("snapshot_ready");
    socket.message({ protocolVersion: 6, type: "display.frame", messageId: "frame", sessionId: "s1", workerEpoch: 3, sequence: 1, payload: { workerEpoch: 3, frameId: 1, commitSequence: 1, reason: "EXPLICIT_REFRESH", requiresSnapshot: false, consoleState: null, transactions: [{ sequence: 1, operations: [{ type: "appendNodes", nodes: [{ type: "text", text: "hello", style: { decorations: [], fontFamily: "game-default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }] }] }] } });
    expect(manager.getSessionState("s1")?.sequence).toBe(1);
    socket.message({ protocolVersion: 6, type: "display.frame", messageId: "gap", sessionId: "s1", workerEpoch: 3, sequence: 3, payload: { workerEpoch: 3, frameId: 2, commitSequence: 3, reason: "EXPLICIT_REFRESH", requiresSnapshot: false, consoleState: null, transactions: [{ sequence: 3, operations: [{ type: "clearConsole" }] }] } });
    expect(manager.getSessionState("s1")?.phase).toBe("resyncing");
    expect(updates).toContain("live");
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("marks a pending input unknown on disconnect instead of generating a new ID", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0]; socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    socket.message({ protocolVersion: 6, type: "session.snapshot", messageId: "snapshot", sessionId: "s1", workerEpoch: 3, sequence: 0, payload: { workerEpoch: 3, snapshotSequence: 0, committedFrameId: 0, consoleState: { ...state, currentPrompt: { promptId: "p1", inputType: "text", promptText: "Answer", defaultValue: null, constraints: { type: "text", maxLength: 20, minimum: null, maximum: null, allowSign: null, allowControlCharacters: null }, timeoutBehavior: "wait", timeoutAction: "close", allowedSources: ["keyboard"], oneInput: false, systemInput: false, stopMessageSkip: false, displayTime: false, timeoutMessage: null, openedAtUnixMilliseconds: Date.now(), deadlineUnixMilliseconds: Date.now() + 10_000, timeoutMilliseconds: 10_000, buttonGeneration: 1 } }, } });
    const clientMessageId = manager.sendInput("s1", { source: "KEYBOARD", value: "answer", pointer: null, key: null });
    expect(clientMessageId).toBeTruthy();
    socket.close(1006, "network");
    expect(manager.getSessionState("s1")?.pendingInput?.status).toBe("unknown");
    expect(manager.getSessionState("s1")?.pendingInput?.clientMessageId).toBe(clientMessageId);
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("does not turn an acknowledged historical input unknown on a later disconnect", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0]; socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    socket.message({ protocolVersion: 6, type: "session.snapshot", messageId: "snapshot", sessionId: "s1", workerEpoch: 3, sequence: 0, payload: { workerEpoch: 3, snapshotSequence: 0, committedFrameId: 0, consoleState: state } });
    const clientMessageId = manager.sendInput("s1", { source: "KEYBOARD", value: "answer", pointer: null, key: null });
    expect(clientMessageId).toBeTruthy();
    socket.message({ protocolVersion: 6, type: "session.input.result", messageId: "receipt", sessionId: "s1", workerEpoch: 3, payload: { clientMessageId, status: "ACCEPTED", reasonCode: "accepted", resolvedPromptId: "prompt-1", normalizedValue: "answer" } });
    expect(manager.getSessionState("s1")?.pendingInput?.status).toBe("accepted");

    socket.close(1006, "network");
    expect(manager.getSessionState("s1")?.pendingInput?.status).toBe("accepted");
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("does not burn reconnect attempts while offline and reconnects immediately when online returns", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const first = FakeWebSocket.instances[0];
    window.dispatchEvent(new Event("offline"));
    expect(manager.isNetworkOnline).toBe(false);
    first.close(1006, "network");
    expect(FakeWebSocket.instances).toHaveLength(1);
    window.dispatchEvent(new Event("online"));
    expect(manager.isNetworkOnline).toBe(true);
    expect(FakeWebSocket.instances).toHaveLength(2);
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("transitions a subscribed stream to ended and fences later batches", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0];
    socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    socket.message({ protocolVersion: 6, type: "session.stream.ended", messageId: "ended", sessionId: "s1", workerEpoch: 3, payload: { reasonCode: "runtime_completed" } });
    expect(manager.getSessionState("s1")?.phase).toBe("ended");
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("waits for the replacement snapshot instead of unsubscribing during resync", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0];
    socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    socket.message({ protocolVersion: 6, type: "session.snapshot", messageId: "snapshot", sessionId: "s1", workerEpoch: 3, sequence: 0, payload: { workerEpoch: 3, snapshotSequence: 0, committedFrameId: 0, consoleState: state } });
    socket.message({ protocolVersion: 6, type: "resync.required", messageId: "resync", sessionId: "s1", workerEpoch: 3, sequence: 1, payload: { workerEpoch: 3, observedSequence: 1, reason: "snapshot-replaced" } });
    expect(manager.getSessionState("s1")?.phase).toBe("resyncing");
    expect(manager.getSessionState("s1")?.fatalRenderError).toBeNull();
    expect(socket.sent.filter(value => JSON.parse(value).type === "session.unsubscribe")).toHaveLength(0);
    socket.message({ protocolVersion: 6, type: "session.snapshot", messageId: "replacement", sessionId: "s1", workerEpoch: 3, sequence: 1, payload: { workerEpoch: 3, snapshotSequence: 1, committedFrameId: 1, consoleState: state } });
    expect(manager.getSessionState("s1")?.phase).toBe("snapshot_ready");
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("does not mutate the console state for an unsubscribe acknowledgement", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0];
    socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    socket.message({ protocolVersion: 6, type: "session.snapshot", messageId: "snapshot", sessionId: "s1", workerEpoch: 3, sequence: 0, payload: { workerEpoch: 3, snapshotSequence: 0, committedFrameId: 0, consoleState: state } });
    socket.message({ protocolVersion: 6, type: "session.stream.ended", messageId: "ended", sessionId: "s1", workerEpoch: 3, payload: { reasonCode: "unsubscribed" } });
    expect(manager.getSessionState("s1")?.phase).toBe("snapshot_ready");
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("stops on capability mismatch and does not enter a partial ready state", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0];
    socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: "sha256:capability-mismatch" } });
    expect(manager.status).toBe("incompatible");
    expect(socket.readyState).toBe(FakeWebSocket.CLOSED);
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("retries a not-ready snapshot with a bounded delay", () => {
    vi.useFakeTimers();
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0];
    socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    socket.message({ protocolVersion: 6, type: "session.resume.result", messageId: "resume-1", sessionId: "s1", payload: { status: "SNAPSHOT_NOT_READY", workerEpoch: null, reasonCode: "snapshot_pending" } });
    expect(socket.sent.filter(value => JSON.parse(value).type === "session.resume")).toHaveLength(1);
    vi.advanceTimersByTime(200);
    expect(socket.sent.filter(value => JSON.parse(value).type === "session.resume")).toHaveLength(2);
    manager.dispose();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("closes a heartbeat-stalled socket without changing the Session worker state", () => {
    vi.useFakeTimers();
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const socket = FakeWebSocket.instances[0];
    socket.open();
    socket.message({ protocolVersion: 6, type: "server.hello", messageId: "hello", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 1_000, heartbeatTimeoutMilliseconds: 1_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    // The server sends its first ping after the advertised interval. The
    // client must not use only the pong deadline from server.hello, or it
    // will close before the first ping arrives.
    vi.advanceTimersByTime(2_999);
    expect(socket.readyState).not.toBe(FakeWebSocket.CLOSED);
    vi.advanceTimersByTime(1);
    expect(socket.readyState).toBe(FakeWebSocket.CLOSED);
    expect(manager.getSessionState("s1")?.phase).not.toBe("ended");
    manager.dispose();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("does not create duplicate sockets when a StrictMode-style subscription remounts", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    const firstCleanup = manager.subscribe("s1", () => undefined);
    const secondCleanup = manager.subscribe("s1", () => undefined);
    expect(FakeWebSocket.instances).toHaveLength(1);
    firstCleanup();
    expect(FakeWebSocket.instances).toHaveLength(1);
    secondCleanup();
    expect(FakeWebSocket.instances[0].readyState).toBe(FakeWebSocket.CLOSED);
    expect(FakeWebSocket.instances).toHaveLength(1);
    manager.dispose();
    vi.unstubAllGlobals();
  });

  it("replaces an asynchronously closing socket when a Session is re-entered", () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    FakeWebSocket.deferClose = true;
    const manager = new RealtimeConnectionManager();
    const firstCleanup = manager.subscribe("s1", () => undefined);
    const first = FakeWebSocket.instances[0];
    firstCleanup();
    const secondCleanup = manager.subscribe("s1", () => undefined);
    const second = FakeWebSocket.instances[1];
    expect(first.readyState).toBe(FakeWebSocket.CLOSING);
    expect(second).toBeTruthy();
    expect(second).not.toBe(first);

    first.finishClose();
    expect(FakeWebSocket.instances).toHaveLength(2);
    secondCleanup();
    manager.dispose();
    FakeWebSocket.deferClose = false;
    vi.unstubAllGlobals();
  });

  it("reconnects after the server closes a heartbeat-timeout socket", () => {
    vi.useFakeTimers();
    vi.spyOn(Math, "random").mockReturnValue(0.5);
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const first = FakeWebSocket.instances[0];

    first.finishClose(1008, "heartbeat_timeout");
    expect(manager.status).toBe("backing_off");
    vi.advanceTimersByTime(250);

    expect(FakeWebSocket.instances).toHaveLength(2);
    expect(manager.status).toBe("connecting");
    manager.dispose();
    vi.mocked(Math.random).mockRestore();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("finishes an in-page reconnect when the server snapshot is unchanged", () => {
    vi.useFakeTimers();
    vi.spyOn(Math, "random").mockReturnValue(0.5);
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const first = FakeWebSocket.instances[0];
    first.open();
    first.message({ protocolVersion: 6, type: "server.hello", messageId: "hello-1", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    first.message({ protocolVersion: 6, type: "session.snapshot", messageId: "snapshot-1", sessionId: "s1", workerEpoch: 3, sequence: 0, payload: { workerEpoch: 3, snapshotSequence: 0, committedFrameId: 0, consoleState: state } });

    first.close(1006, "network");
    vi.advanceTimersByTime(250);
    const second = FakeWebSocket.instances[1];
    second.open();
    second.message({ protocolVersion: 6, type: "server.hello", messageId: "hello-2", payload: { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c2", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000, heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST } });
    expect(manager.getSessionState("s1")?.phase).toBe("resuming");
    second.message({ protocolVersion: 6, type: "session.resume.result", messageId: "resume-2", sessionId: "s1", payload: { status: "ACCEPTED", workerEpoch: 3, reasonCode: null } });
    second.message({ protocolVersion: 6, type: "session.snapshot", messageId: "snapshot-2", sessionId: "s1", workerEpoch: 3, sequence: 0, payload: { workerEpoch: 3, snapshotSequence: 0, committedFrameId: 0, consoleState: state } });

    expect(manager.status).toBe("ready");
    expect(manager.getSessionState("s1")?.phase).toBe("snapshot_ready");
    manager.dispose();
    vi.mocked(Math.random).mockRestore();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("reconnects after a server hello timeout instead of requiring authentication", () => {
    vi.useFakeTimers();
    vi.spyOn(Math, "random").mockReturnValue(0.5);
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    const first = FakeWebSocket.instances[0];

    first.finishClose(1008, "hello_timeout");
    expect(manager.status).toBe("backing_off");
    vi.advanceTimersByTime(250);

    expect(FakeWebSocket.instances).toHaveLength(2);
    expect(manager.status).toBe("connecting");
    manager.dispose();
    vi.mocked(Math.random).mockRestore();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("reconnects after its local hello timeout instead of becoming incompatible", () => {
    vi.useFakeTimers();
    vi.spyOn(Math, "random").mockReturnValue(0.5);
    vi.stubGlobal("WebSocket", FakeWebSocket);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);

    vi.advanceTimersByTime(8_000);
    expect(manager.status).toBe("backing_off");
    vi.advanceTimersByTime(250);

    expect(FakeWebSocket.instances).toHaveLength(2);
    expect(manager.status).toBe("connecting");
    manager.dispose();
    vi.mocked(Math.random).mockRestore();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("uses bounded exponential reconnect backoff instead of a reconnect storm", () => {
    vi.useFakeTimers();
    vi.stubGlobal("WebSocket", FakeWebSocket);
    vi.spyOn(Math, "random").mockReturnValue(0.5);
    FakeWebSocket.instances = [];
    const manager = new RealtimeConnectionManager();
    manager.subscribe("s1", () => undefined);
    FakeWebSocket.instances[0].close(1006, "network");
    expect(FakeWebSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(249);
    expect(FakeWebSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeWebSocket.instances).toHaveLength(2);
    FakeWebSocket.instances[1].close(1006, "network");
    vi.advanceTimersByTime(499);
    expect(FakeWebSocket.instances).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(FakeWebSocket.instances).toHaveLength(3);
    manager.dispose();
    vi.mocked(Math.random).mockRestore();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
});

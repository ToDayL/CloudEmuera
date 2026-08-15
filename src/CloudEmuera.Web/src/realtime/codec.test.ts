import { describe, expect, it } from "vitest";
import { decodeRealtimeMessage, RealtimeDecodeError } from "./codec";
import { CAPABILITY_DIGEST } from "./capabilities";

function envelope(type: string, payload: unknown, extra: Record<string, unknown> = {}) {
  return JSON.stringify({ protocolVersion: 1, type, messageId: "msg-1", ...extra, payload });
}

const emptyState = {
  scrollback: [], backgroundLayers: [], canvasScene: { drawables: [], hitRegions: [] }, mediaState: { channels: [] }, currentPrompt: null,
  windowMetadata: { title: "Game", viewportWidth: 640, viewportHeight: 480, defaultForeground: null, defaultBackground: null, defaultFont: { family: "game-default", size: 16, lineHeight: 20 } },
  truncation: { wasTruncated: false, droppedNodeCount: 0, droppedLineCount: 0, droppedTextLength: 0 },
};

describe("realtime codec", () => {
  it("decodes a closed server hello and complete snapshot envelope", () => {
    const helloJson = envelope("server.hello", {
      protocolVersion: 1, payloadSchemaVersion: "p1-11", connectionId: "conn-1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000,
      heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST,
    });
    const hello = decodeRealtimeMessage(helloJson);
    expect(hello.type).toBe("server.hello");
    const snapshot = decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, consoleState: emptyState }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }));
    expect(snapshot.type).toBe("session.snapshot");
  });

  it("accepts the runtime HTML root and its canonical allowlisted elements", () => {
    const style = { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null };
    const html = { ...emptyState, scrollback: [{ lineId: "line-1", nodes: [{ type: "htmlIsland", root: {
      type: "element", tag: "div", style, assetId: null, altText: null, children: [
        { type: "element", tag: "p", style, assetId: null, altText: null, children: [{ type: "text", text: "safe" }] },
        { type: "element", tag: "img", style, assetId: "sha256-image", altText: "fixture", children: [] },
      ],
    } }], alignment: "left", temporary: false }] };
    const decoded = decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, consoleState: html }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }));
    expect(decoded.type).toBe("session.snapshot");
  });

  it("rejects duplicate keys, unknown fields, unsupported HTML and invalid PNG raster", () => {
    expect(() => decodeRealtimeMessage('{"protocolVersion":1,"type":"server.hello","messageId":"m","messageId":"n","payload":{}}')).toThrowError(RealtimeDecodeError);
    expect(() => decodeRealtimeMessage(envelope("server.hello", { protocolVersion: 1, payloadSchemaVersion: "p1-11", connectionId: "c", serverNowUnixMilliseconds: 1, heartbeatIntervalMilliseconds: 1, heartbeatTimeoutMilliseconds: 1, maxSubscriptionsPerConnection: 1, maxPendingInputsPerConnection: 1, serverMessageMaxBytes: 1, capabilityDigest: CAPABILITY_DIGEST, extra: true }))).toThrowError("不受支持的字段");
    const html = { ...emptyState, scrollback: [{ lineId: "line-1", nodes: [{ type: "htmlIsland", root: { type: "element", tag: "script", children: [] } }], alignment: "left", temporary: false }] };
    expect(() => decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, consoleState: html }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }))).toThrowError(RealtimeDecodeError);
    const raster = { type: "raster", drawableId: "r", bounds: { x: 0, y: 0, width: 1, height: 1 }, zIndex: 0, opacity: 1, pngData: "aGVsbG8=" };
    const state = { ...emptyState, canvasScene: { drawables: [raster], hitRegions: [] } };
    expect(() => decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, consoleState: state }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }))).toThrowError("PNG");
  });

  it("rejects non-finite JSON numbers before JSON.parse can accept them", () => {
    expect(() => decodeRealtimeMessage(envelope("connection.ping", { nonce: "n", serverNowUnixMilliseconds: 1e999 }))).toThrowError(RealtimeDecodeError);
  });

  it("rejects a payload schema version that the client did not compile", () => {
    expect(() => decodeRealtimeMessage(envelope("server.hello", {
      protocolVersion: 1, payloadSchemaVersion: "p1-09", connectionId: "c", serverNowUnixMilliseconds: 1,
      heartbeatIntervalMilliseconds: 1, heartbeatTimeoutMilliseconds: 1, maxSubscriptionsPerConnection: 1,
      maxPendingInputsPerConnection: 1, serverMessageMaxBytes: 1, capabilityDigest: CAPABILITY_DIGEST,
    }))).toThrowError("实时消息内容版本不兼容");
  });
});

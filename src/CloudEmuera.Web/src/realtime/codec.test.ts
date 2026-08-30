import { describe, expect, it } from "vitest";
import { decodeRealtimeMessage, RealtimeDecodeError } from "./codec";
import { CAPABILITY_DIGEST } from "./capabilities";

function envelope(type: string, payload: unknown, extra: Record<string, unknown> = {}) {
  return JSON.stringify({ protocolVersion: 6, type, messageId: "msg-1", ...extra, payload });
}

const emptyState = {
  scrollback: [], backgroundLayers: [], canvasScene: { drawables: [], hitRegions: [] }, mediaState: { channels: [] }, currentPrompt: null,
  tooltipPresentation: { customEnabled: false, foreground: { red: 0, green: 0, blue: 0, alpha: 255 }, background: { red: 255, green: 255, blue: 225, alpha: 255 }, delayMilliseconds: 500, durationMilliseconds: 0, fontFamily: "session-default", fontSize: 16, textFormat: { horizontal: "left", vertical: "top", wrap: false, trimming: "none", expandTabs: false, rightToLeft: false }, imageMode: false, revision: 0 },
  tooltipResources: [],
  windowMetadata: { title: "Game", viewportWidth: 640, viewportHeight: 480, defaultForeground: null, defaultBackground: null, defaultFont: { family: "game-default", size: 16, lineHeight: 20 } },
  truncation: { wasTruncated: false, droppedNodeCount: 0, droppedLineCount: 0, droppedTextLength: 0 },
};

describe("realtime codec", () => {
  it("decodes a closed server hello and complete snapshot envelope", () => {
    const helloJson = envelope("server.hello", {
      protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "conn-1", serverNowUnixMilliseconds: Date.now(), heartbeatIntervalMilliseconds: 20_000,
      heartbeatTimeoutMilliseconds: 10_000, maxSubscriptionsPerConnection: 4, maxPendingInputsPerConnection: 32, serverMessageMaxBytes: 1_000_000, capabilityDigest: CAPABILITY_DIGEST,
    });
    const hello = decodeRealtimeMessage(helloJson);
    expect(hello.type).toBe("server.hello");
    const snapshot = decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: emptyState }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }));
    expect(snapshot.type).toBe("session.snapshot");
  });

  it("accepts the runtime HTML root and its canonical allowlisted elements", () => {
    const style = { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null };
    const longPathAssetId = `path-${"a".repeat(300)}`;
    const html = { ...emptyState, scrollback: [{ lineId: "line-1", nodes: [{ type: "htmlIsland", root: {
      type: "element", tag: "div", style, assetId: null, altText: null, children: [
        { type: "element", tag: "p", style, assetId: null, altText: null, children: [{ type: "text", text: "safe" }] },
        { type: "element", tag: "img", style, assetId: longPathAssetId, altText: "fixture", children: [] },
      ],
    } }], alignment: "left", temporary: false }] };
    const decoded = decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: html }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }));
    expect(decoded.type).toBe("session.snapshot");
  });

  it("decodes native Emuera display semantics without dropping layout fields", () => {
    const color = { red: 1, green: 2, blue: 3, alpha: 255 };
    const style = { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null, buttonColor: color };
    const state = {
      ...emptyState,
      scrollback: [{ lineId: "line-1", nodes: [{
        type: "div", bounds: { x: 1, y: 2, width: 30, height: 12 }, zIndex: 3, background: color, isRelative: false,
        box: { margin: { top: 1, right: 2, bottom: 3, left: 4 }, padding: { top: 5, right: 6, bottom: 7, left: 8 }, border: { top: 1, right: 1, bottom: 1, left: 1 }, radius: { top: 2, right: 3, bottom: 4, left: 5 }, borderColors: [color, null, color, null] },
        children: [{ type: "button", children: [{ type: "text", text: "go", style }], value: "go", tooltip: null, enabled: true, generation: 1, positionX: 42 }],
      }], alignment: "left", temporary: false }],
    };
    const decoded = decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: state }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }));
    const node = (decoded as Extract<typeof decoded, { type: "session.snapshot" }>).payload.consoleState.scrollback[0].nodes[0];
    expect(node.type).toBe("div");
    if (node.type === "div") {
      expect(node.isRelative).toBe(false);
      expect(node.box?.borderColors[1]).toBeNull();
      expect(node.children[0].type).toBe("button");
      if (node.children[0].type === "button") expect(node.children[0].positionX).toBe(42);
    }
  });

  it("preserves a bounded negative positioned segment for viewport clipping", () => {
    const style = { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null };
    const state = {
      ...emptyState,
      scrollback: [{ lineId: "cutin", nodes: [{
        type: "positionedInlineSegment", positionX: -60, measuredWidth: 300, action: null,
        children: [{ type: "text", text: "cutin", style }],
      }], alignment: "left", temporary: false }],
    };

    const decoded = decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: state }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }));
    const node = (decoded as Extract<typeof decoded, { type: "session.snapshot" }>).payload.consoleState.scrollback[0].nodes[0];
    expect(node.type).toBe("positionedInlineSegment");
    if (node.type === "positionedInlineSegment") expect(node.positionX).toBe(-60);
  });

  it("accepts structured upstream HTML islands without falling back to legacy HTML", () => {
    const style = { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null };
    const state = {
      ...emptyState,
      scrollback: [{ lineId: "island-line", alignment: "left", temporary: false, noWrap: true, nodes: [{
        type: "htmlIsland",
        nodes: [{
          type: "div", bounds: { x: 1, y: 2, width: 30, height: 12 }, zIndex: 3, background: null, isRelative: true, box: null,
          children: [{ type: "button", children: [{ type: "text", text: "Go", style }], value: "go", tooltip: null, enabled: true, generation: 4 }],
        }],
      }] }],
    };
    const decoded = decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: state }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }));
    const node = (decoded as Extract<typeof decoded, { type: "session.snapshot" }>).payload.consoleState.scrollback[0].nodes[0];
    expect(node.type).toBe("htmlIsland");
    if (node.type === "htmlIsland") {
      expect(node.root).toBeUndefined();
      expect(node.nodes?.[0].type).toBe("div");
    }
  });

  it("requires the authoritative button generation on every prompt", () => {
    const currentPrompt = {
      promptId: "prompt-1", inputType: "integer", promptText: null, defaultValue: null,
      constraints: { type: "integer", maxLength: null, minimum: null, maximum: null, allowSign: true, allowControlCharacters: null },
      timeoutBehavior: "wait", timeoutAction: "close", allowedSources: ["keyboard", "button"],
      oneInput: false, systemInput: false, stopMessageSkip: false, displayTime: false, timeoutMessage: null,
      openedAtUnixMilliseconds: 1, deadlineUnixMilliseconds: 0, timeoutMilliseconds: null, buttonGeneration: 3,
    };
    const state = { ...emptyState, currentPrompt };
    expect(decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: state }, { sessionId: "s1", workerEpoch: 2, sequence: 0 })).type).toBe("session.snapshot");

    const { buttonGeneration: _, ...missingGeneration } = currentPrompt;
    expect(() => decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: { ...emptyState, currentPrompt: missingGeneration } }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }))).toThrowError("buttonGeneration");
  });

  it("rejects duplicate keys, unknown fields, unsupported HTML and invalid PNG raster", () => {
    expect(() => decodeRealtimeMessage('{"protocolVersion":6,"type":"server.hello","messageId":"m","messageId":"n","payload":{}}')).toThrowError(RealtimeDecodeError);
    expect(() => decodeRealtimeMessage(envelope("server.hello", { protocolVersion: 6, payloadSchemaVersion: "p1-s10-button-generation", connectionId: "c", serverNowUnixMilliseconds: 1, heartbeatIntervalMilliseconds: 1, heartbeatTimeoutMilliseconds: 1, maxSubscriptionsPerConnection: 1, maxPendingInputsPerConnection: 1, serverMessageMaxBytes: 1, capabilityDigest: CAPABILITY_DIGEST, extra: true }))).toThrowError("不受支持的字段");
    const html = { ...emptyState, scrollback: [{ lineId: "line-1", nodes: [{ type: "htmlIsland", root: { type: "element", tag: "script", children: [] } }], alignment: "left", temporary: false }] };
    expect(() => decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: html }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }))).toThrowError(RealtimeDecodeError);
    const raster = { type: "raster", drawableId: "r", bounds: { x: 0, y: 0, width: 1, height: 1 }, zIndex: 0, opacity: 1, pngData: "aGVsbG8=" };
    const state = { ...emptyState, canvasScene: { drawables: [raster], hitRegions: [] } };
    expect(() => decodeRealtimeMessage(envelope("session.snapshot", { workerEpoch: 2, snapshotSequence: 0, committedFrameId: 0, consoleState: state }, { sessionId: "s1", workerEpoch: 2, sequence: 0 }))).toThrowError("PNG");
  });

  it("rejects non-finite JSON numbers before JSON.parse can accept them", () => {
    expect(() => decodeRealtimeMessage(envelope("connection.ping", { nonce: "n", serverNowUnixMilliseconds: 1e999 }))).toThrowError(RealtimeDecodeError);
  });

  it("rejects a payload schema version that the client did not compile", () => {
    expect(() => decodeRealtimeMessage(envelope("server.hello", {
      protocolVersion: 6, payloadSchemaVersion: "p1-09", connectionId: "c", serverNowUnixMilliseconds: 1,
      heartbeatIntervalMilliseconds: 1, heartbeatTimeoutMilliseconds: 1, maxSubscriptionsPerConnection: 1,
      maxPendingInputsPerConnection: 1, serverMessageMaxBytes: 1, capabilityDigest: CAPABILITY_DIGEST,
    }))).toThrowError("实时消息内容版本不兼容");
  });
});

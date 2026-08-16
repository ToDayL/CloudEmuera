import { describe, expect, it } from "vitest";
import type { RealtimeLine, RealtimeNode, RealtimeOperation, RealtimeTransaction } from "./protocol";
import { applyTransaction, applyTransactions, ConsoleReductionError, createEmptyConsoleState } from "./reducer";
import { applyBatch, createPendingInput, createSessionStoreState, replaceSnapshot } from "./sessionStore";
import reducerFixture from "./fixtures/reducer-v1.json";

const style = { decorations: [], fontFamily: "game-default", fontSize: 16, lineHeight: 20, foreground: null, background: null };
const text = (value: string): RealtimeNode => ({ type: "text", text: value, style });
const line = (lineId: string, nodes: RealtimeNode[] = []): RealtimeLine => ({ lineId, nodes, alignment: "left", temporary: false });
const transaction = (sequence: number, operations: RealtimeTransaction["operations"]): RealtimeTransaction => ({ sequence, operations });
type FixtureLine = { lineId: string; text: string; alignment: "left" | "center" | "right"; temporary: boolean };
type FixtureOperation =
  | { type: "appendLine" | "replaceLine"; line: FixtureLine }
  | { type: "appendInline"; lineId: string; text: string }
  | { type: "deleteLines"; lineIds: string[] };

function fixtureOperation(operation: FixtureOperation): RealtimeOperation {
  if (operation.type === "appendInline") return { type: operation.type, lineId: operation.lineId, nodes: [text(operation.text)] };
  if (operation.type === "deleteLines") return operation;
  return { type: operation.type, line: { lineId: operation.line.lineId, nodes: [text(operation.line.text)], alignment: operation.line.alignment, temporary: operation.line.temporary } };
}

describe("realtime reducer", () => {
  it("matches the shared C# reducer golden fixture", () => {
    const initial = createEmptyConsoleState();
    initial.scrollback = reducerFixture.initialState.lines.map(item => line(item.lineId, [text(item.text)]));
    const transactions: RealtimeTransaction[] = reducerFixture.transactions.map(item => ({
      sequence: item.sequence,
      operations: item.operations.map(operation => fixtureOperation(operation as FixtureOperation)),
    }));
    const result = applyTransactions(initial, transactions, 0);
    expect(result.sequence).toBe(reducerFixture.expectedState.sequence);
    expect(result.state.scrollback.map(item => ({ lineId: item.lineId, text: item.nodes.filter(node => node.type === "text").map(node => node.text).join(""), alignment: item.alignment, temporary: item.temporary }))).toEqual(reducerFixture.expectedState.lines);
  });

  it("applies the complete operation union and keeps generated line IDs unique after deletion", () => {
    const initial = createEmptyConsoleState();
    const result = applyTransaction(initial, transaction(1, [
      { type: "appendNodes", nodes: [text("one"), { type: "lineBreak" }, text("two")] },
      { type: "appendInline", lineId: "line-1", nodes: [text("!")] },
      { type: "replaceLine", line: line("line-1", [text("replaced")] ) },
      { type: "appendLine", line: line("fixed", [text("fixed")]) },
      { type: "deleteLines", lineIds: ["fixed"] },
      { type: "setWindowMetadata", windowMetadata: { ...initial.windowMetadata, title: "Game", viewportWidth: 800 } },
      { type: "upsertBackground", backgroundLayer: { layerId: "bg", assetId: "asset", mode: "cover", opacity: 1, depth: 0 } },
      { type: "removeBackground", layerId: "missing" },
      { type: "upsertDrawable", drawable: { type: "shape", drawableId: "shape", bounds: { x: 0, y: 0, width: 10, height: 10 }, zIndex: 1, opacity: 1, shape: "rectangle", fill: null, stroke: null, points: [] } },
      { type: "clearSceneRange", minimumZIndex: 2, maximumZIndex: 2 },
      { type: "upsertHitRegion", hitRegion: { regionId: "hit", bounds: { x: 0, y: 0, width: 10, height: 10 }, inputValue: "1", enabled: true, tooltip: null } },
      { type: "removeHitRegion", regionId: "missing" },
      { type: "setMediaChannel", mediaChannel: { channel: "music", assetId: "audio", playbackState: "playing", loop: true, volume: 1, revision: 0, startPolicy: "onUserGesture" } },
      { type: "stopMediaChannel", channel: "music" },
      { type: "stopAllMedia" },
    ]));

    expect(result.scrollback.map(item => item.lineId)).toEqual(["line-1", "line-2"]);
    expect(result.scrollback[0].nodes).toEqual([text("replaced")]);
    expect(result.scrollback[1].nodes).toEqual([text("two")]);
    expect(result.windowMetadata.title).toBe("Game");
    expect(result.backgroundLayers).toHaveLength(1);
    expect(result.canvasScene.drawables).toHaveLength(1);
    expect(result.canvasScene.hitRegions).toHaveLength(1);
    expect(result.mediaState.channels[0]).toMatchObject({ playbackState: "stopped", revision: 2 });

    const afterDelete = applyTransaction(result, transaction(2, [{ type: "deleteLines", lineIds: ["line-1"] }, { type: "appendNodes", nodes: [{ type: "lineBreak" }, text("new")] }]));
    expect(afterDelete.scrollback.map(item => item.lineId)).toEqual(["line-2", "line-1"]);
  });

  it("does not publish a partially applied transaction", () => {
    const initial = createEmptyConsoleState();
    expect(() => applyTransaction(initial, transaction(1, [
      { type: "setWindowMetadata", windowMetadata: { ...initial.windowMetadata, title: "temporary" } },
      { type: "deleteLines", lineIds: ["missing"] },
    ]))).toThrowError(ConsoleReductionError);
    expect(initial.windowMetadata.title).toBe("");
    expect(initial.scrollback).toHaveLength(0);
  });

  it("rejects gaps and fences old batches after resync or epoch replacement", () => {
    const initial = createEmptyConsoleState();
    expect(() => applyTransactions(initial, [transaction(2, [{ type: "clearConsole" }])], 0)).toThrowError("交易序号不连续");

    let state = createSessionStoreState("s1");
    state = replaceSnapshot(state, { sessionId: "s1", workerEpoch: 4, sequence: 0 }, { workerEpoch: 4, snapshotSequence: 0, consoleState: initial });
    state = createPendingInput(state, { promptId: "p1", workerEpoch: 4, clientMessageId: "client-1", value: "yes", source: "KEYBOARD" });
    state = applyBatch(state, { sessionId: "s1", workerEpoch: 4, sequence: 2 }, { workerEpoch: 4, firstSequence: 1, lastSequence: 2, transactions: [transaction(1, [{ type: "appendNodes", nodes: [text("one")] }]), transaction(2, [{ type: "appendNodes", nodes: [text("two")] }])] });
    expect(state.sequence).toBe(2);
    const duplicate = applyBatch(state, { sessionId: "s1", workerEpoch: 4, sequence: 2 }, { workerEpoch: 4, firstSequence: 1, lastSequence: 2, transactions: [transaction(1, [{ type: "appendNodes", nodes: [text("duplicate")] }]), transaction(2, [{ type: "appendNodes", nodes: [text("duplicate")] }])] });
    expect(duplicate).toBe(state);
    const staleSnapshot = replaceSnapshot(state, { sessionId: "s1", workerEpoch: 4, sequence: 1 }, { workerEpoch: 4, snapshotSequence: 1, consoleState: initial });
    expect(staleSnapshot).toBe(state);
    state = applyBatch(state, { sessionId: "s1", workerEpoch: 4, sequence: 4 }, { workerEpoch: 4, firstSequence: 4, lastSequence: 4, transactions: [transaction(4, [{ type: "appendNodes", nodes: [text("must-not-apply")] }])] });
    expect(state.phase).toBe("resyncing");
    expect(state.consoleState?.scrollback.flatMap(item => item.nodes).some(node => node.type === "text" && node.text === "must-not-apply")).toBe(false);
    state = replaceSnapshot(state, { sessionId: "s1", workerEpoch: 5, sequence: 0 }, { workerEpoch: 5, snapshotSequence: 0, consoleState: initial });
    expect(state.pendingInput).toBeNull();

    const oldEpochBatch = applyBatch(state, { sessionId: "s1", workerEpoch: 4, sequence: 3 }, { workerEpoch: 4, firstSequence: 3, lastSequence: 3, transactions: [transaction(3, [{ type: "appendNodes", nodes: [text("old epoch")] }])] });
    expect(oldEpochBatch).toBe(state);
  });

  it("resyncs on an overlapping batch that contains unseen transactions", () => {
    const initial = createEmptyConsoleState();
    let state = replaceSnapshot(createSessionStoreState("s1"), { sessionId: "s1", workerEpoch: 1, sequence: 0 }, { workerEpoch: 1, snapshotSequence: 0, consoleState: initial });
    state = applyBatch(state, { sessionId: "s1", workerEpoch: 1, sequence: 2 }, { workerEpoch: 1, firstSequence: 1, lastSequence: 2, transactions: [transaction(1, [{ type: "appendNodes", nodes: [text("one")] }]), transaction(2, [{ type: "appendNodes", nodes: [text("two")] }])] });

    const overlapping = applyBatch(state, { sessionId: "s1", workerEpoch: 1, sequence: 3 }, { workerEpoch: 1, firstSequence: 2, lastSequence: 3, transactions: [transaction(2, [{ type: "appendNodes", nodes: [text("replayed")] }]), transaction(3, [{ type: "appendNodes", nodes: [text("three")] }])] });
    expect(overlapping.phase).toBe("resyncing");
    expect(overlapping.sequence).toBe(2);
  });
});

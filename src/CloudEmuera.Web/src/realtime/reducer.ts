import { ConsoleState, RealtimeLine, RealtimeNode, RealtimeOperation, RealtimeTransaction } from "./protocol";

export class ConsoleReductionError extends Error {
  readonly reasonCode: string;

  constructor(reasonCode: string, message: string) {
    super(message);
    this.name = "ConsoleReductionError";
    this.reasonCode = reasonCode;
  }
}

export const EMPTY_WINDOW = {
  title: "",
  viewportWidth: 640,
  viewportHeight: 480,
  defaultForeground: null,
  defaultBackground: null,
  defaultFont: { family: "default", size: 16, lineHeight: 0 },
};

export function createEmptyConsoleState(): ConsoleState {
  return {
    scrollback: [],
    backgroundLayers: [],
    canvasScene: { drawables: [], hitRegions: [] },
    mediaState: { channels: [] },
    currentPrompt: null,
    windowMetadata: { ...EMPTY_WINDOW, defaultFont: { ...EMPTY_WINDOW.defaultFont } },
    truncation: { wasTruncated: false, droppedNodeCount: 0, droppedLineCount: 0, droppedTextLength: 0 },
  };
}

export function cloneConsoleState(state: ConsoleState): ConsoleState {
  // The reducer only mutates collection containers and replaces changed
  // lines/items. Keep immutable payload objects shared so memoized renderers
  // can skip work for output that did not change in this frame.
  return {
    ...state,
    scrollback: [...state.scrollback],
    backgroundLayers: [...state.backgroundLayers],
    canvasScene: {
      ...state.canvasScene,
      drawables: [...state.canvasScene.drawables],
      hitRegions: [...state.canvasScene.hitRegions],
    },
    mediaState: {
      ...state.mediaState,
      channels: [...state.mediaState.channels],
    },
  };
}

/** Apply one transaction to a private candidate. The input state is never mutated. */
export function applyTransaction(state: ConsoleState, transaction: RealtimeTransaction): ConsoleState {
  if (!Number.isSafeInteger(transaction.sequence) || transaction.sequence <= 0)
    throw new ConsoleReductionError("invalid_sequence", "交易序号无效。");
  if (transaction.operations.length === 0)
    throw new ConsoleReductionError("empty_transaction", "交易不能为空。");
  const candidate = cloneConsoleState(state);
  for (const operation of transaction.operations) applyOperation(candidate, operation);
  return candidate;
}

export function applyTransactions(state: ConsoleState, transactions: RealtimeTransaction[], expectedSequence = 0): { state: ConsoleState; sequence: number } {
  let candidate = state;
  let sequence = expectedSequence;
  for (const transaction of transactions) {
    if (transaction.sequence !== sequence + 1)
      throw new ConsoleReductionError("sequence_gap", "交易序号不连续。");
    candidate = applyTransaction(candidate, transaction);
    sequence = transaction.sequence;
  }
  return { state: candidate, sequence };
}

function applyOperation(state: ConsoleState, operation: RealtimeOperation): void {
  switch (operation.type) {
    case "appendNodes": appendNodes(state.scrollback, operation.nodes); return;
    case "clearConsole":
    case "clearScrollback": state.scrollback = []; return;
    case "openPrompt":
      if (state.currentPrompt) throw new ConsoleReductionError("prompt_already_active", "已有活动输入提示。");
      state.currentPrompt = operation.prompt;
      return;
    case "closePrompt":
      if (!state.currentPrompt) throw new ConsoleReductionError("prompt_not_active", "没有活动输入提示。");
      if (state.currentPrompt.promptId !== operation.promptId) throw new ConsoleReductionError("prompt_mismatch", "关闭的提示不是当前提示。");
      state.currentPrompt = null;
      return;
    case "appendLine":
      if (state.scrollback.some(line => line.lineId === operation.line.lineId)) throw new ConsoleReductionError("duplicate_line", "行 ID 已存在。");
      state.scrollback.push(operation.line);
      return;
    case "appendInline": {
      const index = state.scrollback.findIndex(line => line.lineId === operation.lineId);
      if (index < 0) throw new ConsoleReductionError("line_not_found", "目标行不存在。");
      if (operation.nodes.some(node => node.type === "lineBreak")) throw new ConsoleReductionError("invalid_inline_node", "行内追加不能包含换行节点。");
      state.scrollback[index] = { ...state.scrollback[index], nodes: [...state.scrollback[index].nodes, ...operation.nodes] };
      return;
    }
    case "replaceLine": {
      const index = state.scrollback.findIndex(line => line.lineId === operation.line.lineId);
      if (index < 0) throw new ConsoleReductionError("line_not_found", "目标行不存在。");
      state.scrollback[index] = operation.line;
      return;
    }
    case "deleteLines":
      for (const id of operation.lineIds) {
        const index = state.scrollback.findIndex(line => line.lineId === id);
        if (index < 0) throw new ConsoleReductionError("line_not_found", "要删除的行不存在。");
        state.scrollback.splice(index, 1);
      }
      return;
    case "setWindowMetadata": state.windowMetadata = operation.windowMetadata; return;
    case "upsertBackground": upsertById(state.backgroundLayers, operation.backgroundLayer, "layerId"); return;
    case "removeBackground": state.backgroundLayers = state.backgroundLayers.filter(layer => layer.layerId !== operation.layerId); return;
    case "clearBackgrounds": state.backgroundLayers = []; return;
    case "upsertDrawable": upsertById(state.canvasScene.drawables, operation.drawable, "drawableId"); return;
    case "removeDrawable": state.canvasScene.drawables = state.canvasScene.drawables.filter(drawable => drawable.drawableId !== operation.drawableId); return;
    case "clearSceneRange": state.canvasScene.drawables = state.canvasScene.drawables.filter(drawable => drawable.zIndex < operation.minimumZIndex || drawable.zIndex > operation.maximumZIndex); return;
    case "clearScene": state.canvasScene = { drawables: [], hitRegions: [] }; return;
    case "upsertHitRegion": upsertById(state.canvasScene.hitRegions, operation.hitRegion, "regionId"); return;
    case "removeHitRegion": state.canvasScene.hitRegions = state.canvasScene.hitRegions.filter(region => region.regionId !== operation.regionId); return;
    case "clearHitRegions": state.canvasScene.hitRegions = []; return;
    case "setMediaChannel": upsertById(state.mediaState.channels, operation.mediaChannel, "channel"); return;
    case "stopMediaChannel": {
      const channel = state.mediaState.channels.find(item => item.channel === operation.channel);
      if (channel) state.mediaState.channels = state.mediaState.channels.map(item => item.channel === channel.channel ? { ...item, playbackState: "stopped", revision: item.revision + 1 } : item);
      return;
    }
    case "stopAllMedia": state.mediaState.channels = state.mediaState.channels.map(channel => ({ ...channel, playbackState: "stopped", revision: channel.revision + 1 })); return;
    default: return assertNever(operation);
  }
}

function appendNodes(lines: RealtimeLine[], nodes: RealtimeNode[]): void {
  if (lines.length === 0) lines.push({ lineId: nextGeneratedLineId(lines), nodes: [], alignment: "left", temporary: false });
  for (const node of nodes) {
    if (node.type === "lineBreak") lines.push({ lineId: nextGeneratedLineId(lines), nodes: [], alignment: "left", temporary: false });
    else lines[lines.length - 1] = { ...lines[lines.length - 1], nodes: [...lines[lines.length - 1].nodes, node] };
  }
}

function nextGeneratedLineId(lines: RealtimeLine[]): string {
  const existing = new Set(lines.map(line => line.lineId));
  let counter = 1;
  while (existing.has(`line-${counter.toString(16)}`)) counter++;
  return `line-${counter.toString(16)}`;
}

function upsertById<T extends object>(items: T[], value: T, key: keyof T): void {
  const index = items.findIndex(item => item[key] === value[key]);
  if (index < 0) items.push(value); else items[index] = value;
}

function assertNever(value: never): never { throw new ConsoleReductionError("unknown_operation", `未知操作：${String(value)}`); }

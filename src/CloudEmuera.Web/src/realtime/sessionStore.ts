import { InputResultPayload, RealtimeServerMessage, RealtimeSnapshotPayload, RealtimeTransactionBatchPayload } from "./protocol";
import { applyTransactions, ConsoleReductionError, createEmptyConsoleState } from "./reducer";

export type SessionPhase = "idle" | "resuming" | "snapshot_ready" | "live" | "resyncing" | "ended" | "forbidden" | "error";
export type PendingInputStatus = "pending" | "unknown" | "accepted" | "duplicate" | "rejected" | "stale";
export interface PendingInput {
  promptId: string;
  workerEpoch: number;
  clientMessageId: string;
  value: string;
  source: "KEYBOARD" | "BUTTON" | "POINTER";
  pointer?: { x: number; y: number; button: number; pressed: boolean } | null;
  key?: { keyCode: number; control: boolean; alt: boolean; shift: boolean } | null;
  fingerprint: string;
  status: PendingInputStatus;
  receipt?: InputResultPayload;
}
export interface SessionStoreState {
  sessionId: string;
  workerEpoch: number | null;
  sequence: number | null;
  consoleState: ReturnType<typeof createEmptyConsoleState> | null;
  phase: SessionPhase;
  pendingInput: PendingInput | null;
  lastReceipt: InputResultPayload | null;
  clockSample: { serverNowUnixMilliseconds: number; localMonotonicMilliseconds: number } | null;
  fatalRenderError: string | null;
}

export function createSessionStoreState(sessionId: string): SessionStoreState {
  return { sessionId, workerEpoch: null, sequence: null, consoleState: null, phase: "idle", pendingInput: null, lastReceipt: null, clockSample: null, fatalRenderError: null };
}

export function replaceSnapshot(state: SessionStoreState, envelope: { sessionId?: string; workerEpoch?: number; sequence?: number }, payload: RealtimeSnapshotPayload): SessionStoreState {
  assertSession(state, envelope.sessionId);
  if (envelope.workerEpoch !== payload.workerEpoch || envelope.sequence !== payload.snapshotSequence) return { ...state, phase: "resyncing", fatalRenderError: "快照元数据与内容不一致，请重新同步。" };
  if (state.workerEpoch !== null && payload.workerEpoch < state.workerEpoch) return { ...state, phase: "resyncing", fatalRenderError: "收到旧 Worker 的快照，请重新同步。" };
  if (state.workerEpoch === payload.workerEpoch && state.sequence !== null && payload.snapshotSequence < state.sequence) return { ...state, phase: "resyncing", fatalRenderError: "收到旧序号的快照，请重新同步。" };
  const epochChanged = state.workerEpoch !== null && payload.workerEpoch !== state.workerEpoch;
  return {
    ...state,
    workerEpoch: payload.workerEpoch,
    sequence: payload.snapshotSequence,
    consoleState: payload.consoleState,
    phase: "snapshot_ready",
    pendingInput: epochChanged ? null : settlePendingForPrompt(state.pendingInput, payload.consoleState.currentPrompt?.promptId ?? null),
    lastReceipt: null,
    fatalRenderError: null,
  };
}

export function applyBatch(state: SessionStoreState, envelope: { sessionId?: string; workerEpoch?: number; sequence?: number }, payload: RealtimeTransactionBatchPayload): SessionStoreState {
  assertSession(state, envelope.sessionId);
  if (state.phase === "resyncing" || state.phase === "ended" || state.phase === "error") return state;
  if (state.workerEpoch !== payload.workerEpoch || envelope.workerEpoch !== payload.workerEpoch || envelope.sequence !== payload.lastSequence || state.sequence === null || payload.firstSequence !== state.sequence + 1)
    return { ...state, phase: "resyncing" };
  if (!state.consoleState) return { ...state, phase: "resyncing" };
  try {
    const reduced = applyTransactions(state.consoleState, payload.transactions, state.sequence);
    if (reduced.sequence !== payload.lastSequence) throw new ConsoleReductionError("sequence_mismatch", "批次末序号不一致。");
    return { ...state, consoleState: reduced.state, sequence: reduced.sequence, phase: "live", pendingInput: settlePendingForPrompt(state.pendingInput, reduced.state.currentPrompt?.promptId ?? null) };
  } catch {
    return { ...state, phase: "resyncing" };
  }
}

export function beginResume(state: SessionStoreState): SessionStoreState { return { ...state, phase: "resuming", fatalRenderError: null }; }
export function markResync(state: SessionStoreState, reason?: string): SessionStoreState { return { ...state, phase: "resyncing", fatalRenderError: reason ?? null }; }
export function markEnded(state: SessionStoreState): SessionStoreState {
  return { ...state, phase: "ended", pendingInput: state.pendingInput?.status === "pending" ? { ...state.pendingInput, status: "unknown" } : state.pendingInput };
}
export function markForbidden(state: SessionStoreState): SessionStoreState {
  return { ...state, phase: "forbidden", pendingInput: state.pendingInput?.status === "pending" ? { ...state.pendingInput, status: "unknown" } : state.pendingInput };
}

export function createPendingInput(state: SessionStoreState, input: Omit<PendingInput, "fingerprint" | "status">): SessionStoreState {
  const fingerprint = `${input.promptId}\u0000${input.workerEpoch}\u0000${input.source}\u0000${input.value}`;
  return { ...state, pendingInput: { ...input, fingerprint, status: "pending" } };
}

export function markInputUnknown(state: SessionStoreState): SessionStoreState {
  return state.pendingInput ? { ...state, pendingInput: { ...state.pendingInput, status: "unknown" } } : state;
}

export function applyInputReceipt(state: SessionStoreState, receipt: InputResultPayload): SessionStoreState {
  const pending = state.pendingInput;
  if (!pending || pending.clientMessageId !== receipt.clientMessageId || pending.promptId !== receipt.promptId) return { ...state, lastReceipt: receipt };
  const accepted = receipt.status === "ACCEPTED" || receipt.status === "DUPLICATE";
  const stale = receipt.status === "STALE_PROMPT" || receipt.status === "NO_ACTIVE_PROMPT" || receipt.status === "STALE_EPOCH";
  return { ...state, pendingInput: { ...pending, status: accepted ? receipt.status === "ACCEPTED" ? "accepted" : "duplicate" : stale ? "stale" : "rejected", receipt }, lastReceipt: receipt };
}

export function handleServerMessage(state: SessionStoreState, message: RealtimeServerMessage): SessionStoreState {
  switch (message.type) {
    case "session.snapshot": return replaceSnapshot(state, message, message.payload);
    case "display.batch": return applyBatch(state, message, message.payload);
    case "session.input.result": return applyInputReceipt(state, message.payload);
    case "resync.required": return markResync(state, message.payload.reason);
    case "session.stream.ended": return markEnded(state);
    case "protocol.error": return message.payload.code === "AUTHENTICATION_EXPIRED" ? markForbidden(state) : { ...state, phase: "error", fatalRenderError: message.payload.message };
    default: return state;
  }
}

function settlePendingForPrompt(pending: PendingInput | null, promptId: string | null): PendingInput | null {
  if (!pending) return null;
  return pending.promptId === promptId ? pending : { ...pending, status: "stale" };
}

function assertSession(state: SessionStoreState, sessionId?: string): void {
  if (sessionId !== state.sessionId) throw new ConsoleReductionError("session_mismatch", "消息属于其他 Session。");
}

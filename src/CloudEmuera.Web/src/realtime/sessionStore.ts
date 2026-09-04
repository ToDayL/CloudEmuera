import { InputResultPayload, RealtimeDisplayFramePayload, RealtimeServerMessage, RealtimeSnapshotPayload, RealtimeTransaction } from "./protocol";
import { applyTransactions, ConsoleReductionError, createEmptyConsoleState } from "./reducer";

export type SessionPhase = "idle" | "resuming" | "snapshot_ready" | "live" | "resyncing" | "ended" | "forbidden" | "error";
export type PendingInputStatus = "pending" | "unknown" | "accepted" | "duplicate" | "rejected" | "stale";
export interface PendingInput {
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
  committedFrameId: number;
  consoleState: ReturnType<typeof createEmptyConsoleState> | null;
  phase: SessionPhase;
  pendingInput: PendingInput | null;
  lastReceipt: InputResultPayload | null;
  clockSample: { serverNowUnixMilliseconds: number; localMonotonicMilliseconds: number } | null;
  fatalRenderError: string | null;
}

export function createSessionStoreState(sessionId: string): SessionStoreState {
  return { sessionId, workerEpoch: null, sequence: null, committedFrameId: 0, consoleState: null, phase: "idle", pendingInput: null, lastReceipt: null, clockSample: null, fatalRenderError: null };
}

export function replaceSnapshot(state: SessionStoreState, envelope: { sessionId?: string; workerEpoch?: number; sequence?: number }, payload: RealtimeSnapshotPayload): SessionStoreState {
  assertSession(state, envelope.sessionId);
  if (envelope.workerEpoch !== payload.workerEpoch || envelope.sequence !== payload.snapshotSequence) return { ...state, phase: "resyncing", fatalRenderError: "快照元数据与内容不一致，请重新同步。" };
  // A snapshot from an old subscription can still be in flight when a replacement
  // subscription is installed. It is stale data, not a reason to resync again.
  if (state.workerEpoch !== null && payload.workerEpoch < state.workerEpoch) return state;
  if (state.workerEpoch === payload.workerEpoch && state.sequence !== null && payload.snapshotSequence < state.sequence) return state;
  if (state.workerEpoch === payload.workerEpoch && payload.snapshotSequence === state.sequence && payload.committedFrameId < state.committedFrameId) return state;
  if (state.workerEpoch === payload.workerEpoch && state.sequence !== null && payload.snapshotSequence > state.sequence && payload.committedFrameId <= state.committedFrameId)
    return { ...state, phase: "resyncing", fatalRenderError: "快照提交帧号没有前进，请重新同步。" };
  if (state.workerEpoch === payload.workerEpoch && payload.snapshotSequence === state.sequence && payload.committedFrameId === state.committedFrameId) {
    // Resume always establishes a new authoritative transport baseline, even
    // when the Worker produced no output while the browser was away. An
    // equivalent snapshot must finish recovery instead of leaving an
    // otherwise healthy connection permanently non-interactive.
    if (state.phase !== "resuming" && state.phase !== "resyncing") return state;
    return {
      ...state,
      consoleState: payload.consoleState,
      phase: "snapshot_ready",
      lastReceipt: null,
      fatalRenderError: null,
    };
  }
  const epochChanged = state.workerEpoch !== null && payload.workerEpoch !== state.workerEpoch;
  return {
    ...state,
    workerEpoch: payload.workerEpoch,
    sequence: payload.snapshotSequence,
    committedFrameId: payload.committedFrameId,
    consoleState: payload.consoleState,
    phase: "snapshot_ready",
    pendingInput: epochChanged ? null : state.pendingInput,
    lastReceipt: null,
    fatalRenderError: null,
  };
}

export interface LegacyRealtimeTransactionBatchPayload {
  workerEpoch: number;
  firstSequence: number;
  lastSequence: number;
  transactions: RealtimeTransaction[];
}

export function applyBatch(state: SessionStoreState, envelope: { sessionId?: string; workerEpoch?: number; sequence?: number }, payload: LegacyRealtimeTransactionBatchPayload): SessionStoreState {
  assertSession(state, envelope.sessionId);
  if (state.phase === "resyncing" || state.phase === "ended" || state.phase === "error") return state;
  if (state.workerEpoch !== null && payload.workerEpoch < state.workerEpoch) return state;
  if (state.workerEpoch !== payload.workerEpoch || envelope.workerEpoch !== payload.workerEpoch || envelope.sequence !== payload.lastSequence || state.sequence === null)
    return { ...state, phase: "resyncing" };

  // Duplicate and stale batches are expected at a reconnect boundary and must be
  // fenced without disturbing the already rendered state. A batch that overlaps
  // the local tail while extending beyond it is not safe to apply: it indicates
  // reordering or a broken replay boundary and requires a complete snapshot.
  if (payload.lastSequence <= state.sequence) return state;
  if (payload.firstSequence <= state.sequence || payload.firstSequence !== state.sequence + 1)
    return { ...state, phase: "resyncing" };
  if (!state.consoleState) return { ...state, phase: "resyncing" };
  try {
    const reduced = applyTransactions(state.consoleState, payload.transactions, state.sequence);
    if (reduced.sequence !== payload.lastSequence) throw new ConsoleReductionError("sequence_mismatch", "批次末序号不一致。");
    return { ...state, consoleState: reduced.state, sequence: reduced.sequence, phase: "live" };
  } catch {
    return { ...state, phase: "resyncing" };
  }
}

export function applyDisplayFrame(state: SessionStoreState, envelope: { sessionId?: string; workerEpoch?: number; sequence?: number }, payload: RealtimeDisplayFramePayload): SessionStoreState {
  assertSession(state, envelope.sessionId);
  if (state.phase === "resyncing" || state.phase === "ended" || state.phase === "error") return state;
  if (state.workerEpoch !== null && payload.workerEpoch < state.workerEpoch) return state;
  if (state.workerEpoch !== payload.workerEpoch || envelope.workerEpoch !== payload.workerEpoch || envelope.sequence !== payload.commitSequence)
    return { ...state, phase: "resyncing" };

  if (state.workerEpoch === payload.workerEpoch && payload.frameId <= state.committedFrameId) return state;
  if (state.workerEpoch === payload.workerEpoch && state.committedFrameId !== 0 && payload.frameId !== state.committedFrameId + 1)
    return { ...state, phase: "resyncing" };

  if (payload.requiresSnapshot) {
    if (payload.consoleState === null || payload.transactions.length !== 0) return { ...state, phase: "resyncing" };
    if (payload.reason === "WAITING_FOR_INPUT" && payload.consoleState.currentPrompt === null)
      return { ...state, phase: "resyncing" };
    if ((payload.reason === "RUNTIME_COMPLETED" || payload.reason === "RUNTIME_FAILED") && payload.consoleState.currentPrompt !== null)
      return { ...state, phase: "resyncing" };
    return {
      ...state,
      workerEpoch: payload.workerEpoch,
      sequence: payload.commitSequence,
      committedFrameId: payload.frameId,
      consoleState: payload.consoleState,
      phase: "live",
      pendingInput: state.workerEpoch === payload.workerEpoch ? state.pendingInput : null,
      fatalRenderError: null,
    };
  }

  if (payload.consoleState !== null || payload.transactions.length === 0 || state.consoleState === null || state.sequence === null)
    return { ...state, phase: "resyncing" };
  if (payload.transactions[0].sequence !== state.sequence + 1 || payload.transactions.at(-1)?.sequence !== payload.commitSequence)
    return { ...state, phase: "resyncing" };
  try {
    const reduced = applyTransactions(state.consoleState, payload.transactions, state.sequence);
    if (reduced.sequence !== payload.commitSequence) throw new ConsoleReductionError("sequence_mismatch", "显示帧末序号不一致。");
    if (payload.reason === "WAITING_FOR_INPUT" && reduced.state.currentPrompt === null)
      return { ...state, phase: "resyncing" };
    if ((payload.reason === "RUNTIME_COMPLETED" || payload.reason === "RUNTIME_FAILED") && reduced.state.currentPrompt !== null)
      return { ...state, phase: "resyncing" };
    return { ...state, consoleState: reduced.state, sequence: reduced.sequence, committedFrameId: payload.frameId, phase: "live", fatalRenderError: null };
  } catch {
    return { ...state, phase: "resyncing" };
  }
}

export function beginResume(state: SessionStoreState): SessionStoreState { return { ...state, phase: "resuming", fatalRenderError: null }; }
/** A resync is a recoverable transport state; invariant/protocol failures use the fatal field. */
export function markResync(state: SessionStoreState): SessionStoreState { return { ...state, phase: "resyncing", fatalRenderError: null }; }
export function markEnded(state: SessionStoreState): SessionStoreState {
  return { ...state, phase: "ended", pendingInput: state.pendingInput?.status === "pending" ? { ...state.pendingInput, status: "unknown" } : state.pendingInput };
}
export function markForbidden(state: SessionStoreState): SessionStoreState {
  return { ...state, phase: "forbidden", pendingInput: state.pendingInput?.status === "pending" ? { ...state.pendingInput, status: "unknown" } : state.pendingInput };
}

export function createPendingInput(state: SessionStoreState, input: Omit<PendingInput, "fingerprint" | "status">): SessionStoreState {
  const fingerprint = `${input.workerEpoch}\u0000${input.source}\u0000${input.value}`;
  return { ...state, pendingInput: { ...input, fingerprint, status: "pending" } };
}

export function markInputUnknown(state: SessionStoreState): SessionStoreState {
  return state.pendingInput?.status === "pending"
    ? { ...state, pendingInput: { ...state.pendingInput, status: "unknown" } }
    : state;
}

export function applyInputReceipt(state: SessionStoreState, envelope: { workerEpoch?: number }, receipt: InputResultPayload): SessionStoreState {
  const pending = state.pendingInput;
  // A receipt is meaningful only for the exact Worker epoch that accepted the
  // browser attempt. A delayed old-epoch receipt must not mutate a replacement
  // session view or its pending input.
  if (!pending || pending.workerEpoch !== envelope.workerEpoch || pending.clientMessageId !== receipt.clientMessageId) return state;
  const accepted = receipt.status === "ACCEPTED" || receipt.status === "DUPLICATE";
  const stale = receipt.status === "NO_ACTIVE_PROMPT" || receipt.status === "STALE_EPOCH";
  return { ...state, pendingInput: { ...pending, status: accepted ? receipt.status === "ACCEPTED" ? "accepted" : "duplicate" : stale ? "stale" : "rejected", receipt }, lastReceipt: receipt };
}

export function handleServerMessage(state: SessionStoreState, message: RealtimeServerMessage): SessionStoreState {
  switch (message.type) {
    case "session.snapshot": return replaceSnapshot(state, message, message.payload);
    case "display.frame": return applyDisplayFrame(state, message, message.payload);
    case "session.input.result": return applyInputReceipt(state, message, message.payload);
    case "resync.required": return markResync(state);
    case "session.stream.ended": return markEnded(state);
    case "protocol.error": return message.payload.code === "AUTHENTICATION_EXPIRED" ? markForbidden(state) : { ...state, phase: "error", fatalRenderError: message.payload.message };
    default: return state;
  }
}

function assertSession(state: SessionStoreState, sessionId?: string): void {
  if (sessionId !== state.sessionId) throw new ConsoleReductionError("session_mismatch", "消息属于其他 Session。");
}

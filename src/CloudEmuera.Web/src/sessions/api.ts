import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, apiRequest, apiRequestWithMeta, getCsrfToken, newIdempotencyKey } from "../api";
import type { SessionGameSummaryDto, SessionListResponseDto, SessionResponseDto, SessionStateDto } from "../api/generated";

export type SessionState = SessionStateDto;
export type SessionGameSummary = SessionGameSummaryDto;
export type SessionView = SessionResponseDto;
export type SessionListResponse = SessionListResponseDto;

export interface SessionListQuery {
  gameId?: string;
  state?: SessionState;
  cursor?: string;
  limit?: number;
}

export interface SessionTextMetrics { halfWidthPx: number; fullWidthPx: number }

const consoleFontFamily = '"SFMono-Regular", Consolas, "Liberation Mono", "Noto Sans Mono CJK SC", monospace';

export function measureSessionTextMetrics(fontSize: number): SessionTextMetrics {
  const canvas = typeof document === "undefined" ? null : document.createElement("canvas");
  const context = canvas?.getContext("2d");
  if (!context) return { halfWidthPx: fontSize * 0.5, fullWidthPx: fontSize };
  context.font = `${fontSize}px ${consoleFontFamily}`;
  return { halfWidthPx: Math.max(0.1, context.measureText("A").width), fullWidthPx: Math.max(0.1, context.measureText("汉").width) };
}

function jsonMutationHeaders(token: string, key: string): Record<string, string> {
  return {
    "Content-Type": "application/json",
    "X-CSRF-TOKEN": token,
    "Idempotency-Key": key,
  };
}

function queryString(query: SessionListQuery): string {
  const params = new URLSearchParams();
  if (query.gameId) params.set("gameId", query.gameId);
  if (query.state) params.set("state", query.state);
  if (query.cursor) params.set("cursor", query.cursor);
  if (query.limit) params.set("limit", String(query.limit));
  const value = params.toString();
  return value ? `?${value}` : "";
}

export async function listSessions(query: SessionListQuery = {}): Promise<SessionListResponse> {
  return apiRequest<SessionListResponse>(`/sessions${queryString(query)}`);
}

export async function getSession(sessionId: string): Promise<SessionView> {
  return apiRequest<SessionView>(`/sessions/${encodeURIComponent(sessionId)}`);
}

export async function createSession(gameId: string, name: string, fontSize = 16, lineHeight = 16, idempotencyKey = newIdempotencyKey()): Promise<SessionView> {
  const token = await getCsrfToken();
  return (await apiRequestWithMeta<SessionView>("/sessions", {
    method: "POST",
    headers: jsonMutationHeaders(token, idempotencyKey),
    body: JSON.stringify({ gameId, name, fontSize, lineHeight }),
  })).value;
}

export async function openSession(sessionId: string, browserWidth = typeof window === "undefined" ? 0 : Math.round(window.innerWidth), idempotencyKey = newIdempotencyKey()): Promise<SessionView> {
  const session = await getSession(sessionId);
  return lifecycleRequest(sessionId, "open", browserWidth, measureSessionTextMetrics(session.fontSize), idempotencyKey);
}

export async function updateSessionConfiguration(sessionId: string, name: string, fontSize: number, lineHeight: number, idempotencyKey = newIdempotencyKey()): Promise<SessionView> {
  const token = await getCsrfToken();
  return (await apiRequestWithMeta<SessionView>(`/sessions/${encodeURIComponent(sessionId)}/configuration`, { method: "PUT", headers: jsonMutationHeaders(token, idempotencyKey), body: JSON.stringify({ name, fontSize, lineHeight }) })).value;
}

export async function closeSession(sessionId: string, idempotencyKey = newIdempotencyKey()): Promise<SessionView> {
  return lifecycleRequest(sessionId, "close", 0, null, idempotencyKey);
}

export async function deleteSession(sessionId: string, idempotencyKey = newIdempotencyKey()): Promise<{ pending: boolean }> {
  const token = await getCsrfToken();
  const response = await apiRequestWithMeta<{ pending?: boolean }>(`/sessions/${encodeURIComponent(sessionId)}`, {
    method: "DELETE",
    headers: jsonMutationHeaders(token, idempotencyKey),
  });
  return { pending: response.value?.pending === true };
}

export async function waitForSessionDeletion(
  sessionId: string,
  options: { signal?: AbortSignal; attempts?: number } = {},
): Promise<void> {
  const attempts = options.attempts ?? 60;
  let delayMilliseconds = 150;
  for (let attempt = 0; attempt < attempts; attempt++) {
    options.signal?.throwIfAborted();
    try {
      await getSession(sessionId);
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 404) return;
      throw cause;
    }
    if (attempt + 1 < attempts) {
      await wait(delayMilliseconds, options.signal);
      delayMilliseconds = Math.min(1_000, Math.round(delayMilliseconds * 1.35));
    }
  }
  throw new Error("Session 删除在限定时间内没有完成。");
}

async function lifecycleRequest(sessionId: string, operation: "open" | "close", browserWidth: number, textMetrics: SessionTextMetrics | null, idempotencyKey: string): Promise<SessionView> {
  const token = await getCsrfToken();
  return (await apiRequestWithMeta<SessionView>(`/sessions/${encodeURIComponent(sessionId)}:${operation}`, {
    method: "POST",
    headers: jsonMutationHeaders(token, idempotencyKey),
    body: operation === "open" ? JSON.stringify({ browserWidth, textMetrics }) : "{}",
  })).value;
}

export async function waitForSession(
  sessionId: string,
  expected: ReadonlySet<SessionState>,
  options: { signal?: AbortSignal; attempts?: number } = {},
): Promise<SessionView> {
  const attempts = options.attempts ?? 60;
  let delayMilliseconds = 150;
  for (let attempt = 0; attempt < attempts; attempt++) {
    options.signal?.throwIfAborted();
    const session = await getSession(sessionId);
    if (expected.has(session.state)) return session;
    if (attempt + 1 < attempts) {
      await wait(delayMilliseconds, options.signal);
      delayMilliseconds = Math.min(1_000, Math.round(delayMilliseconds * 1.35));
    }
  }
  throw new Error("Session 状态在限定时间内没有完成变更。");
}

function wait(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason ?? new DOMException("操作已取消。", "AbortError"));
      return;
    }
    const timer = window.setTimeout(resolve, milliseconds);
    signal?.addEventListener("abort", () => {
      window.clearTimeout(timer);
      reject(signal.reason ?? new DOMException("操作已取消。", "AbortError"));
    }, { once: true });
  });
}

export function useSessionList(query: SessionListQuery = {}) {
  return useQuery({
    queryKey: ["sessions", query],
    queryFn: () => listSessions(query),
    staleTime: 2_000,
    refetchOnWindowFocus: true,
  });
}

export function useSession(sessionId: string | undefined) {
  return useQuery({
    queryKey: ["session", sessionId],
    queryFn: () => getSession(sessionId!),
    enabled: Boolean(sessionId),
    staleTime: 1_000,
    refetchOnWindowFocus: true,
  });
}

export function useInvalidateSessions() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ["sessions"] });
}

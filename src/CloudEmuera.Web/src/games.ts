import { ApiError, apiRequest, getCsrfToken, newIdempotencyKey } from "./api";

export type GameVisibility = "PRIVATE" | "SERVER_SHARED";
export type GameStatus = "ACTIVE" | "BLOCKED" | "DELETED";
export type WorkspaceStatus = "NONE" | "DRAFT" | "VALIDATING";
export type ContentScope = "WORKSPACE" | "CURRENT";

export interface GameLibraryItem {
  id: string;
  name: string;
  visibility: GameVisibility;
  status: GameStatus;
  workspaceStatus: WorkspaceStatus;
  hasCurrentContent: boolean;
  contentDigest: string | null;
  contentRevision: number;
  stateVersion: number;
  createdAt: string;
  updatedAt: string;
}

export interface GameUploadProgress {
  gameId: string;
  operationId: string;
  status: "PENDING" | "RUNNING" | "CONTENT_READY" | "COMMITTED" | "FAILED";
  stage: string;
  currentItem: string | null;
  errorCode: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
}

export interface GameUploadOptions {
  signal?: AbortSignal;
  onRequestId?: (requestId: string) => void;
  onUploadProgress?: (loadedBytes: number, totalBytes: number) => void;
}

export interface GameFileItem {
  path: string;
  isDirectory: boolean;
  bytes: number;
  etag?: string | null;
}

export interface GameTextFile {
  path: string;
  content: string;
  encoding: string;
  hasBom: boolean;
  bytes: number;
  etag: string;
  stateVersion: number;
}

export interface GameDiagnosticItem {
  id: string;
  code: string;
  severity: string;
  path: string | null;
  message: string;
  messageKey: string;
  activationBlocking: boolean;
  overridePolicy: string;
  overriddenBy: string | null;
  overriddenAt: string | null;
}

export interface GameListResponse {
  items: GameLibraryItem[];
}

function mutationHeaders(token: string, extra?: Record<string, string>): Record<string, string> {
  return { "Content-Type": "application/json", "X-CSRF-TOKEN": token, ...extra };
}

function stateVersionHeader(stateVersion: number): Record<string, string> {
  return { "X-Game-State-Version": String(stateVersion) };
}

function scopeQuery(scope?: ContentScope | null, path?: string | null): string {
  const params = new URLSearchParams();
  if (scope) params.set("scope", scope);
  if (path) params.set("path", path);
  const query = params.toString();
  return query ? `?${query}` : "";
}

export async function listGames(): Promise<GameLibraryItem[]> {
  const page = await apiRequest<GameListResponse>("/games");
  return page.items;
}

export async function getGame(gameId: string): Promise<GameLibraryItem> {
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}`);
}

export async function updateGame(gameId: string, stateVersion: number, input: { name?: string; visibility?: GameVisibility }): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}`, {
    method: "PATCH",
    headers: mutationHeaders(token, stateVersionHeader(stateVersion)),
    body: JSON.stringify(input),
  });
}

export async function deleteGame(gameId: string, stateVersion: number): Promise<void> {
  const token = await getCsrfToken();
  return apiRequest<void>(`/games/${encodeURIComponent(gameId)}`, {
    method: "DELETE",
    headers: mutationHeaders(token, stateVersionHeader(stateVersion)),
  });
}

export async function setGameBlocked(gameId: string, stateVersion: number, blocked: boolean): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>(`/admin/games/${encodeURIComponent(gameId)}:block`, {
    method: "POST",
    headers: mutationHeaders(token, stateVersionHeader(stateVersion)),
    body: JSON.stringify({ blocked }),
  });
}

export async function uploadGame(name: string, visibility: GameVisibility, file: File, options: GameUploadOptions = {}): Promise<GameLibraryItem> {
  const idempotencyKey = newIdempotencyKey();
  options.onRequestId?.(idempotencyKey);
  const token = await getCsrfToken();
  const query = new URLSearchParams({ name, visibility });
  if (options.signal?.aborted) throw options.signal.reason ?? new DOMException("上传已取消。", "AbortError");

  return new Promise<GameLibraryItem>((resolve, reject) => {
    const request = new XMLHttpRequest();
    let settled = false;
    const abort = () => {
      if (request.readyState !== XMLHttpRequest.DONE) request.abort();
    };
    const cleanup = () => options.signal?.removeEventListener("abort", abort);
    const finish = (callback: () => void) => {
      if (settled) return;
      settled = true;
      cleanup();
      callback();
    };
    request.open("POST", `/api/v1/games?${query.toString()}`);
    request.withCredentials = true;
    request.setRequestHeader("X-CSRF-TOKEN", token);
    request.setRequestHeader("Idempotency-Key", idempotencyKey);
    request.setRequestHeader("Content-Type", "application/zip");
    request.upload.addEventListener("progress", event => {
      if (event.lengthComputable) options.onUploadProgress?.(event.loaded, event.total);
    });
    request.addEventListener("load", () => finish(() => {
      let body: GameLibraryItem | { message?: string; code?: string; requestId?: string } | null;
      try {
        body = request.responseText ? JSON.parse(request.responseText) as GameLibraryItem | { message?: string; code?: string; requestId?: string } : null;
      } catch {
        reject(new ApiError("服务器返回了无法识别的响应。", "INVALID_RESPONSE", request.status));
        return;
      }
      if (request.status >= 200 && request.status < 300) {
        resolve(body as GameLibraryItem);
        return;
      }
      const error = body as { message?: string; code?: string; requestId?: string } | null;
      reject(new ApiError(error?.message ?? "上传失败。", error?.code ?? "REQUEST_FAILED", request.status, error?.requestId));
    }));
    request.addEventListener("error", () => finish(() => reject(new Error("网络错误，上传未能完成。"))));
    request.addEventListener("abort", () => finish(() => reject(options.signal?.reason ?? new DOMException("上传已取消。", "AbortError"))));
    options.signal?.addEventListener("abort", abort, { once: true });
    request.send(file);
  });
}

export async function getGameUploadProgress(requestId: string): Promise<GameUploadProgress> {
  return apiRequest<GameUploadProgress>(`/games/uploads/${encodeURIComponent(requestId)}`);
}

export async function listFiles(gameId: string, scope: ContentScope, path?: string | null): Promise<GameFileItem[]> {
  const page = await apiRequest<{ items: GameFileItem[] }>(`/games/${encodeURIComponent(gameId)}/files${scopeQuery(scope, path)}`);
  return page.items;
}

export async function readTextFile(gameId: string, scope: ContentScope, path: string): Promise<GameTextFile> {
  return apiRequest<GameTextFile>(`/games/${encodeURIComponent(gameId)}/file${scopeQuery(scope, path)}`);
}

export async function listDiagnostics(gameId: string): Promise<GameDiagnosticItem[]> {
  const page = await apiRequest<{ items: GameDiagnosticItem[] }>(`/games/${encodeURIComponent(gameId)}/diagnostics`);
  return page.items;
}


export function downloadFileUrl(gameId: string, scope: ContentScope, path: string): string {
  const params = new URLSearchParams({ scope, path });
  return `/api/v1/games/${encodeURIComponent(gameId)}/download?${params.toString()}`;
}

export function shortDigest(digest: string | null | undefined): string {
  if (!digest) return "";
  const value = digest.startsWith("sha256:") ? digest.slice(7) : digest;
  return value.length > 16 ? `${value.slice(0, 8)}…${value.slice(-4)}` : value;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function formatDateTime(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString("zh-CN", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

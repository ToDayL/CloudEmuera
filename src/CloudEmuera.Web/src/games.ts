import { apiRequest, getCsrfToken, newIdempotencyKey } from "./api";

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

export async function uploadGame(name: string, visibility: GameVisibility, file: File): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  const query = new URLSearchParams({ name, visibility });
  return apiRequest<GameLibraryItem>(`/games?${query.toString()}`, {
    method: "POST",
    headers: {
      "X-CSRF-TOKEN": token,
      "Idempotency-Key": newIdempotencyKey(),
      "Content-Type": "application/zip",
    },
    body: file,
  });
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

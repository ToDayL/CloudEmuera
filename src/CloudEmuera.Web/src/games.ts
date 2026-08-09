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

export interface GameSearchMatch {
  path: string;
  line: number;
  column: number;
  preview: string;
}

export interface GameSearchPage {
  items: GameSearchMatch[];
  nextCursor: string | null;
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

export interface GameValidationDiagnostic {
  code: string;
  severity: string;
  path: string | null;
  message: string;
  activationBlocking: boolean;
}

export interface GameValidationResult {
  canActivate: boolean;
  contentDigest: string;
  fileCount: number;
  totalBytes: number;
  diagnostics: GameValidationDiagnostic[];
  stateVersion: number;
}

export type ContentOperationType = "IMPORT" | "RESET_WORKSPACE" | "VALIDATE" | "ACTIVATE";
export type ContentOperationStatus = "PENDING" | "RUNNING" | "CONTENT_READY" | "COMMITTED" | "FAILED";

export interface GameContentOperationItem {
  id: string;
  type: ContentOperationType;
  status: ContentOperationStatus;
  contentDigest: string | null;
  errorCode: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
}

// Game package ingestion (P1-03 manifest). Enum values are serialized as numbers
// by the ASP.NET Core default JSON writer.
export const GamePackageFileKind = { Binary: 0, Text: 1 } as const;
export const GamePackageTextEncoding = { None: 0, Utf8: 1, Utf8Bom: 2, ShiftJis: 3, Unknown: 4 } as const;
export const GamePackageSeverity = { Info: 0, Warning: 1, Error: 2 } as const;

export interface GamePackageFileManifest {
  path: string;
  bytes: number;
  digest: string;
  kind: number;
  encoding: number;
  hasBom: boolean;
}

export interface GamePackageDiagnostic {
  code: string;
  severity: number;
  stage: string;
  logicalPath: string | null;
  messageKey: string;
  arguments: Record<string, string>;
  publishBlocking: boolean;
  suppressedCount: number;
}

export interface GamePackageManifest {
  schemaVersion: number;
  archiveBytes: number;
  archiveDigest: string;
  contentBytes: number;
  fileCount: number;
  directoryCount: number;
  contentDigest: string;
  files: GamePackageFileManifest[];
  directories: string[];
  diagnostics: GamePackageDiagnostic[];
}

export interface IngestedGamePackage {
  ingestionId: string;
  ownerUserId: string;
  expiresAt: string;
  manifest: GamePackageManifest;
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

function fileETagHeader(etag: string): Record<string, string> {
  return { "X-File-If-Match": `"${etag}"` };
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

export async function createGame(name: string, visibility: GameVisibility): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>("/games", {
    method: "POST",
    headers: mutationHeaders(token),
    body: JSON.stringify({ name, visibility }),
  });
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

export async function ingestGamePackage(file: File): Promise<IngestedGamePackage> {
  const token = await getCsrfToken();
  return apiRequest<IngestedGamePackage>("/game-package-ingestions", {
    method: "POST",
    headers: {
      "X-CSRF-TOKEN": token,
      "Idempotency-Key": newIdempotencyKey(),
      "Content-Type": "application/zip",
    },
    body: file,
  });
}

export async function bindGamePackage(gameId: string, stateVersion: number, ingestionId: string, contentDigest: string): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}/package`, {
    method: "PUT",
    headers: mutationHeaders(token, {
      ...stateVersionHeader(stateVersion),
      "Idempotency-Key": newIdempotencyKey(),
    }),
    body: JSON.stringify({ ingestionId, contentDigest }),
  });
}

export async function startEditing(gameId: string, stateVersion: number): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}:edit`, {
    method: "POST",
    headers: mutationHeaders(token, stateVersionHeader(stateVersion)),
  });
}

export async function discardWorkspace(gameId: string, stateVersion: number): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}/workspace`, {
    method: "DELETE",
    headers: mutationHeaders(token, stateVersionHeader(stateVersion)),
  });
}

export async function listFiles(gameId: string, scope: ContentScope, path?: string | null): Promise<GameFileItem[]> {
  const page = await apiRequest<{ items: GameFileItem[] }>(`/games/${encodeURIComponent(gameId)}/files${scopeQuery(scope, path)}`);
  return page.items;
}

export async function readTextFile(gameId: string, scope: ContentScope, path: string): Promise<GameTextFile> {
  return apiRequest<GameTextFile>(`/games/${encodeURIComponent(gameId)}/file${scopeQuery(scope, path)}`);
}

export async function writeTextFile(
  gameId: string,
  path: string,
  content: string,
  stateVersion: number,
  fileETag?: string | null,
  requireAbsent = false,
): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  const precondition = requireAbsent ? { "If-None-Match": "*" } : fileETag ? fileETagHeader(fileETag) : {};
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}/file?path=${encodeURIComponent(path)}`, {
    method: "PUT",
    headers: mutationHeaders(token, { ...stateVersionHeader(stateVersion), ...precondition }),
    body: JSON.stringify({ content }),
  });
}

export async function deletePath(gameId: string, path: string, stateVersion: number): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}/file?path=${encodeURIComponent(path)}`, {
    method: "DELETE",
    headers: mutationHeaders(token, stateVersionHeader(stateVersion)),
  });
}

export async function searchFiles(
  gameId: string,
  scope: ContentScope,
  query: string,
  cursor?: string | null,
  limit = 100,
): Promise<GameSearchPage> {
  const params = new URLSearchParams({ scope, q: query, limit: String(limit) });
  if (cursor) params.set("cursor", cursor);
  return apiRequest<GameSearchPage>(`/games/${encodeURIComponent(gameId)}/search?${params.toString()}`);
}

export async function validateGame(gameId: string, stateVersion: number): Promise<GameValidationResult> {
  const token = await getCsrfToken();
  return apiRequest<GameValidationResult>(`/games/${encodeURIComponent(gameId)}:validate`, {
    method: "POST",
    headers: mutationHeaders(token, {
      ...stateVersionHeader(stateVersion),
      "Idempotency-Key": newIdempotencyKey(),
    }),
  });
}

export async function activateGame(gameId: string, stateVersion: number): Promise<GameLibraryItem> {
  const token = await getCsrfToken();
  return apiRequest<GameLibraryItem>(`/games/${encodeURIComponent(gameId)}:activate`, {
    method: "POST",
    headers: mutationHeaders(token, {
      ...stateVersionHeader(stateVersion),
      "Idempotency-Key": newIdempotencyKey(),
    }),
  });
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

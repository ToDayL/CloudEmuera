/*
 * GENERATED CONTRACT SNAPSHOT — do not add UI behavior here.
 *
 * Source: /openapi/v1.json exposed by CloudEmuera.Api.
 * Regenerate: node scripts/generate-api-types.mjs
 */

export type SessionStateDto = "CREATING" | "STARTING" | "RUNNING" | "STOPPING" | "CLOSED" | "CRASHED";

export type RuntimeWidthModeDto = "ORIGIN" | "MAX" | "CUSTOM";

export type SaveLayoutDto = "ROOT" | "SAV_DIRECTORY";

export interface SessionGameSummaryDto {
  id: string;
  name: string;
}

export interface SessionResponseDto {
  schemaVersion: number;
  id: string;
  name: string;
  game: SessionGameSummaryDto;
  sourceContentDigest: string;
  sourceContentRevision: number;
  runtimeVersion: string;
  fontFaceId: string;
  fontSize: number;
  lineHeight: number;
  widthMode: RuntimeWidthModeDto;
  customWidth: number | null;
  convertBackslashToYen: boolean;
  state: SessionStateDto;
  stateVersion: number;
  workerEpoch: number;
  waitingForInput: boolean;
  createdAt: string;
  startedAt: string | null;
  lastActivityAt: string;
  closedAt: string | null;
  closeReason: string | null;
}

export interface SessionListResponseDto {
  items: SessionResponseDto[];
  nextCursor: string | null;
}

export interface SaveItemResponseDto {
  path: string;
  kind: string;
  sizeBytes: number;
  modifiedAt: string;
}

export interface SaveListResponseDto {
  schemaVersion: number;
  layout: SaveLayoutDto;
  items: SaveItemResponseDto[];
}

export interface SessionPresentationAssetDto {
  assetId: string;
  mediaType: string;
  byteLength: number;
  contentDigest: string;
  eTag?: string | null;
}

export interface SessionPresentationFontDto {
  family: string;
  assetId: string;
  fallback: string;
  cssFamily: string;
  aliases: string[];
}

export interface SessionPresentationManifestDto {
  schemaVersion: number;
  assets: SessionPresentationAssetDto[];
  fonts: SessionPresentationFontDto[];
  fontDiagnostics: string[];
}

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, apiRequest, getCsrfToken, newIdempotencyKey } from "../api";
import type { SessionState } from "../sessions/api";
import type { SaveItemResponseDto, SaveLayoutDto, SaveListResponseDto } from "../api/generated";

export type SaveLayout = SaveLayoutDto;
export type SaveItem = SaveItemResponseDto;
export type SaveListResponse = SaveListResponseDto;

function pathPart(path: string): string {
  return path.split("/").map(segment => encodeURIComponent(segment)).join("/");
}

export function saveDownloadUrl(sessionId: string, path: string): string {
  return `/api/v1/sessions/${encodeURIComponent(sessionId)}/saves/${pathPart(path)}`;
}

export async function listSaves(sessionId: string): Promise<SaveListResponse> {
  return apiRequest<SaveListResponse>(`/sessions/${encodeURIComponent(sessionId)}/saves`);
}

export interface SaveUploadOptions {
  signal?: AbortSignal;
  onProgress?: (loadedBytes: number, totalBytes: number) => void;
}

export async function importSave(
  sessionId: string,
  targetPath: string,
  file: File,
  options: SaveUploadOptions = {},
): Promise<SaveItem | null> {
  const token = await getCsrfToken();
  const path = `/api/v1/sessions/${encodeURIComponent(sessionId)}/saves/${pathPart(targetPath)}`;
  const idempotencyKey = newIdempotencyKey();

  return new Promise<SaveItem | null>((resolve, reject) => {
    const request = new XMLHttpRequest();
    let settled = false;
    const abort = () => {
      if (request.readyState !== XMLHttpRequest.DONE) request.abort();
    };
    const cleanup = () => {
      options.signal?.removeEventListener("abort", abort);
    };
    const finish = (callback: () => void) => {
      if (settled) return;
      settled = true;
      cleanup();
      callback();
    };
    if (options.signal?.aborted) {
      reject(options.signal.reason ?? new DOMException("操作已取消。", "AbortError"));
      return;
    }

    request.open("PUT", path);
    request.withCredentials = true;
    request.setRequestHeader("X-CSRF-TOKEN", token);
    request.setRequestHeader("Idempotency-Key", idempotencyKey);
    request.setRequestHeader("Content-Type", file.type || "application/octet-stream");
    request.upload.addEventListener("progress", event => {
      if (event.lengthComputable) options.onProgress?.(event.loaded, event.total);
    });
    request.addEventListener("load", () => {
      finish(() => {
        let body: SaveItem | null | { message?: string; code?: string; requestId?: string };
        try {
          body = request.responseText ? JSON.parse(request.responseText) as SaveItem | null | { message?: string; code?: string; requestId?: string } : null;
        } catch {
          reject(new ApiError("服务器返回了无法识别的响应。", "INVALID_RESPONSE", request.status));
          return;
        }
        if (request.status >= 200 && request.status < 300) {
          resolve(body as SaveItem | null);
          return;
        }
        const error = body as { message?: string; code?: string; requestId?: string } | null;
        reject(new ApiError(error?.message ?? "请求失败。", error?.code ?? "REQUEST_FAILED", request.status, error?.requestId));
      });
    });
    request.addEventListener("error", () => finish(() => reject(new Error("网络错误，上传未能完成。"))));
    request.addEventListener("abort", () => finish(() => reject(new DOMException("上传等待已取消。", "AbortError"))));
    options.signal?.addEventListener("abort", abort, { once: true });
    if (options.signal?.aborted) {
      request.abort();
      return;
    }
    request.send(file);
  });
}

export async function renameSave(sessionId: string, sourcePath: string, targetPath: string): Promise<SaveItem | null> {
  const token = await getCsrfToken();
  return apiRequest<SaveItem | null>(`/sessions/${encodeURIComponent(sessionId)}/saves/${pathPart(sourcePath)}`, {
    method: "PATCH",
    headers: {
      "Content-Type": "application/json",
      "X-CSRF-TOKEN": token,
      "Idempotency-Key": newIdempotencyKey(),
    },
    body: JSON.stringify({ targetPath }),
  });
}

export async function deleteSave(sessionId: string, path: string): Promise<void> {
  const token = await getCsrfToken();
  await apiRequest<void>(`/sessions/${encodeURIComponent(sessionId)}/saves/${pathPart(path)}`, {
    method: "DELETE",
    headers: {
      "X-CSRF-TOKEN": token,
      "Idempotency-Key": newIdempotencyKey(),
      "X-Confirm-Delete": "true",
    },
  });
}

export function useSaves(sessionId: string | undefined) {
  return useQuery({
    queryKey: ["saves", sessionId],
    queryFn: () => listSaves(sessionId!),
    enabled: Boolean(sessionId),
    staleTime: 1_000,
    refetchOnWindowFocus: true,
  });
}

export function useInvalidateSaves(sessionId: string | undefined) {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ["saves", sessionId] });
}

export function canMutateSaves(state: SessionState | undefined): boolean {
  return state === "CLOSED" || state === "CRASHED";
}

export function saveKindLabel(kind: string): string {
  switch (kind) {
    case "GLOBAL": return "全局数据";
    case "AUXILIARY_TEXT": return "辅助文本";
    case "AUXILIARY_IMAGE": return "辅助图片";
    default: return "原生存档";
  }
}

export class ApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly requestId?: string;

  constructor(message: string, code: string, status: number, requestId?: string) {
    super(message);
    this.name = "ApiError";
    this.code = code;
    this.status = status;
    this.requestId = requestId;
  }
}

export interface ApiResponse<T> {
  value: T;
  response: Response;
}

async function readApiError(response: Response): Promise<never> {
  const body = await response.json().catch(() => ({})) as { message?: string; code?: string; requestId?: string };
  throw new ApiError(
    body.message ?? "请求失败。",
    body.code ?? "REQUEST_FAILED",
    response.status,
    body.requestId,
  );
}

export async function apiRequestWithMeta<T>(path: string, init?: RequestInit): Promise<ApiResponse<T>> {
  const response = await fetch(`/api/v1${path}`, { credentials: "same-origin", ...init });
  if (!response.ok) await readApiError(response);
  const value = response.status === 204 ? undefined as T : await response.json() as T;
  return { value, response };
}

export async function apiRequest<T>(path: string, init?: RequestInit): Promise<T> {
  return (await apiRequestWithMeta<T>(path, init)).value;
}

export async function getCsrfToken(): Promise<string> {
  return (await apiRequest<{ token: string }>("/auth/csrf")).token;
}

export function newIdempotencyKey(): string {
  const random = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  return `web-${random}`;
}

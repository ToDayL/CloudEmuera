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

export async function apiRequest<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api/v1${path}`, { credentials: "same-origin", ...init });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new ApiError(
      body.message ?? "请求失败。",
      body.code ?? "REQUEST_FAILED",
      response.status,
      body.requestId,
    );
  }
  return response.status === 204 ? (undefined as T) : (response.json() as Promise<T>);
}

export async function getCsrfToken(): Promise<string> {
  return (await apiRequest<{ token: string }>("/auth/csrf")).token;
}

export function newIdempotencyKey(): string {
  const random = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  return `web-${random}`;
}

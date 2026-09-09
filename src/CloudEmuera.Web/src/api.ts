import i18n from "./i18n";

export class ApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly requestId?: string;

  readonly serverMessage: string;

  constructor(message: string, code: string, status: number, requestId?: string) {
    const key = apiErrorKeyForCode(code);
    const localized = i18n.t(key);
    const reference = requestId ? i18n.t("errors.requestSuffix", { code, requestId }) : code;
    super(`${localized} ${reference}`);
    this.name = "ApiError";
    this.code = code;
    this.status = status;
    this.requestId = requestId;
    this.serverMessage = message;
  }
}

export function apiErrorKey(error: ApiError): string {
  return apiErrorKeyForCode(error.code);
}

function apiErrorKeyForCode(code: string): "errors.invalidUiLocale" | "errors.unauthenticated" | "errors.csrf" | "errors.generic" {
  const keys: Record<string, "errors.invalidUiLocale" | "errors.unauthenticated" | "errors.csrf"> = { INVALID_UI_LOCALE: "errors.invalidUiLocale", UNAUTHENTICATED: "errors.unauthenticated", CSRF_VALIDATION_FAILED: "errors.csrf" };
  return keys[code] ?? "errors.generic";
}

export interface ApiResponse<T> {
  value: T;
  response: Response;
}

async function readApiError(response: Response): Promise<never> {
  const body = await response.json().catch(() => ({})) as { message?: string; code?: string; requestId?: string };
  throw new ApiError(
    body.message ?? i18n.t("errors.generic"),
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

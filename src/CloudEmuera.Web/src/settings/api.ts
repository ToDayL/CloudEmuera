import { useQuery } from "@tanstack/react-query";
import { apiRequest, apiRequestWithMeta, getCsrfToken } from "../api";
import type { RuntimeWidthModeDto } from "../api/generated";

export interface SessionStartupDefaults {
  fontFaceId: string;
  fontSize: number;
  lineHeight: number;
  widthMode: RuntimeWidthModeDto;
  customWidth: number | null;
  convertBackslashToYen: boolean;
}

export const DEFAULT_SESSION_STARTUP_DEFAULTS: SessionStartupDefaults = {
  fontFaceId: "sarasa-fixed-sc-1.0.40-regular",
  fontSize: 18,
  lineHeight: 19,
  widthMode: "ADAPTIVE",
  customWidth: null,
  convertBackslashToYen: true,
};

export function sessionStartupDefaultsQueryKey(userId: string | undefined) {
  return ["session-startup-defaults", userId] as const;
}

export async function getSessionStartupDefaults(): Promise<SessionStartupDefaults> {
  return apiRequest<SessionStartupDefaults>("/preferences/session-startup-defaults");
}

export async function updateSessionStartupDefaults(defaults: SessionStartupDefaults): Promise<SessionStartupDefaults> {
  const csrfToken = await getCsrfToken();
  return (await apiRequestWithMeta<SessionStartupDefaults>("/preferences/session-startup-defaults", {
    method: "PUT",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrfToken },
    body: JSON.stringify(defaults),
  })).value;
}

export function useSessionStartupDefaults(userId: string | undefined) {
  return useQuery({
    queryKey: sessionStartupDefaultsQueryKey(userId),
    queryFn: getSessionStartupDefaults,
    enabled: Boolean(userId),
    staleTime: 0,
    refetchOnWindowFocus: false,
  });
}

import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { resources } from "./resources";

export const UI_LOCALES = ["zh-CN", "en-US", "ja-JP"] as const;
export type UiLocale = typeof UI_LOCALES[number];
export const DEVICE_LOCALE_KEY = "cloudemuera.uiLocale";
export const LOGIN_LOCALE_OVERRIDE_KEY = "cloudemuera.loginLocaleOverride";

export function normalizeUiLocale(value: unknown): UiLocale | null {
  if (typeof value !== "string") return null;
  const normalized = value.trim().replace("_", "-").toLowerCase();
  if (normalized === "zh" || normalized.startsWith("zh-")) return "zh-CN";
  if (normalized === "ja" || normalized.startsWith("ja-")) return "ja-JP";
  if (normalized === "en" || normalized.startsWith("en-")) return "en-US";
  return null;
}

function safeRead(storage: Storage | undefined, key: string): UiLocale | null {
  try { return exactUiLocale(storage?.getItem(key)); } catch { return null; }
}

function exactUiLocale(value: unknown): UiLocale | null { return typeof value === "string" && UI_LOCALES.includes(value as UiLocale) ? value as UiLocale : null; }

export function resolvePreAuthLocale(loginOverride: unknown, deviceLocale: unknown, navigatorLanguages: readonly string[] = []): UiLocale {
  return exactUiLocale(loginOverride) ?? exactUiLocale(deviceLocale) ?? navigatorLanguages.map(normalizeUiLocale).find(Boolean) ?? "zh-CN";
}

export function resolvePostLoginLocale(loginOverride: unknown, accountLocale: unknown, currentDeviceLocale: unknown): UiLocale {
  return exactUiLocale(loginOverride) ?? exactUiLocale(accountLocale) ?? exactUiLocale(currentDeviceLocale) ?? "zh-CN";
}

export function getLoginLocaleOverride(): UiLocale | null { try { return safeRead(globalThis.sessionStorage, LOGIN_LOCALE_OVERRIDE_KEY); } catch { return null; } }
export function getDeviceLocale(): UiLocale | null { try { return safeRead(globalThis.localStorage, DEVICE_LOCALE_KEY); } catch { return null; } }
export function persistDeviceLocale(locale: UiLocale): void { try { globalThis.localStorage?.setItem(DEVICE_LOCALE_KEY, locale); } catch { /* storage can be disabled */ } }
export function setLoginLocaleOverride(locale: UiLocale): void { persistDeviceLocale(locale); try { globalThis.sessionStorage?.setItem(LOGIN_LOCALE_OVERRIDE_KEY, locale); } catch { /* storage can be disabled */ } }
export function clearLoginLocaleOverride(): void { try { globalThis.sessionStorage?.removeItem(LOGIN_LOCALE_OVERRIDE_KEY); } catch { /* storage can be disabled */ } }

export function applyDocumentLocale(locale: UiLocale): void {
  document.documentElement.lang = locale;
  document.documentElement.dir = "ltr";
}

const initialLocale = resolvePreAuthLocale(getLoginLocaleOverride(), getDeviceLocale(), globalThis.navigator?.languages ?? []);
void i18n.use(initReactI18next).init({ resources, lng: initialLocale, fallbackLng: "zh-CN", supportedLngs: UI_LOCALES, initImmediate: false, showSupportNotice: false, interpolation: { escapeValue: false } });
applyDocumentLocale(initialLocale);
i18n.on("languageChanged", language => applyDocumentLocale(normalizeUiLocale(language) ?? "zh-CN"));
globalThis.addEventListener?.("storage", event => {
  if (event.key !== DEVICE_LOCALE_KEY) return;
  const locale = normalizeUiLocale(event.newValue);
  if (locale) void i18n.changeLanguage(locale);
});

export async function changeUiLocale(locale: UiLocale): Promise<void> { await i18n.changeLanguage(locale); }
export default i18n;

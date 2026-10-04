import i18n from "../i18n";

export interface FontDigestResult { bytes: ArrayBuffer; digest: string }
const VERIFICATION_TIMEOUT_MS = 30_000;

export async function hashRuntimeFont(bytes: ArrayBuffer): Promise<FontDigestResult> {
  // The browser's secure context is authoritative, including TLS terminated at
  // a reverse proxy. Never infer it from the API's internal connection scheme.
  if (globalThis.crypto?.subtle) {
    const digest = await crypto.subtle.digest("SHA-256", bytes);
    return { bytes, digest: Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("") };
  }

  return new Promise((resolve, reject) => {
    const byteLength = bytes.byteLength;
    let worker: Worker;
    try {
      worker = new Worker(new URL("./RuntimeFontDigest.worker.ts", import.meta.url), { type: "module" });
    } catch { reject(new Error(i18n.t("runtimeUi.fontDigest"))); return; }
    const cleanup = () => { clearTimeout(timeout); worker.terminate(); };
    const fail = () => { cleanup(); reject(new Error(i18n.t("runtimeUi.fontDigest"))); };
    const timeout = setTimeout(fail, VERIFICATION_TIMEOUT_MS);
    worker.onerror = fail;
    worker.onmessageerror = fail;
    worker.onmessage = (event: MessageEvent<FontDigestResult>) => {
      const result = event.data;
      if (!(result?.bytes instanceof ArrayBuffer) || result.bytes.byteLength !== byteLength || !/^[0-9a-f]{64}$/.test(result.digest)) { fail(); return; }
      cleanup();
      resolve(result);
    };
    try { worker.postMessage(bytes, [bytes]); } catch { fail(); }
  });
}

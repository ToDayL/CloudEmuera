import { hashRuntimeFont } from "./RuntimeFontDigest";
import type { RuntimeFontFace } from "../sessions/api";
import { readRuntimeFontBytes, writeRuntimeFontBytes, deleteRuntimeFontBytes } from "./RuntimeFontCache";
import i18n from "../i18n";

export interface RuntimeFontProgress {
  phase: "downloading" | "verifying" | "decoding" | "ready";
  receivedBytes: number;
  totalBytes: number;
}

type ProgressListener = (progress: RuntimeFontProgress) => void;
interface FontLoad {
  promise: Promise<FontFace>;
  progress: RuntimeFontProgress;
  listeners: Set<ProgressListener>;
}
const loadedByDigest = new Map<string, FontLoad>();
const DOWNLOAD_TIMEOUT_MS = 120_000;

export function runtimeFontCssFamily(face: RuntimeFontFace): string {
  return `cloudemuera-runtime-${face.webAssetDigest.slice(0, 16)}`;
}

/** Shared downloads and verified FontFaces survive navigation within this document.
 * Reloads register a new FontFace using verified IndexedDB bytes or HTTP cache. */
export function loadRuntimeFont(face: RuntimeFontFace, cssFamily: string, onProgress?: ProgressListener): Promise<FontFace> {
  let entry = loadedByDigest.get(face.webAssetDigest);
  if (!entry) {
    const loading: FontLoad = {
      promise: undefined!,
      progress: { phase: "downloading", receivedBytes: 0, totalBytes: face.webAssetByteLength },
      listeners: new Set(),
    };
    loading.promise = Promise.resolve().then(() => loadAndVerify(face, cssFamily, progress => {
      loading.progress = progress;
      for (const listener of loading.listeners) listener(progress);
    })).catch(error => {
      loadedByDigest.delete(face.webAssetDigest);
      throw error;
    });
    loadedByDigest.set(face.webAssetDigest, loading);
    entry = loading;
  }
  if (!onProgress) return entry.promise;
  const listeners = entry.listeners;
  listeners.add(onProgress);
  onProgress(entry.progress);
  return entry.promise.finally(() => listeners.delete(onProgress));
}

async function loadAndVerify(face: RuntimeFontFace, cssFamily: string, report: ProgressListener): Promise<FontFace> {
  if (typeof FontFace === "undefined" || typeof document === "undefined" || !document.fonts)
    throw new Error(i18n.t("runtimeUi.fontUnsupported"));
  if (!/^[0-9a-f]{64}$/.test(face.webAssetDigest) || !Number.isSafeInteger(face.webAssetByteLength) || face.webAssetByteLength <= 0)
    throw new Error(i18n.t("runtimeUi.fontCatalog"));

  let bytes = await readRuntimeFontBytes(face.webAssetDigest);
  if (bytes) {
    report({ phase: "verifying", receivedBytes: bytes.byteLength, totalBytes: face.webAssetByteLength });
    const validLength = bytes.byteLength === face.webAssetByteLength;
    const verified = validLength ? await hashRuntimeFont(bytes) : null;
    bytes = verified?.bytes ?? bytes;
    if (!verified || verified.digest !== face.webAssetDigest) {
      await deleteRuntimeFontBytes(face.webAssetDigest);
      bytes = null;
    }
  }
  const fromCache = bytes !== null;
  if (!bytes) {
    report({ phase: "downloading", receivedBytes: 0, totalBytes: face.webAssetByteLength });
    bytes = await downloadFont(face, report);
    report({ phase: "verifying", receivedBytes: bytes.byteLength, totalBytes: face.webAssetByteLength });
    const verified = await hashRuntimeFont(bytes);
    bytes = verified.bytes;
    if (verified.digest !== face.webAssetDigest) throw new Error(i18n.t("runtimeUi.fontDigest"));
  }
  const progress = { receivedBytes: bytes.byteLength, totalBytes: face.webAssetByteLength };
  report({ ...progress, phase: "decoding" });
  const loaded = await new FontFace(cssFamily, bytes, {
    display: "block",
    style: "normal",
    weight: String(face.weight),
  }).load();
  document.fonts.add(loaded);
  // FontFace.load() has decoded this exact face. Waiting for fonts.ready would
  // also wait for unrelated application fonts and layout in the document.
  if (!fromCache) await writeRuntimeFontBytes(face.webAssetDigest, bytes);
  report({ ...progress, phase: "ready" });
  return loaded;
}

async function downloadFont(face: RuntimeFontFace, report: ProgressListener): Promise<ArrayBuffer> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), DOWNLOAD_TIMEOUT_MS);
  let bytes: ArrayBuffer;
  try {
    const response = await fetch(face.webAssetUrl, { credentials: "same-origin", cache: "force-cache", signal: controller.signal });
    if (!response.ok) throw new Error(i18n.t("runtimeUi.fontHttp", { status: response.status }));
    const contentType = response.headers.get("content-type")?.split(";", 1)[0].trim().toLowerCase();
    if (contentType !== "font/woff2") throw new Error(i18n.t("runtimeUi.fontMime"));
    const declaredLength = response.headers.get("content-length");
    if (declaredLength !== null && declaredLength !== String(face.webAssetByteLength))
      throw new Error(i18n.t("runtimeUi.fontCatalogLength"));

    if (response.body) {
      const reader = response.body.getReader();
      const buffer = new Uint8Array(face.webAssetByteLength);
      let received = 0;
      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          received += value.byteLength;
          if (received > buffer.byteLength) throw new Error(i18n.t("runtimeUi.fontLength"));
          buffer.set(value, received - value.byteLength);
          report({ phase: "downloading", receivedBytes: received, totalBytes: buffer.byteLength });
        }
        if (received !== buffer.byteLength) throw new Error(i18n.t("runtimeUi.fontLength"));
      } catch (error) {
        await reader.cancel().catch(() => undefined);
        throw error;
      } finally {
        reader.releaseLock();
      }
      bytes = buffer.buffer;
    } else {
      bytes = await response.arrayBuffer();
      if (bytes.byteLength !== face.webAssetByteLength) throw new Error(i18n.t("runtimeUi.fontLength"));
    }
  } finally {
    clearTimeout(timeout);
    controller.abort();
  }
  return bytes;
}

export function clearRuntimeFontCacheForTests(): void {
  loadedByDigest.clear();
}

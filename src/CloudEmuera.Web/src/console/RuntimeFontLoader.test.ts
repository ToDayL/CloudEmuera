import { TestFontDigestWorker } from "../test/FontDigestWorker";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearRuntimeFontCacheForTests, loadRuntimeFont, runtimeFontCssFamily } from "./RuntimeFontLoader";
import { readRuntimeFontBytes, writeRuntimeFontBytes, deleteRuntimeFontBytes } from "./RuntimeFontCache";
import type { RuntimeFontFace } from "../sessions/api";

vi.mock("./RuntimeFontCache", () => ({
  readRuntimeFontBytes: vi.fn(), writeRuntimeFontBytes: vi.fn(), deleteRuntimeFontBytes: vi.fn(),
}));

const digest = "01799063a83f8af346c5e02f1a46c3adcd8b81a189abda60a6903075aea7bb25";
const face: RuntimeFontFace = {
  faceId: "sarasa-fixed-sc-1.0.40-regular",
  displayName: "Sarasa Fixed SC Regular",
  family: "sarasa-fixed-sc",
  sourceVersion: "1.0.40",
  weight: 400,
  runtimeFamilyName: "Sarasa Fixed SC",
  webAssetDigest: digest,
  webAssetByteLength: 9,
  webAssetUrl: `/api/v1/runtime-fonts/assets/${digest}.woff2`,
  licenseId: "OFL-1.1",
};

describe("RuntimeFontLoader (PLAY-013)", () => {
  const originalFonts = Object.getOwnPropertyDescriptor(document, "fonts");
  const add = vi.fn();
  beforeEach(() => {
    vi.stubGlobal("Worker", TestFontDigestWorker);
    Object.defineProperty(document, "fonts", { configurable: true, value: { add, ready: new Promise(() => {}) } });
    class TestFontFace {
      load(): Promise<FontFace> { return Promise.resolve(this as unknown as FontFace); }
    }
    vi.stubGlobal("FontFace", TestFontFace);
    add.mockClear();
    vi.mocked(readRuntimeFontBytes).mockReset().mockResolvedValue(null);
    vi.mocked(writeRuntimeFontBytes).mockReset().mockResolvedValue(true);
    vi.mocked(deleteRuntimeFontBytes).mockReset().mockResolvedValue(true);
  });
  afterEach(() => {
    clearRuntimeFontCacheForTests();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    if (originalFonts) Object.defineProperty(document, "fonts", originalFonts);
    else Reflect.deleteProperty(document, "fonts");
  });

  it("keeps WOFF2 digest verification working without SubtleCrypto", async () => {
    const originalFonts = Object.getOwnPropertyDescriptor(document, "fonts");
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: { add: vi.fn(), load: vi.fn().mockResolvedValue([]), ready: Promise.resolve([]) },
    });
    class TestFontFace {
      constructor(readonly family: string, readonly source: ArrayBuffer, readonly descriptors: FontFaceDescriptors) {}
      load(): Promise<FontFace> { return Promise.resolve(this as unknown as FontFace); }
    }
    vi.stubGlobal("FontFace", TestFontFace);
    vi.stubGlobal("crypto", { subtle: undefined });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(new TextEncoder().encode("font-test"), {
      headers: { "Content-Type": "font/woff2", "Content-Length": "9" },
    })));

    try {
      await expect(loadRuntimeFont(face, runtimeFontCssFamily(face))).resolves.toBeTruthy();
    } finally {
      if (originalFonts) Object.defineProperty(document, "fonts", originalFonts);
      else Reflect.deleteProperty(document, "fonts");
    }
  });
  it("shares an in-flight download and replays progress to a later Session", async () => {
    let stream!: ReadableStreamDefaultController<Uint8Array>;
    const fetchMock = vi.fn().mockResolvedValue(new Response(new ReadableStream<Uint8Array>({ start(controller) { stream = controller; } }), { headers: { "Content-Type": "font/woff2" } }));
    vi.stubGlobal("fetch", fetchMock);
    const firstProgress = vi.fn();
    const secondProgress = vi.fn();
    const first = loadRuntimeFont(face, runtimeFontCssFamily(face), firstProgress);
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledOnce());
    stream.enqueue(new TextEncoder().encode("font"));
    await vi.waitFor(() => expect(firstProgress).toHaveBeenLastCalledWith({ phase: "downloading", receivedBytes: 4, totalBytes: 9 }));
    const second = loadRuntimeFont(face, runtimeFontCssFamily(face), secondProgress);
    expect(secondProgress).toHaveBeenLastCalledWith({ phase: "downloading", receivedBytes: 4, totalBytes: 9 });
    stream.enqueue(new TextEncoder().encode("-test"));
    stream.close();
    expect(await first).toBe(await second);
    expect(firstProgress.mock.calls.map(([progress]) => progress.phase)).toEqual(["downloading", "downloading", "downloading", "downloading", "verifying", "decoding", "ready"]);
    expect(await loadRuntimeFont(face, runtimeFontCssFamily(face))).toBe(await first);
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock).toHaveBeenCalledWith(face.webAssetUrl, expect.objectContaining({ cache: "force-cache" }));
    expect(add).toHaveBeenCalledOnce();
    // Unrelated document.fonts.ready never resolves; this exact face is enough.
  });

  it.each([
    ["wrong digest", "font-fail", "font/woff2"],
    ["short body", "font", "font/woff2"],
    ["oversized body", "font-test-too-long", "font/woff2"],
    ["wrong MIME", "font-test", "text/html"],
  ])("rejects %s and permits retry without registering a bad face", async (_name, content, mime) => {
    const fetchMock = vi.fn().mockResolvedValueOnce(new Response(content, { headers: { "Content-Type": mime } }))
      .mockResolvedValueOnce(new Response("font-test", { headers: { "Content-Type": "font/woff2" } }));
    vi.stubGlobal("fetch", fetchMock);
    await expect(loadRuntimeFont(face, runtimeFontCssFamily(face))).rejects.toThrow();
    expect(add).not.toHaveBeenCalled();
    await expect(loadRuntimeFont(face, runtimeFontCssFamily(face))).resolves.toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("reuses verified persisted bytes without fetching after a document reload", async () => {
    vi.mocked(readRuntimeFontBytes).mockResolvedValue(new TextEncoder().encode("font-test").buffer);
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    await expect(loadRuntimeFont(face, runtimeFontCssFamily(face))).resolves.toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(writeRuntimeFontBytes).not.toHaveBeenCalled();
  });

  it("evicts corrupt persisted bytes and downloads a verified replacement", async () => {
    vi.mocked(readRuntimeFontBytes).mockResolvedValue(new TextEncoder().encode("font-fail").buffer);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("font-test", { headers: { "Content-Type": "font/woff2" } })));
    await expect(loadRuntimeFont(face, runtimeFontCssFamily(face))).resolves.toBeTruthy();
    expect(deleteRuntimeFontBytes).toHaveBeenCalledWith(digest);
    expect(writeRuntimeFontBytes).toHaveBeenCalledWith(digest, expect.any(ArrayBuffer));
  });

  it("bounds a stalled download and allows retry", async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn((_url, options: RequestInit) => new Promise((_resolve, reject) => {
      options.signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
    }));
    vi.stubGlobal("fetch", fetchMock);
    const failed = expect(loadRuntimeFont(face, runtimeFontCssFamily(face))).rejects.toThrow("Aborted");
    await vi.advanceTimersByTimeAsync(120_000);
    await failed;
    fetchMock.mockImplementationOnce(() => Promise.resolve(new Response("font-test", { headers: { "Content-Type": "font/woff2" } })));
    await expect(loadRuntimeFont(face, runtimeFontCssFamily(face))).resolves.toBeTruthy();
  });

});

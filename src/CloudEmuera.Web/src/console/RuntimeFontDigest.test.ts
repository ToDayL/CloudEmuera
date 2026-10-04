import { afterEach, describe, expect, it, vi } from "vitest";
import { hashRuntimeFont } from "./RuntimeFontDigest";
import { sha256Fallback } from "./RuntimeFontSha256";
import { TestFontDigestWorker } from "../test/FontDigestWorker";

describe("RuntimeFontDigest (PLAY-013)", () => {
  afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

  it.each([
    ["", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"],
    ["abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"],
    ["abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"],
    ["a".repeat(1_000_000), "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"],
  ])("matches the SHA-256 vector %# without timer-based yields", (input, digest) => {
    expect(sha256Fallback(new TextEncoder().encode(input))).toBe(digest);
  });

  it("uses browser Web Crypto when available regardless of the backend scheme", async () => {
    const digest = vi.fn().mockResolvedValue(new Uint8Array(32).buffer);
    const worker = vi.fn();
    vi.stubGlobal("crypto", { subtle: { digest } });
    vi.stubGlobal("Worker", worker);
    const bytes = new TextEncoder().encode("font-test").buffer;
    expect(await hashRuntimeFont(bytes)).toEqual({ bytes, digest: "0".repeat(64) });
    expect(digest).toHaveBeenCalledWith("SHA-256", bytes);
    expect(worker).not.toHaveBeenCalled();
  });

  it("transfers HTTP verification bytes to a Worker and returns them intact for decoding", async () => {
    vi.stubGlobal("crypto", { subtle: undefined });
    const terminate = vi.spyOn(TestFontDigestWorker.prototype, "terminate");
    vi.stubGlobal("Worker", TestFontDigestWorker);
    const bytes = new TextEncoder().encode("font-test").buffer;
    const result = await hashRuntimeFont(bytes);
    expect(bytes.byteLength).toBe(0);
    expect(new TextDecoder().decode(result.bytes)).toBe("font-test");
    expect(result.digest).toBe("01799063a83f8af346c5e02f1a46c3adcd8b81a189abda60a6903075aea7bb25");
    expect(terminate).toHaveBeenCalledOnce();
    terminate.mockRestore();
  });

  it.each(["error", "malformed", "timeout"])("fails closed and terminates a Worker on %s", async mode => {
    vi.useFakeTimers();
    vi.stubGlobal("crypto", { subtle: undefined });
    const terminate = vi.fn();
    vi.stubGlobal("Worker", class {
      onerror: (() => void) | null = null;
      onmessage: ((event: { data: unknown }) => void) | null = null;
      terminate = terminate;
      postMessage() {
        if (mode === "error") queueMicrotask(() => this.onerror?.());
        if (mode === "malformed") queueMicrotask(() => this.onmessage?.({ data: {} }));
      }
    });
    const rejected = expect(hashRuntimeFont(new ArrayBuffer(9))).rejects.toThrow();
    await vi.advanceTimersByTimeAsync(mode === "timeout" ? 30_000 : 0);
    await rejected;
    expect(terminate).toHaveBeenCalledOnce();
  });
});

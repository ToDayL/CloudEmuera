import { afterEach, describe, expect, it, vi } from "vitest";
import { readRuntimeFontBytes, writeRuntimeFontBytes } from "./RuntimeFontCache";

describe("RuntimeFontCache (PLAY-013)", () => {
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it("bounds a stalled database open so font loading can fall back", async () => {
    vi.useFakeTimers();
    vi.stubGlobal("indexedDB", { open: vi.fn().mockReturnValue({}) });
    const read = readRuntimeFontBytes("a".repeat(64));
    await vi.advanceTimersByTimeAsync(1_500);
    await expect(read).resolves.toBeNull();
  });

  it("treats denied storage as a cache miss and a best-effort write", async () => {
    vi.stubGlobal("indexedDB", { open() { throw new DOMException("Denied", "SecurityError"); } });
    await expect(readRuntimeFontBytes("a".repeat(64))).resolves.toBeNull();
    await expect(writeRuntimeFontBytes("a".repeat(64), new ArrayBuffer(9))).resolves.toBeNull();
  });
});

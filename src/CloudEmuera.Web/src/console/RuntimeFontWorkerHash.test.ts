import { afterEach, describe, expect, it, vi } from "vitest";
import { hashFontInWorker } from "./RuntimeFontWorkerHash";
import { sha256Fallback } from "./RuntimeFontSha256";

describe("HTTP Worker WASM SHA-256 (PLAY-013)", () => {
  afterEach(() => vi.restoreAllMocks());

  it.each(["", "abc", "font-test", "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "a".repeat(1_000_000)])("matches the verified JavaScript implementation for vector %#", async input => {
    const bytes = new TextEncoder().encode(input);
    expect(await hashFontInWorker(bytes.buffer)).toEqual({ digest: sha256Fallback(bytes), implementation: "wasm" });
  });
});

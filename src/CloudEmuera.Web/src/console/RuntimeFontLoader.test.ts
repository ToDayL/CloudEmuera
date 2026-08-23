import { afterEach, describe, expect, it, vi } from "vitest";
import { clearRuntimeFontCacheForTests, loadRuntimeFont, runtimeFontCssFamily } from "./RuntimeFontLoader";
import type { RuntimeFontFace } from "../sessions/api";

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

describe("RuntimeFontLoader", () => {
  afterEach(() => {
    clearRuntimeFontCacheForTests();
    vi.unstubAllGlobals();
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
});

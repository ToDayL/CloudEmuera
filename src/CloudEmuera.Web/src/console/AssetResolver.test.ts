import { describe, expect, it } from "vitest";
import { AssetResolver } from "./AssetResolver";

describe("AssetResolver", () => {
  it("only creates same-origin URLs for manifest-authorized safe assets", () => {
    const resolver = new AssetResolver("s/../hidden", {
      schemaVersion: 1,
      assets: [
        { assetId: "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", mediaType: "image/png", byteLength: 10, contentDigest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
        { assetId: "external", mediaType: "text/html", byteLength: 1, contentDigest: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
      ],
      fonts: [],
      fontDiagnostics: [],
    });
    expect(resolver.url("sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).toContain("/api/v1/sessions/s%2F..%2Fhidden/assets/");
    expect(resolver.url("external")).toBeNull();
    expect(resolver.url("../../etc/passwd")).toBeNull();
  });

  it("maps logical font names to a fixed CSS family and validates fallback", () => {
    const assetId = "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const resolver = new AssetResolver("s1", {
      schemaVersion: 1,
      assets: [{ assetId, mediaType: "font/woff2", byteLength: 10, contentDigest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }],
      fonts: [{ family: "game-default", assetId, fallback: "sans-serif", cssFamily: "cloudemuera-font-0123456789abcdef", aliases: ["default"] }],
      fontDiagnostics: [],
    });
    expect(resolver.fontFamily("game-default")).toBe("cloudemuera-font-0123456789abcdef");
    expect(resolver.fontFamily("default")).toBe("cloudemuera-font-0123456789abcdef");
    expect(resolver.fontFamily("font-family:evil")).toBe("sans-serif");
  });

  it("keeps multiple font assets addressable and exposes compatibility diagnostics", () => {
    const first = "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const second = "sha256-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const resolver = new AssetResolver("s1", {
      schemaVersion: 1,
      assets: [
        { assetId: first, mediaType: "font/woff2", byteLength: 10, contentDigest: `sha256:${"a".repeat(64)}` },
        { assetId: second, mediaType: "font/woff2", byteLength: 10, contentDigest: `sha256:${"b".repeat(64)}` },
      ],
      fonts: [
        { family: "alpha", assetId: first, fallback: "sans-serif", cssFamily: "cloudemuera-font-aaaaaaaaaaaaaaaa", aliases: ["default"] },
        { family: "beta", assetId: second, fallback: "sans-serif", cssFamily: "cloudemuera-font-bbbbbbbbbbbbbbbb", aliases: [] },
      ],
      fontDiagnostics: ["FONT_MULTIPLE_ASSETS_ISOLATED"],
    });
    expect(resolver.manifestFonts()).toHaveLength(2);
    expect(resolver.fontFamily("default")).toBe("cloudemuera-font-aaaaaaaaaaaaaaaa");
    expect(resolver.fontFamily("beta")).toBe("cloudemuera-font-bbbbbbbbbbbbbbbb");
    expect(resolver.diagnostics()).toEqual(["FONT_MULTIPLE_ASSETS_ISOLATED"]);
  });
});

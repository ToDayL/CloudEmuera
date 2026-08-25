import { describe, expect, it } from "vitest";
import { AssetResolver } from "./AssetResolver";

describe("AssetResolver", () => {
  it("only creates same-origin URLs for path references", () => {
    const resolver = new AssetResolver("s/../hidden");
    const assetId = "path-c2F2L2JhY2tncm91bmQucG5n";

    expect(resolver.url(assetId)).toContain("/api/v1/sessions/s%2F..%2Fhidden/assets/");
    expect(resolver.url("sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).toBeNull();
    expect(resolver.url("external")).toBeNull();
    expect(resolver.url("../../etc/passwd")).toBeNull();
  });

  it("keeps the runtime-selected font authoritative and has no manifest diagnostics", () => {
    const resolver = new AssetResolver("s1", "cloudemuera-runtime-face");
    expect(resolver.fontFamily("default")).toBe("cloudemuera-runtime-face");
    expect(resolver.fontFamily("game-default")).toBe("cloudemuera-runtime-face");
    expect(resolver.diagnostics()).toEqual([]);
  });

  it("falls back to a safe browser family without a presentation manifest", () => {
    const resolver = new AssetResolver("s1");
    expect(resolver.fontFamily("font-family:evil")).toBe("sans-serif");
  });
});

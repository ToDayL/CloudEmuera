import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AssetResolver } from "./AssetResolver";
import { SafeHtmlRenderer } from "./SafeHtmlRenderer";

describe("SafeHtmlRenderer", () => {
  it("renders text and allowlisted tags without interpreting markup", () => {
    const assets = new AssetResolver("s1", { schemaVersion: 1, assets: [], fonts: [], fontDiagnostics: [] });
    render(<SafeHtmlRenderer assets={assets} node={{ type: "element", tag: "div", children: [
      { type: "element", tag: "strong", children: [{ type: "text", text: "<script>alert(1)</script>" }] },
      { type: "element", tag: "p", children: [{ type: "element", tag: "strike", children: [{ type: "text", text: "struck" }] }] },
    ] }} />);
    expect(screen.getByText("<script>alert(1)</script>")).toBeInTheDocument();
    expect(document.querySelector("s")).toHaveTextContent("struck");
    expect(document.querySelector("script")).toBeNull();
    expect(document.querySelector("a")).toBeNull();
  });

  it("renders only manifest-authorized HTML image assets", () => {
    const assets = new AssetResolver("s1", {
      schemaVersion: 1,
      assets: [{ assetId: "sha256-image", mediaType: "image/png", byteLength: 8, contentDigest: `sha256:${"a".repeat(64)}`, eTag: null }],
      fonts: [],
      fontDiagnostics: [],
    });
    render(<SafeHtmlRenderer assets={assets} node={{ type: "element", tag: "img", assetId: "sha256-image", altText: "fixture", children: [] }} />);
    expect(screen.getByRole("img", { name: "fixture" })).toHaveAttribute("src", "/api/v1/sessions/s1/assets/sha256-image");
  });

  it("fails closed for disallowed tags, unsafe styles, and unauthorized assets", () => {
    const onRenderError = vi.fn();
    const assets = new AssetResolver("s1", { schemaVersion: 1, assets: [], fonts: [], fontDiagnostics: [] });
    render(<SafeHtmlRenderer assets={assets} onRenderError={onRenderError} node={{ type: "element", tag: "a", assetId: "sha256-missing", children: [{ type: "text", text: "blocked" }], style: { decorations: ["blink", "bold"], fontFamily: "url(javascript:evil)", fontSize: 16, lineHeight: 0, foreground: null, background: null } } as never} />);
    expect(screen.queryByText("blocked")).toBeNull();
    expect(document.querySelector("a")).toBeNull();
    expect(onRenderError).not.toHaveBeenCalled();
  });
});

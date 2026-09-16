import manifest from "../public/manifest.json";
import { describe, expect, it } from "vitest";

describe("Home Screen web app manifest", () => {
  it("NFR-016 keeps every same-origin application route in scope", () => {
    expect(manifest).toMatchObject({
      id: "/",
      start_url: "/games",
      scope: "/",
      display: "standalone",
      icons: [{
        src: "/cloudemuera-icon.png",
        sizes: "1254x1254",
        type: "image/png",
        purpose: "any",
      }],
    });
  });
});

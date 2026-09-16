import manifest from "../public/manifest.json";
import { describe, expect, it } from "vitest";

describe("Home Screen web app manifest", () => {
  it("NFR-016 keeps every same-origin application route in scope", () => {
    expect(manifest).toMatchObject({
      id: "/",
      start_url: "/games",
      scope: "/",
      display: "standalone",
    });
  });
});

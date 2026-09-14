import { describe, expect, it } from "vitest";
import { browserLayoutWidth } from "./api";

describe("browser layout width", () => {
  it("uses the narrower document content box when a scrollbar reduces available width", () => {
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 390 });
    Object.defineProperty(document.documentElement, "clientWidth", { configurable: true, value: 375 });

    expect(browserLayoutWidth()).toBe(375);
  });

  it("falls back to the browser width when the document metric is unavailable", () => {
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 390 });
    Object.defineProperty(document.documentElement, "clientWidth", { configurable: true, value: 0 });

    expect(browserLayoutWidth()).toBe(390);
  });
});

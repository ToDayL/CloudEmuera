import { describe, expect, it } from "vitest";
import { consoleSurfaceStyle, consoleViewportStyle, effectiveConsoleWidth, isBlankConsoleSurfaceTarget } from "./ConsolePage";

describe("console surface click filtering", () => {
  it("accepts non-control output areas and ignores buttons and form controls", () => {
    const surface = document.createElement("div");
    const button = document.createElement("button");
    const buttonLabel = document.createElement("span");
    button.append(buttonLabel);
    const input = document.createElement("input");
    const roleButton = document.createElement("div");
    roleButton.setAttribute("role", "button");

    expect(isBlankConsoleSurfaceTarget(surface)).toBe(true);
    expect(isBlankConsoleSurfaceTarget(button)).toBe(false);
    expect(isBlankConsoleSurfaceTarget(buttonLabel)).toBe(false);
    expect(isBlankConsoleSurfaceTarget(input)).toBe(false);
    expect(isBlankConsoleSurfaceTarget(roleButton)).toBe(false);
  });
});

describe("console surface background", () => {
  it("applies the runtime default background to the whole surface", () => {
    expect(consoleSurfaceStyle({ red: 18, green: 52, blue: 86, alpha: 255 })).toEqual({ backgroundColor: "rgba(18, 52, 86, 1)" });
    expect(consoleSurfaceStyle(null)).toEqual({});
  });

  it("uses the server-selected runtime viewport width", () => {
    expect(consoleSurfaceStyle(null, 640, 390)).toEqual({ width: "640px" });
    expect(effectiveConsoleWidth(1000, 390)).toBe(1000);
    expect(effectiveConsoleWidth(390, 1024)).toBe(390);
  });
});

describe("console visual viewport", () => {
  it("uses the visible viewport dimensions after the soft keyboard resizes it", () => {
    expect(consoleViewportStyle(612, 0)).toEqual({
      "--console-visual-viewport-height": "612px",
      "--console-visual-viewport-offset-top": "0px",
    });
    expect(consoleViewportStyle(612.4, 24.6)).toEqual({
      "--console-visual-viewport-height": "612px",
      "--console-visual-viewport-offset-top": "25px",
    });
  });

  it("falls back to the dynamic viewport CSS unit when the browser reports no usable height", () => {
    expect(consoleViewportStyle(0)).toEqual({});
    expect(consoleViewportStyle(Number.NaN)).toEqual({});
  });
});

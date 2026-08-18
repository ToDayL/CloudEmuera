import { describe, expect, it } from "vitest";
import { isBlankConsoleSurfaceTarget } from "./ConsolePage";

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

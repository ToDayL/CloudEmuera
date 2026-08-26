import { createEvent, fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { consoleSurfaceStyle, consoleViewportStyle, ConsoleSurface, effectiveConsoleWidth, isBlankConsoleSurfaceTarget } from "./ConsolePage";

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

  it("consumes a two-finger gesture before a button can receive a synthetic click", () => {
    const buttonClick = vi.fn();
    const submitRightClick = vi.fn(() => true);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}>
      <button type="button" onClick={buttonClick}>游戏按钮</button>
    </ConsoleSurface>);

    const button = screen.getByRole("button", { name: "游戏按钮" });
    fireEvent.touchStart(button, { touches: [{ clientX: 4, clientY: 4 }, { clientX: 8, clientY: 8 }] });
    fireEvent.touchEnd(button, { touches: [] });
    fireEvent.click(button);

    expect(submitRightClick).toHaveBeenCalledTimes(1);
    expect(buttonClick).not.toHaveBeenCalled();
  });

  it("keeps a single-finger button tap available", () => {
    const buttonClick = vi.fn();
    const submitRightClick = vi.fn(() => true);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}>
      <button type="button" onClick={buttonClick}>游戏按钮</button>
    </ConsoleSurface>);

    const button = screen.getByRole("button", { name: "游戏按钮" });
    fireEvent.touchStart(button, { touches: [{ clientX: 4, clientY: 4 }] });
    fireEvent.click(button);

    expect(submitRightClick).not.toHaveBeenCalled();
    expect(buttonClick).toHaveBeenCalledTimes(1);
  });

  it("does not cancel a single-finger touch, so native scrolling can start immediately", () => {
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick: vi.fn(() => true) } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}><div>输出</div></ConsoleSurface>);

    const output = screen.getByText("输出");
    const touchStart = createEvent.touchStart(output, { touches: [{ clientX: 4, clientY: 4 }] });
    fireEvent(output, touchStart);

    expect(touchStart.defaultPrevented).toBe(false);
    expect(promptControllerRef.current.submitRightClick).not.toHaveBeenCalled();
  });

  it("consumes a two-touch pointer gesture before a button can receive a click", () => {
    const buttonClick = vi.fn();
    const submitRightClick = vi.fn(() => true);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}>
      <button type="button" onClick={buttonClick}>游戏按钮</button>
    </ConsoleSurface>);

    const button = screen.getByRole("button", { name: "游戏按钮" });
    fireEvent.pointerDown(button, { pointerId: 1, pointerType: "touch", clientX: 4, clientY: 4 });
    fireEvent.pointerDown(button, { pointerId: 2, pointerType: "touch", clientX: 8, clientY: 8 });
    fireEvent.pointerUp(button, { pointerId: 2, pointerType: "touch" });
    fireEvent.pointerUp(button, { pointerId: 1, pointerType: "touch" });
    fireEvent.click(button);

    expect(submitRightClick).toHaveBeenCalledTimes(1);
    expect(buttonClick).not.toHaveBeenCalled();
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

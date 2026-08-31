import { createEvent, fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { consoleBackgroundStyle, consoleSurfaceStyle, consoleViewportStyle, ConsoleSurface, effectiveConsoleWidth, isBlankConsoleSurfaceTarget, submitConsoleSurfacePointer } from "./ConsolePage";

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

  it("clears an accidental selection after a stationary mouse click in the game stage", () => {
    vi.stubGlobal("requestAnimationFrame", undefined);
    const removeAllRanges = vi.fn();
    const getSelection = vi.spyOn(document, "getSelection").mockReturnValue({ removeAllRanges } as unknown as Selection);
    try {
      const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick: vi.fn(() => false) } };
      render(<ConsoleSurface promptControllerRef={promptControllerRef}><div className="realtime-console-stage"><div>输出</div></div></ConsoleSurface>);
      const output = screen.getByText("输出");
      fireEvent.pointerDown(output, { pointerId: 1, pointerType: "mouse", button: 0, clientX: 4, clientY: 4 });
      fireEvent.pointerUp(output, { pointerId: 1, pointerType: "mouse", button: 0, clientX: 4, clientY: 4 });

      expect(removeAllRanges).toHaveBeenCalledTimes(1);
    } finally {
      getSelection.mockRestore();
      vi.unstubAllGlobals();
    }
  });

  it("keeps an intentional mouse drag available for text selection", () => {
    vi.stubGlobal("requestAnimationFrame", undefined);
    const removeAllRanges = vi.fn();
    const getSelection = vi.spyOn(document, "getSelection").mockReturnValue({ removeAllRanges } as unknown as Selection);
    try {
      const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick: vi.fn(() => false) } };
      render(<ConsoleSurface promptControllerRef={promptControllerRef}><div className="realtime-console-stage"><div>输出</div></div></ConsoleSurface>);
      const output = screen.getByText("输出");
      fireEvent.pointerDown(output, { pointerId: 1, pointerType: "mouse", button: 0, clientX: 4, clientY: 4 });
      fireEvent.pointerMove(output, { pointerId: 1, pointerType: "mouse", clientX: 20, clientY: 4 });
      fireEvent.pointerUp(output, { pointerId: 1, pointerType: "mouse", button: 0, clientX: 20, clientY: 4 });

      expect(removeAllRanges).not.toHaveBeenCalled();
    } finally {
      getSelection.mockRestore();
      vi.unstubAllGlobals();
    }
  });

  it("submits a middle press from ordinary game output to a mouse-enabled prompt", () => {
    const submitPointer = vi.fn(() => true);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick: vi.fn(() => false), submitPointer } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef} runtimeViewportHeight={600}><div className="realtime-console-stage"><div>战斗面板空白</div></div></ConsoleSurface>);

    const output = screen.getByText("战斗面板空白");
    const middleDown = createEvent.pointerDown(output, { pointerId: 1, pointerType: "mouse", button: 1, clientX: 24, clientY: 12 });
    fireEvent(output, middleDown);

    expect(middleDown.defaultPrevented).toBe(true);
    expect(submitPointer).toHaveBeenCalledWith({ x: 0, y: 0, button: 1, pressed: true });
  });

  it("submits a left click from ordinary game output to a mouse-enabled prompt", () => {
    const submitPointer = vi.fn(() => true);
    const submitBlankEnter = vi.fn();
    const controller = { submitBlankEnter, submitRightClick: vi.fn(() => false), submitPointer };
    const output = document.createElement("div");

    expect(submitConsoleSurfacePointer(controller, output, 24, 12, 0, 600)).toBe(true);

    expect(submitPointer).toHaveBeenCalledWith({ x: 0, y: 0, button: 0, pressed: true });
    expect(submitBlankEnter).not.toHaveBeenCalled();
  });

  it("submits a right press from ordinary game output to a mouse-enabled prompt", () => {
    const submitPointer = vi.fn(() => true);
    const submitRightClick = vi.fn(() => false);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick, submitPointer } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef} runtimeViewportHeight={600}><div className="realtime-console-stage"><div>战斗面板空白</div></div></ConsoleSurface>);

    const output = screen.getByText("战斗面板空白");
    const contextMenu = createEvent.contextMenu(output, { button: 2, clientX: 24, clientY: 12 });
    fireEvent(output, contextMenu);

    expect(contextMenu.defaultPrevented).toBe(true);
    expect(submitPointer).toHaveBeenCalledWith({ x: 0, y: 0, button: 2, pressed: true });
    expect(submitRightClick).not.toHaveBeenCalled();
  });

  it("does not duplicate a middle press handled by an active game button", () => {
    const submitPointer = vi.fn(() => true);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick: vi.fn(() => false), submitPointer } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef} runtimeViewportHeight={600}><div className="realtime-console-stage"><button className="console-choice" aria-disabled="false">自动</button></div></ConsoleSurface>);

    fireEvent.pointerDown(screen.getByRole("button", { name: "自动" }), { pointerId: 1, pointerType: "mouse", button: 1, clientX: 24, clientY: 12 });

    expect(submitPointer).not.toHaveBeenCalled();
  });

  it("suppresses the native context menu for ordinary game-stage output", () => {
    const submitRightClick = vi.fn(() => false);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}><main className="realtime-game-console"><div className="realtime-console-stage"><div>输出</div></div></main></ConsoleSurface>);
    const output = screen.getByText("输出");
    const contextMenu = createEvent.contextMenu(output, { button: 2, clientX: 24, clientY: 12 });
    fireEvent(output, contextMenu);

    expect(contextMenu.defaultPrevented).toBe(true);
    expect(submitRightClick).toHaveBeenCalledTimes(1);
  });

  it("keeps right-click message skip while suppressing the native context menu", () => {
    const submitRightClick = vi.fn(() => true);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}><main className="realtime-game-console"><div className="realtime-console-stage"><div>等待</div></div></main></ConsoleSurface>);
    const output = screen.getByText("等待");
    const contextMenu = createEvent.contextMenu(output, { button: 2, clientX: 24, clientY: 12 });
    fireEvent(output, contextMenu);

    expect(contextMenu.defaultPrevented).toBe(true);
    expect(submitRightClick).toHaveBeenCalledTimes(1);
  });

  it("sends right-click message skip even when the pointer is over a game button", () => {
    const submitRightClick = vi.fn(() => true);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}><main className="realtime-game-console"><div className="realtime-console-stage"><button type="button" disabled>游戏选项</button></div></main></ConsoleSurface>);
    const button = screen.getByRole("button", { name: "游戏选项" });
    const contextMenu = createEvent.contextMenu(button, { button: 2, clientX: 24, clientY: 12 });
    fireEvent(button, contextMenu);

    expect(contextMenu.defaultPrevented).toBe(true);
    expect(submitRightClick).toHaveBeenCalledTimes(1);
  });

  it("suppresses the native context menu across the whole console surface", () => {
    const submitRightClick = vi.fn(() => false);
    const promptControllerRef = { current: { submitBlankEnter: vi.fn(), submitRightClick } };
    render(<ConsoleSurface promptControllerRef={promptControllerRef}><main className="realtime-game-console"><div className="realtime-console-stage">输出</div><div className="console-input-dock"><input aria-label="游戏输入" /></div></main></ConsoleSurface>);
    const input = screen.getByRole("textbox", { name: "游戏输入" });
    const contextMenu = createEvent.contextMenu(input, { button: 2 });
    fireEvent(input, contextMenu);

    expect(contextMenu.defaultPrevented).toBe(true);
    expect(submitRightClick).not.toHaveBeenCalled();
  });
});

describe("console surface background", () => {
  it("applies the runtime default background to the whole surface", () => {
    expect(consoleSurfaceStyle({ red: 18, green: 52, blue: 86, alpha: 255 })).toEqual({ backgroundColor: "rgba(18, 52, 86, 1)" });
    expect(consoleSurfaceStyle(null)).toEqual({});
    expect(consoleBackgroundStyle({ red: 18, green: 52, blue: 86, alpha: 255 })).toEqual({ backgroundColor: "rgba(18, 52, 86, 1)" });
    expect(consoleBackgroundStyle(null)).toEqual({});
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

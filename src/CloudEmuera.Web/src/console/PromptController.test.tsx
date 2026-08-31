import { createRef } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { PromptController, type PromptControllerHandle } from "./PromptController";
import type { Prompt } from "../realtime/protocol";

function prompt(inputType: Prompt["inputType"]): Prompt {
  return {
    promptId: "prompt-1",
    inputType,
    promptText: "请输入",
    defaultValue: "默认值",
    constraints: { type: inputType === "integer" || inputType === "integerButton" ? "integer" : "text", maxLength: 20, minimum: null, maximum: null, allowSign: null, allowControlCharacters: null },
    timeoutBehavior: "wait",
    timeoutAction: "close",
    allowedSources: ["keyboard", "button", "pointer"],
    oneInput: false,
    systemInput: false,
    stopMessageSkip: false,
    displayTime: false,
    timeoutMessage: null,
    openedAtUnixMilliseconds: Date.now(),
    deadlineUnixMilliseconds: Date.now() + 30_000,
    timeoutMilliseconds: 30_000,
    buttonGeneration: 1,
  };
}

describe("PromptController", () => {
  it("submits anyKey through a keyboard key event and keeps pointer/button sources separate", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("anyKey")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    const control = screen.getByRole("button", { name: "按任意键继续" });
    fireEvent.keyDown(control, { key: "A", keyCode: 65, which: 65, ctrlKey: false, altKey: false, shiftKey: true });
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "KEYBOARD", value: "A", key: expect.objectContaining({ keyCode: 65, shift: true }) }));
    fireEvent.click(control);
    expect(onInput).toHaveBeenCalledTimes(1);
  });

  it.each(["enterKey", "anyKey"] as const)("submits a right pointer press for %s waits", inputType => {
    const onInput = vi.fn();
    const controller = createRef<PromptControllerHandle>();
    render(<PromptController ref={controller} prompt={prompt(inputType)} serverTimeOffsetMilliseconds={0} onInput={onInput} />);

    expect(controller.current?.submitRightClick({ x: 24, y: 12 })).toBe(true);
    expect(onInput).toHaveBeenCalledWith({ value: "", source: "POINTER", pointer: { x: 24, y: 12, button: 2, pressed: true } });
  });

  it("accepts all physical buttons for mouse-enabled value and primitive-pointer prompts", () => {
    const onInput = vi.fn();
    const controller = createRef<PromptControllerHandle>();
    const { rerender } = render(<PromptController ref={controller} prompt={prompt("text")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);

    expect(controller.current?.submitPointer?.({ x: 10, y: 20, button: 0, pressed: true })).toBe(true);
    expect(controller.current?.submitPointer?.({ x: 10, y: 20, button: 1, pressed: true })).toBe(true);
    expect(controller.current?.submitRightClick({ x: 10, y: 20 })).toBe(true);
    rerender(<PromptController ref={controller} prompt={prompt("primitivePointerKey")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    expect(controller.current?.submitPointer?.({ x: 30, y: 40, button: 1, pressed: true })).toBe(true);
    expect(onInput).toHaveBeenNthCalledWith(1, { value: "", source: "POINTER", pointer: { x: 10, y: 20, button: 0, pressed: true } });
    expect(onInput).toHaveBeenNthCalledWith(2, { value: "", source: "POINTER", pointer: { x: 10, y: 20, button: 1, pressed: true } });
    expect(onInput).toHaveBeenNthCalledWith(3, { value: "", source: "POINTER", pointer: { x: 10, y: 20, button: 2, pressed: true } });
    expect(onInput).toHaveBeenNthCalledWith(4, { value: "", source: "POINTER", pointer: { x: 30, y: 40, button: 1, pressed: true } });
  });

  it("rejects background pointer input when the runtime prompt did not enable mouse input", () => {
    const onInput = vi.fn();
    const controller = createRef<PromptControllerHandle>();
    render(<PromptController ref={controller} prompt={{ ...prompt("text"), allowedSources: ["keyboard", "button"] }} serverTimeOffsetMilliseconds={0} onInput={onInput} />);

    expect(controller.current?.submitPointer?.({ x: 10, y: 20, button: 0, pressed: true })).toBe(false);
    expect(onInput).not.toHaveBeenCalled();
  });

  it("does not use a waitOnly snapshot to block an input attempt", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("waitOnly")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    const input = screen.getByRole("textbox", { name: "游戏输入" });
    expect(input).toBeEnabled();
    fireEvent.submit(input.closest("form")!);
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "默认值" }));
  });

  it("keeps the text input beside the enter control for enterKey prompts", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("enterKey")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    expect(screen.getByRole("textbox", { name: "游戏输入" })).toHaveValue("默认值");
    expect(screen.getByRole("button", { name: "按回车继续" })).toHaveClass("is-ready");
    fireEvent.click(screen.getByRole("button", { name: "按回车继续" }));
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "默认值" }));
  });

  it("submits text when no prompt is displayed", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={null} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    const input = screen.getByRole("textbox", { name: "游戏输入" });
    expect(input).toBeEnabled();
    fireEvent.change(input, { target: { value: "continue" } });
    fireEvent.submit(input.closest("form")!);
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "continue" }));
  });

  it("sends a keyboard metadata object when a text form is submitted", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("text")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    fireEvent.change(screen.getByRole("textbox", { name: "游戏输入" }), { target: { value: "回答" } });
    fireEvent.submit(screen.getByRole("textbox", { name: "游戏输入" }).closest("form")!);
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "回答" }));
  });

  it("submits an empty text form as a current-slot input attempt", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("text")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    const input = screen.getByRole("textbox", { name: "游戏输入" });
    fireEvent.change(input, { target: { value: "" } });
    fireEvent.submit(input.closest("form")!);
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "" }));
  });

  it("submits an empty keyboard Enter for a blank console-surface click", () => {
    const onInput = vi.fn();
    const controller = createRef<PromptControllerHandle>();
    render(<PromptController ref={controller} prompt={{ ...prompt("text"), defaultValue: null }} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    controller.current?.submitBlankEnter();
    expect(onInput).toHaveBeenCalledWith({ source: "KEYBOARD", value: "", key: { keyCode: 13, control: false, alt: false, shift: false } });
  });

  it("does not submit a blank Enter when the input already has a value, but does not consult the displayed prompt sources", () => {
    const onInput = vi.fn();
    const controller = createRef<PromptControllerHandle>();
    const { rerender } = render(<PromptController ref={controller} prompt={prompt("text")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    controller.current?.submitBlankEnter();
    expect(onInput).not.toHaveBeenCalled();

    rerender(<PromptController ref={controller} prompt={{ ...prompt("text"), defaultValue: null, allowedSources: ["button"] }} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    controller.current?.submitBlankEnter();
    expect(onInput).toHaveBeenCalledWith({ source: "KEYBOARD", value: "", key: { keyCode: 13, control: false, alt: false, shift: false } });
  });

  it("does not use a displayed deadline to block an input attempt", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={{ ...prompt("text"), deadlineUnixMilliseconds: Date.now() - 1 }} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    const input = screen.getByRole("textbox", { name: "游戏输入" });
    expect(input).toBeEnabled();
    expect(screen.getByRole("button", { name: "发送" })).toHaveClass("is-ready");
    fireEvent.change(input, { target: { value: "late" } });
    fireEvent.submit(input.closest("form")!);
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "late" }));
  });

  it("renders integer constraints as native min/max bounds", () => {
    const integerPrompt = { ...prompt("integer"), constraints: { ...prompt("integer").constraints, minimum: -3, maximum: 9 } };
    render(<PromptController prompt={integerPrompt} serverTimeOffsetMilliseconds={0} onInput={() => undefined} />);
    expect(screen.getByRole("spinbutton", { name: "游戏输入" })).toHaveAttribute("min", "-3");
    expect(screen.getByRole("spinbutton", { name: "游戏输入" })).toHaveAttribute("max", "9");
  });

  it("applies integer bounds to the button-backed integer prompt too", () => {
    const integerButtonPrompt = { ...prompt("integerButton"), constraints: { ...prompt("integerButton").constraints, minimum: 1, maximum: 4 } };
    render(<PromptController prompt={integerButtonPrompt} serverTimeOffsetMilliseconds={0} onInput={() => undefined} />);
    expect(screen.getByRole("spinbutton", { name: "游戏输入" })).toHaveAttribute("min", "1");
    expect(screen.getByRole("spinbutton", { name: "游戏输入" })).toHaveAttribute("max", "4");
  });

  it("renders native runtime input with constrained controls while preserving its system flag", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={{ ...prompt("integer"), promptText: null, systemInput: true }} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    expect(screen.getByLabelText("游戏运行时输入")).toBeInTheDocument();
    expect(screen.queryByText("这是由游戏运行时处理的输入，不提供可伪造的浏览器控件。")).not.toBeInTheDocument();

    const input = screen.getByRole("spinbutton", { name: "游戏输入" });
    fireEvent.change(input, { target: { value: "2" } });
    fireEvent.submit(input.closest("form")!);
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "2" }));
  });

  it("keeps an untimed prompt enabled when its deadline sentinel is zero", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={{ ...prompt("text"), deadlineUnixMilliseconds: 0, timeoutMilliseconds: null }} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    const input = screen.getByRole("textbox", { name: "游戏输入" });
    expect(input).toBeEnabled();
    fireEvent.submit(input.closest("form")!);
    expect(onInput).toHaveBeenCalled();
  });
});

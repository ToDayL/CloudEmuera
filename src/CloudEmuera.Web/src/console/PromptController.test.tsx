import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { PromptController } from "./PromptController";
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

  it("renders waitOnly as a non-submitting status rather than a fake input control", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("waitOnly")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    expect(screen.getByRole("status")).toHaveTextContent("等待游戏继续");
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(onInput).not.toHaveBeenCalled();
  });

  it("renders an inline enter control for a prompt without choices", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("enterKey")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    expect(screen.queryByRole("textbox", { name: "游戏输入" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "按回车继续" }));
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "默认值" }));
  });

  it("sends a keyboard metadata object when a text form is submitted", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={prompt("text")} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    fireEvent.change(screen.getByRole("textbox", { name: "游戏输入" }), { target: { value: "回答" } });
    fireEvent.submit(screen.getByRole("textbox", { name: "游戏输入" }).closest("form")!);
    expect(onInput).toHaveBeenCalledWith(expect.objectContaining({ source: "BUTTON", value: "回答" }));
  });

  it("disables every submit control after the local deadline until the prompt changes", () => {
    const onInput = vi.fn();
    render(<PromptController prompt={{ ...prompt("text"), deadlineUnixMilliseconds: Date.now() - 1 }} serverTimeOffsetMilliseconds={0} onInput={onInput} />);
    const input = screen.getByRole("textbox", { name: "游戏输入" });
    expect(input).toBeDisabled();
    expect(screen.getByRole("button", { name: "等待游戏确认…" })).toBeDisabled();
    fireEvent.change(input, { target: { value: "late" } });
    fireEvent.submit(input.closest("form")!);
    expect(onInput).not.toHaveBeenCalled();
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

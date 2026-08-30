import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { AssetResolver } from "../console/AssetResolver";
import { PromptController } from "../console/PromptController";
import type { Prompt } from "../realtime/protocol";

function accessibilityViolations(root: HTMLElement): string[] {
  const violations: string[] = [];
  root.querySelectorAll("button, input, select, textarea").forEach(element => {
    const label = element.getAttribute("aria-label") || element.getAttribute("aria-labelledby") || (element.closest("label")?.textContent ?? "").trim() || (element.tagName === "BUTTON" ? element.textContent?.trim() : "");
    if (!label) violations.push(`${element.tagName.toLowerCase()} has no accessible name`);
  });
  root.querySelectorAll("img").forEach(element => { if (!element.hasAttribute("alt")) violations.push("img has no alt attribute"); });
  return violations;
}

const prompt: Prompt = {
  promptId: "a11y-prompt",
  inputType: "integer",
  promptText: "请输入数量",
  defaultValue: null,
  constraints: { type: "integer", maxLength: 6, minimum: 1, maximum: 9, allowSign: false, allowControlCharacters: false },
  timeoutBehavior: "wait",
  timeoutAction: "close",
  allowedSources: ["keyboard", "button"],
  oneInput: false,
  systemInput: false,
  stopMessageSkip: false,
  displayTime: true,
  timeoutMessage: null,
  openedAtUnixMilliseconds: Date.now(),
  deadlineUnixMilliseconds: Date.now() + 10_000,
  timeoutMilliseconds: 10_000,
  buttonGeneration: 1,
};

describe("browser accessibility smoke checks", () => {
  it("keeps the live prompt controls named and bounded", () => {
    const { container } = render(<PromptController prompt={prompt} serverTimeOffsetMilliseconds={0} onInput={() => undefined} />);
    expect(accessibilityViolations(container)).toEqual([]);
    expect(screen.getByRole("spinbutton", { name: "游戏输入" })).toHaveAttribute("min", "1");
  });
});

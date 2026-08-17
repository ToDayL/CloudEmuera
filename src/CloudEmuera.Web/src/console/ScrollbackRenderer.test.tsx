import { fireEvent, render, screen } from "@testing-library/react";
import { createRef } from "react";
import { describe, expect, it, vi } from "vitest";
import { AssetResolver } from "./AssetResolver";
import { ScrollbackRenderer, trimTrailingEmptyLines } from "./ScrollbackRenderer";
import type { RealtimeLine } from "../realtime/protocol";

const assets = new AssetResolver("s1", { schemaVersion: 1, assets: [], fonts: [], fontDiagnostics: [] });
const line = (id: string, text: string): RealtimeLine => ({ lineId: id, nodes: [{ type: "text", text, style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], alignment: "left", temporary: false });

function scrollContainer(atLatest: boolean) {
  const ref = createRef<HTMLElement>();
  const element = document.createElement("main");
  Object.defineProperties(element, {
    clientHeight: { configurable: true, value: 100 },
    scrollHeight: { configurable: true, writable: true, value: 200 },
    scrollTop: { configurable: true, writable: true, value: atLatest ? 100 : 10 },
  });
  Object.assign(element, { scrollTo: vi.fn() });
  ref.current = element;
  return { ref, element };
}

describe("ScrollbackRenderer", () => {
  it("follows new output to the bottom while the reader is at the latest position", () => {
    const { ref, element } = scrollContainer(true);
    const view = render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    view.rerender(<ScrollbackRenderer lines={[line("one", "one"), line("two", "two")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    expect(element.scrollTo).toHaveBeenCalledWith({ top: 100, behavior: "auto" });
  });

  it("follows new output even when the reader was above the latest position", () => {
    const { ref, element } = scrollContainer(false);
    const view = render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    element.scrollTop = 10;
    fireEvent.scroll(element);
    view.rerender(<ScrollbackRenderer lines={[line("one", "one"), line("two", "two")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 100, behavior: "auto" });
  });

  it("shows a back-to-latest control after the reader scrolls upward", () => {
    const { ref, element } = scrollContainer(true);
    render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    element.scrollTop = 10;
    fireEvent.scroll(element);
    const button = screen.getByRole("button", { name: "↓ 回到最新" });
    fireEvent.click(button);
    expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 100, behavior: "smooth" });
  });

  it("does not render the protocol cursor line left after a trailing line break", () => {
    const empty = line("cursor", "");
    const visible = trimTrailingEmptyLines([line("one", "one"), empty]);
    expect(visible.map(item => item.lineId)).toEqual(["one"]);
    expect(trimTrailingEmptyLines([line("one", "one"), line("blank", ""), empty])).toHaveLength(2);
    render(<ScrollbackRenderer lines={[line("one", "one"), empty]} assets={assets} onInput={() => undefined} />);
    expect(document.querySelectorAll(".console-line")).toHaveLength(1);
  });

  it("only enables choice buttons from the current runtime generation", () => {
    const onInput = vi.fn();
    const choiceLine = (id: string, generation: number, value: string): RealtimeLine => ({
      lineId: id,
      nodes: [{ type: "button", children: [{ type: "text", text: value, style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], value, tooltip: null, enabled: true, generation }],
      alignment: "left",
      temporary: false,
    });
    render(<ScrollbackRenderer lines={[choiceLine("old", 1, "old"), choiceLine("current", 2, "current")]} currentPrompt={{ promptId: "p2", inputType: "integer", promptText: null, defaultValue: null, constraints: { type: "integer" }, timeoutBehavior: "wait", timeoutAction: "close", allowedSources: ["button"], oneInput: false, systemInput: false, stopMessageSkip: false, displayTime: false, timeoutMessage: null, openedAtUnixMilliseconds: 0, deadlineUnixMilliseconds: 0, timeoutMilliseconds: null }} assets={assets} onInput={onInput} />);
    const buttons = screen.getAllByRole("button");
    expect(buttons[0]).toBeDisabled();
    expect(buttons[1]).toBeEnabled();
    fireEvent.click(buttons[0]);
    fireEvent.click(buttons[1]);
    expect(onInput).toHaveBeenCalledTimes(1);
    expect(onInput).toHaveBeenCalledWith({ value: "current", source: "BUTTON" });
  });

  it("keeps PRINTC padding outside the underlined and clickable label", () => {
    const choiceLine: RealtimeLine = {
      lineId: "padded-choice",
      nodes: [{ type: "button", children: [{ type: "text", text: "   ACTION   ", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], value: "action", tooltip: null, enabled: true, generation: 1 }],
      alignment: "left",
      temporary: false,
    };
    render(<ScrollbackRenderer lines={[choiceLine]} currentPrompt={{ promptId: "p1", inputType: "integer", promptText: null, defaultValue: null, constraints: { type: "integer" }, timeoutBehavior: "wait", timeoutAction: "close", allowedSources: ["button"], oneInput: false, systemInput: false, stopMessageSkip: false, displayTime: false, timeoutMessage: null, openedAtUnixMilliseconds: 0, deadlineUnixMilliseconds: 0, timeoutMilliseconds: null }} assets={assets} onInput={() => undefined} />);

    const button = screen.getByRole("button", { name: "ACTION" });
    expect(button.textContent).toBe("ACTION");
    expect(button.querySelector(".console-choice-label")?.textContent).toBe("ACTION");
    expect(button.previousSibling?.textContent).toBe("   ");
    expect(button.nextSibling?.textContent).toBe("   ");
  });
});

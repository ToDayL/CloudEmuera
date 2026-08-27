import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { TooltipPresentation } from "../realtime/protocol";
import { ConsoleTooltipProvider, useConsoleTooltipTarget } from "./TooltipLayer";

const presentation: TooltipPresentation = {
  customEnabled: true,
  foreground: { red: 10, green: 20, blue: 30, alpha: 255 },
  background: { red: 240, green: 241, blue: 242, alpha: 255 },
  delayMilliseconds: 200,
  durationMilliseconds: 0,
  fontFamily: "session-default",
  fontSize: 16,
  textFormat: { horizontal: "left", vertical: "top", wrap: true, trimming: "none", expandTabs: false, rightToLeft: false },
  imageMode: false,
  revision: 1,
};

afterEach(() => vi.useRealTimers());

describe("ConsoleTooltipProvider", () => {
  it("uses a zero-layout badge and opens pure text immediately on keyboard focus", () => {
    render(<Fixture tooltip={'line 1<br><img src="x">'} />);
    const target = screen.getByRole("button", { name: /choice/i });
    expect(target).not.toHaveAttribute("title");
    expect(target.querySelector(".console-tooltip-badge")).toHaveAttribute("aria-hidden", "true");

    fireEvent.focus(target);
    const tooltip = screen.getByRole("tooltip");
    expect(tooltip).toHaveTextContent(/line 1\s+<img src="x">/);
    expect(tooltip.innerHTML).not.toContain("<img src");
    expect(target).toHaveAttribute("aria-describedby", tooltip.id);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("tooltip")).toBeNull();
  });

  it("applies the game hover delay without creating per-target timers", () => {
    vi.useFakeTimers();
    render(<Fixture tooltip="delayed" />);
    const target = screen.getByRole("button", { name: /choice/i });
    target.getBoundingClientRect = () => rect(10, 10, 80, 30);
    fireEvent.pointerMove(target, { pointerType: "mouse", clientX: 20, clientY: 20, buttons: 0 });
    act(() => vi.advanceTimersByTime(100));
    fireEvent.pointerMove(target, { pointerType: "mouse", clientX: 30, clientY: 20, buttons: 0 });
    expect(screen.queryByRole("tooltip")).toBeNull();
    act(() => vi.advanceTimersByTime(99));
    expect(screen.queryByRole("tooltip")).toBeNull();
    act(() => vi.advanceTimersByTime(1));
    expect(screen.getByRole("tooltip")).toHaveTextContent("delayed");

    fireEvent.keyDown(document, { key: "Escape" });
    fireEvent.pointerMove(target, { pointerType: "mouse", clientX: 40, clientY: 20, buttons: 0 });
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole("tooltip")).toBeNull();
  });

  it("suppresses a qualified touch badge activation but preserves a small target tap", () => {
    const onInput = vi.fn();
    const view = render(<Fixture tooltip="touch" onInput={onInput} />);
    let target = screen.getByRole("button", { name: /choice/i });
    target.getBoundingClientRect = () => rect(10, 10, 80, 30);
    fireEvent.pointerDown(target, { pointerType: "touch", pointerId: 1, clientX: 85, clientY: 15 });
    fireEvent.pointerUp(target, { pointerType: "touch", pointerId: 1, clientX: 85, clientY: 15 });
    fireEvent.click(target, { detail: 1, clientX: 85, clientY: 15 });
    expect(screen.getByRole("tooltip")).toHaveClass("is-pinned");
    expect(onInput).not.toHaveBeenCalled();

    view.unmount();
    render(<Fixture tooltip="small" onInput={onInput} />);
    target = screen.getByRole("button", { name: /choice/i });
    target.getBoundingClientRect = () => rect(10, 10, 30, 20);
    fireEvent.pointerDown(target, { pointerType: "touch", pointerId: 2, clientX: 35, clientY: 15 });
    fireEvent.pointerUp(target, { pointerType: "touch", pointerId: 2, clientX: 35, clientY: 15 });
    fireEvent.click(target, { detail: 1, clientX: 35, clientY: 15 });
    expect(onInput).toHaveBeenCalledTimes(1);
  });

  it("opens on a 500ms touch hold and consumes the following synthetic click once", () => {
    vi.useFakeTimers();
    const onInput = vi.fn();
    render(<Fixture tooltip="hold" onInput={onInput} />);
    const target = screen.getByRole("button", { name: /choice/i });
    target.getBoundingClientRect = () => rect(10, 10, 30, 20);
    fireEvent.pointerDown(target, { pointerType: "touch", pointerId: 3, clientX: 20, clientY: 20 });
    act(() => vi.advanceTimersByTime(500));
    fireEvent.click(target, { detail: 1, clientX: 20, clientY: 20 });
    expect(screen.getByRole("tooltip")).toHaveClass("is-pinned");
    expect(onInput).not.toHaveBeenCalled();
    fireEvent.click(target, { detail: 1, clientX: 20, clientY: 20 });
    expect(onInput).toHaveBeenCalledTimes(1);
  });

  it("opens an input-transparent target on an ordinary touch tap", () => {
    render(<ConsoleTooltipProvider presentation={presentation} resources={[]}><TransparentTarget /></ConsoleTooltipProvider>);
    const target = screen.getByText("note");
    target.getBoundingClientRect = () => rect(10, 10, 30, 20);
    fireEvent.pointerDown(target, { pointerType: "touch", pointerId: 4, clientX: 20, clientY: 20 });
    fireEvent.pointerUp(target, { pointerType: "touch", pointerId: 4, clientX: 20, clientY: 20 });
    fireEvent.click(target, { detail: 1, clientX: 20, clientY: 20 });
    expect(screen.getByRole("tooltip")).toHaveClass("is-pinned");
  });

  it("resolves a nested target by renderer paint order instead of effect registration order", () => {
    vi.useFakeTimers();
    render(<ConsoleTooltipProvider presentation={presentation} resources={[]}><NestedTargets /></ConsoleTooltipProvider>);
    const parent = screen.getByTestId("tooltip-parent");
    const child = screen.getByRole("button", { name: /nested choice/i });
    parent.getBoundingClientRect = () => rect(0, 0, 120, 40);
    child.getBoundingClientRect = () => rect(10, 5, 80, 30);

    fireEvent.pointerMove(child, { pointerType: "mouse", clientX: 20, clientY: 20, buttons: 0 });
    act(() => vi.advanceTimersByTime(200));

    expect(screen.getByRole("tooltip")).toHaveTextContent("child tooltip");
  });

  it("keeps product controls outside delegated touch arbitration", () => {
    render(<Fixture tooltip="under toolbar" />);
    const target = screen.getByRole("button", { name: /choice/i });
    target.getBoundingClientRect = () => rect(0, 0, 300, 100);
    const inspect = screen.getByRole("button", { name: "查看提示" });

    fireEvent.pointerDown(inspect, { pointerType: "touch", pointerId: 8, clientX: 20, clientY: 20 });
    fireEvent.pointerUp(inspect, { pointerType: "touch", pointerId: 8, clientX: 20, clientY: 20 });
    fireEvent.click(inspect, { detail: 1, clientX: 20, clientY: 20 });

    expect(inspect).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByRole("tooltip")).toBeNull();
  });

  it("makes inspect mode suppress mouse activation on hybrid devices", () => {
    const onInput = vi.fn();
    render(<Fixture tooltip="hybrid" onInput={onInput} />);
    const target = screen.getByRole("button", { name: /choice/i });
    target.getBoundingClientRect = () => rect(10, 10, 80, 30);
    fireEvent.click(screen.getByRole("button", { name: "查看提示" }), { detail: 1 });

    fireEvent.click(target, { detail: 1, clientX: 30, clientY: 20 });

    expect(screen.getByRole("tooltip")).toHaveClass("is-pinned");
    expect(onInput).not.toHaveBeenCalled();
  });

  it("projects vertical alignment and bounded path trimming", () => {
    const formatted = {
      ...presentation,
      textFormat: { ...presentation.textFormat, vertical: "bottom" as const, trimming: "pathEllipsis" as const, wrap: false },
    };
    render(<ConsoleTooltipProvider presentation={formatted} resources={[]}><Target tooltip="a/very/long/path/file.txt" onInput={() => undefined} /></ConsoleTooltipProvider>);
    fireEvent.focus(screen.getByRole("button", { name: /choice/i }));

    const tooltip = screen.getByRole("tooltip");
    expect(tooltip).toHaveStyle({ alignContent: "end", overflow: "hidden" });
    expect(tooltip.querySelector(".console-tooltip-path-ellipsis")).not.toBeNull();
  });
});

function Fixture({ tooltip, onInput = () => undefined }: { tooltip: string; onInput?: () => void }) {
  return <ConsoleTooltipProvider presentation={presentation} resources={[]}><Target tooltip={tooltip} onInput={onInput} /></ConsoleTooltipProvider>;
}

function Target({ tooltip, onInput }: { tooltip: string; onInput: () => void }) {
  const target = useConsoleTooltipTarget(tooltip, 7);
  return <button ref={target.ref as React.RefCallback<HTMLButtonElement>} {...target.props} type="button" onClick={onInput}>choice{target.badge}</button>;
}

function TransparentTarget() {
  const target = useConsoleTooltipTarget("transparent", 1, true);
  return <span ref={target.ref as React.RefCallback<HTMLSpanElement>} {...target.props}>note{target.badge}</span>;
}

function NestedTargets() {
  const parent = useConsoleTooltipTarget("parent tooltip", 1, true);
  const child = useConsoleTooltipTarget("child tooltip", 2);
  return <span data-testid="tooltip-parent" ref={parent.ref as React.RefCallback<HTMLSpanElement>}>
    parent
    <button ref={child.ref as React.RefCallback<HTMLButtonElement>} {...child.props} type="button">nested choice{child.badge}</button>
    {parent.badge}
  </span>;
}

function rect(left: number, top: number, width: number, height: number): DOMRect {
  return { left, top, right: left + width, bottom: top + height, width, height, x: left, y: top, toJSON: () => ({}) } as DOMRect;
}

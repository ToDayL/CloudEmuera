import { fireEvent, render, screen } from "@testing-library/react";
import { createRef } from "react";
import { describe, expect, it, vi } from "vitest";
import { AssetResolver } from "./AssetResolver";
import { ScrollbackRenderer, leadingVisualOverflow, trailingVisualOverflow, trimTrailingEmptyLines } from "./ScrollbackRenderer";
import type { RealtimeLine, RealtimeNode } from "../realtime/protocol";

const assets = new AssetResolver("s1");
const clockAssets = new AssetResolver("s1");
const line = (id: string, text: string): RealtimeLine => ({ lineId: id, nodes: [{ type: "text", text, style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], alignment: "left", temporary: false });

function scrollContainer(atLatest: boolean) {
  const ref = createRef<HTMLElement>();
  const element = document.createElement("main");
  Object.defineProperties(element, {
    clientHeight: { configurable: true, value: 100 },
    offsetHeight: { configurable: true, value: 100 },
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

  it("does not pull the reader to the bottom when a frame arrives above the latest position", () => {
    const { ref, element } = scrollContainer(false);
    const view = render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    element.scrollTop = 10;
    fireEvent.scroll(element);
    vi.mocked(element.scrollTo).mockClear();
    view.rerender(<ScrollbackRenderer lines={[line("one", "one"), line("two", "two")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    expect(element.scrollTo).not.toHaveBeenCalled();
  });

  it("does not pull the reader to the bottom for a reconnect phase change while above the latest position", () => {
    const { ref, element } = scrollContainer(false);
    const view = render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} scrollVersion="ready:live:1:12" />);
    element.scrollTop = 10;
    fireEvent.scroll(element);
    vi.mocked(element.scrollTo).mockClear();
    view.rerender(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} scrollVersion="ready:resuming:1:12" />);
    expect(element.scrollTo).not.toHaveBeenCalled();
  });

  it("always scrolls for input and keeps following the next output frame", () => {
    const { ref, element } = scrollContainer(false);
    const view = render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} forceScrollVersion={0} />);
    element.scrollTop = 10;
    fireEvent.scroll(element);
    vi.mocked(element.scrollTo).mockClear();

    view.rerender(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} forceScrollVersion={1} />);
    expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 100, behavior: "auto" });

    (element as HTMLElement & { scrollHeight: number }).scrollHeight = 300;
    view.rerender(<ScrollbackRenderer lines={[line("one", "one"), line("two", "two")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} scrollVersion={1} forceScrollVersion={1} />);
    expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 200, behavior: "auto" });
  });

  it("re-synchronizes after the latest content changes its measured height", () => {
    let notifyResize: (() => void) | undefined;
    class ResizeObserverStub {
      constructor(callback: ResizeObserverCallback) {
        notifyResize = () => callback([], this as unknown as ResizeObserver);
      }
      observe() {}
      disconnect() {}
      unobserve() {}
    }
    vi.stubGlobal("ResizeObserver", ResizeObserverStub);
    try {
      const { ref, element } = scrollContainer(true);
      render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
      vi.mocked(element.scrollTo).mockClear();
      (element as HTMLElement & { scrollHeight: number }).scrollHeight = 500;
      notifyResize?.();

      expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 400, behavior: "auto" });
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("keeps following a large frame when the virtual content settles after the initial jump", () => {
    const observed: Array<{ target: Element; callback: ResizeObserverCallback }> = [];
    class ResizeObserverStub {
      constructor(private readonly callback: ResizeObserverCallback) {}
      observe(target: Element) {
        observed.push({ target, callback: this.callback });
      }
      disconnect() {}
      unobserve() {}
    }
    vi.stubGlobal("ResizeObserver", ResizeObserverStub);
    vi.stubGlobal("requestAnimationFrame", undefined);
    try {
      const { ref, element } = scrollContainer(true);
      const initial = [line("initial", "initial")];
      const view = render(<ScrollbackRenderer lines={initial} assets={assets} onInput={() => undefined} scrollContainerRef={ref} defaultLineHeight={20} />);
      vi.mocked(element.scrollTo).mockClear();

      const largeFrame = Array.from({ length: 4096 }, (_, index) => line(`large-${index}`, `large frame line ${index}`));
      view.rerender(<ScrollbackRenderer lines={largeFrame} assets={assets} onInput={() => undefined} scrollContainerRef={ref} defaultLineHeight={20} scrollVersion="large-frame" />);
      expect(element.scrollTop).toBe(100);

      const virtualContent = document.querySelector<HTMLElement>(".console-virtual-content");
      expect(virtualContent).not.toBeNull();
      const virtualContentObserver = observed.find(item => item.target === virtualContent);
      expect(virtualContentObserver).toBeDefined();

      (element as HTMLElement & { scrollHeight: number }).scrollHeight = 82_020;
      virtualContentObserver?.callback([], {} as ResizeObserver);

      expect(element.scrollTop).toBe(81_920);
      expect(document.querySelector<HTMLButtonElement>(".scrollback-latest")).toHaveClass("is-hidden");
      view.unmount();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("does not force the reader to the bottom from a content resize", () => {
    const resizeCallbacks: ResizeObserverCallback[] = [];
    class ResizeObserverStub {
      constructor(callback: ResizeObserverCallback) {
        resizeCallbacks.push(callback);
      }
      observe() {}
      disconnect() {}
      unobserve() {}
    }
    vi.stubGlobal("ResizeObserver", ResizeObserverStub);
    try {
      const { ref, element } = scrollContainer(false);
      render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
      element.scrollTop = 10;
      fireEvent.scroll(element);
      vi.mocked(element.scrollTo).mockClear();
      (element as HTMLElement & { scrollHeight: number }).scrollHeight = 500;
      for (const callback of resizeCallbacks) callback([], {} as ResizeObserver);
      expect(element.scrollTo).not.toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("jumps to the latest output after the reader scrolls upward", () => {
    const { ref, element } = scrollContainer(true);
    render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    element.scrollTop = 10;
    fireEvent.scroll(element);
    const button = screen.getByRole("button", { name: "↓ 回到最新" });
    fireEvent.click(button);
    expect(element.scrollTop).toBe(100);
    expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 100, behavior: "auto" });
  });

  it("keeps the latest control mounted while only toggling its visibility", () => {
    const { ref, element } = scrollContainer(true);
    render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    const button = document.querySelector<HTMLButtonElement>(".scrollback-latest");
    expect(button).not.toBeNull();
    expect(button).toHaveClass("is-hidden");

    element.scrollTop = 10;
    fireEvent.scroll(element);
    expect(document.querySelector(".scrollback-latest")).toBe(button);
    expect(button).not.toHaveClass("is-hidden");

    element.scrollTop = 100;
    fireEvent.scroll(element);
    expect(document.querySelector(".scrollback-latest")).toBe(button);
    expect(button).toHaveClass("is-hidden");

    element.scrollTop = 76;
    fireEvent.scroll(element);
    expect(button).not.toHaveClass("is-hidden");

    element.scrollTop = 99;
    fireEvent.scroll(element);
    expect(button).toHaveClass("is-hidden");
  });

  it("settles at the latest bottom after a late content-height update", () => {
    const callbacks: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      callbacks.push(callback);
      return callbacks.length;
    });
    vi.stubGlobal("cancelAnimationFrame", vi.fn());

    try {
      const { ref, element } = scrollContainer(true);
      const lines = [line("one", "one")];
      const view = render(<ScrollbackRenderer lines={lines} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
      callbacks.length = 0;
      vi.mocked(element.scrollTo).mockClear();

      let scrollCalls = 0;
      vi.mocked(element.scrollTo).mockImplementation(() => {
        scrollCalls += 1;
        if (scrollCalls === 1) {
          (element as HTMLElement & { scrollHeight: number }).scrollHeight = 300;
        }
      });

      view.rerender(<ScrollbackRenderer lines={lines} assets={assets} onInput={() => undefined} scrollContainerRef={ref} forceScrollVersion={1} />);

      expect(element.scrollTop).toBe(100);
      expect(callbacks).toHaveLength(1);

      callbacks.shift()?.(0);

      expect(element.scrollTop).toBe(200);
      view.unmount();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("mounts only the viewport plus one screen of physical lines", () => {
    const { ref } = scrollContainer(true);
    const lines = Array.from({ length: 200 }, (_, index) => line(`line-${index}`, `line ${index}`));

    render(<ScrollbackRenderer lines={lines} assets={assets} onInput={() => undefined} scrollContainerRef={ref} defaultLineHeight={20} />);

    const renderedLines = document.querySelectorAll(".console-line");
    expect(renderedLines.length).toBeGreaterThan(0);
    expect(renderedLines.length).toBeLessThan(lines.length);
    expect(document.querySelector<HTMLElement>(".console-virtual-content")).toHaveStyle({ height: "4000px" });
  });

  it("keeps a portrait mounted when its visual overflow enters the viewport", () => {
    vi.stubGlobal("requestAnimationFrame", undefined);
    try {
      const { ref, element } = scrollContainer(true);
      const lines = Array.from({ length: 100 }, (_, index) => line(`line-${index}`, `line ${index}`));
      const portraitLine: RealtimeLine = {
        lineId: "portrait-line",
        nodes: [{ type: "sprite", assetId: "path-YXNzZXQ", sourceRect: { x: 0, y: 0, width: 180, height: 600 }, destination: { x: 0, y: -400, width: 180, height: 600 }, frame: 0, zIndex: 0, opacity: 1, altText: "portrait", hoverAssetId: null, hoverSourceRect: null, mappingAssetId: null, mappingSourceRect: null, animationFrames: [] }],
        alignment: "left",
        temporary: false,
        lineHeight: 20,
      };
      const initial = render(<ScrollbackRenderer lines={[]} assets={clockAssets} onInput={() => undefined} scrollContainerRef={ref} defaultLineHeight={20} />);
      (element as HTMLElement & { scrollHeight: number }).scrollHeight = 2_000;
      element.scrollTop = 0;
      fireEvent.scroll(element);
      initial.rerender(<ScrollbackRenderer lines={[...lines.slice(0, 20), portraitLine, ...lines.slice(21)]} assets={clockAssets} onInput={() => undefined} scrollContainerRef={ref} defaultLineHeight={20} />);

      expect(screen.getByRole("img", { name: "portrait" })).toBeInTheDocument();
      expect(document.querySelectorAll(".console-line").length).toBeGreaterThan(11);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("does not render the protocol cursor line left after a trailing line break", () => {
    const empty = line("cursor", "");
    const stableLines = [line("stable", "stable")];
    expect(trimTrailingEmptyLines(stableLines)).toBe(stableLines);
    const visible = trimTrailingEmptyLines([line("one", "one"), empty]);
    expect(visible.map(item => item.lineId)).toEqual(["one"]);
    expect(trimTrailingEmptyLines([line("one", "one"), line("blank", ""), empty])).toHaveLength(2);
    render(<ScrollbackRenderer lines={[line("one", "one"), empty]} assets={assets} onInput={() => undefined} />);
    expect(document.querySelectorAll(".console-line")).toHaveLength(1);
  });

  it("applies physical line height as a CSS pixel length", () => {
    render(<ScrollbackRenderer lines={[{ ...line("measured", "measured"), layoutWidth: 760, lineHeight: 19 }]} assets={assets} onInput={() => undefined} />);
    const element = document.querySelector<HTMLElement>(".console-line");
    expect(element).not.toBeNull();
    expect(element).toHaveStyle({ width: "760px", height: "19px", minHeight: "19px", lineHeight: "19px" });
  });

  it("projects eraTW ambiguous map glyphs into one CJK cell", () => {
    render(<ScrollbackRenderer lines={[line("map", "■│12☆")]} assets={assets} onInput={() => undefined} />);
    expect(document.querySelectorAll(".console-era-wide-cell")).toHaveLength(3);
    expect(document.querySelectorAll(".console-era-wide-line")).toHaveLength(1);
    expect(document.querySelectorAll(".console-era-wide-shape")).toHaveLength(2);
    expect(document.querySelector<HTMLElement>(".console-line")).toHaveTextContent("■│12☆");
  });

  it("keeps escaped portrait pixels inside the scrollable scrollback extent", () => {
    const portraitLine: RealtimeLine = {
      lineId: "portrait-line",
      nodes: [{ type: "sprite", assetId: "path-YXNzZXQ", sourceRect: { x: 0, y: 0, width: 180, height: 180 }, destination: { x: 0, y: 32, width: 180, height: 180 }, frame: 0, zIndex: 0, opacity: 1, altText: "portrait", hoverAssetId: null, hoverSourceRect: null, mappingAssetId: null, mappingSourceRect: null, animationFrames: [] }],
      alignment: "left",
      temporary: false,
      lineHeight: 20,
    };
    const positionedPortraitLine = {
      ...portraitLine,
      nodes: [{ type: "positionedInlineSegment" as const, positionX: 0, measuredWidth: 180, action: null, children: portraitLine.nodes }],
    };

    expect(trailingVisualOverflow([portraitLine])).toBe(192);
    render(<ScrollbackRenderer lines={[positionedPortraitLine]} assets={clockAssets} onInput={() => undefined} />);
    expect(document.querySelector<HTMLElement>(".console-virtual-content")).toHaveStyle({ height: "212px" });
    expect(document.querySelector<HTMLElement>(".positioned-inline-segment")).toHaveStyle({ overflow: "visible" });
  });

  it("reserves leading space for a title image with a negative ypos", () => {
    const titleLine: RealtimeLine = {
      lineId: "title-line",
      nodes: [{ type: "sprite", assetId: "path-YXNzZXQ", sourceRect: { x: 0, y: 0, width: 486, height: 648 }, destination: { x: 0, y: -594, width: 486, height: 648 }, frame: 0, zIndex: 0, opacity: 1, altText: "title", hoverAssetId: null, hoverSourceRect: null, mappingAssetId: null, mappingSourceRect: null, animationFrames: [] }],
      alignment: "left",
      temporary: false,
      lineHeight: 20,
    };

    expect(leadingVisualOverflow([titleLine])).toBe(594);
    render(<ScrollbackRenderer lines={[titleLine]} assets={clockAssets} onInput={() => undefined} />);
    expect(document.querySelector<HTMLElement>(".console-virtual-content")).toHaveStyle({ height: "648px" });
  });

  it("submits enabled choice buttons from old display frames to the current input slot", () => {
    const onInput = vi.fn();
    const choiceLine = (id: string, generation: number, value: string): RealtimeLine => ({
      lineId: id,
      nodes: [{ type: "button", children: [{ type: "text", text: value, style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], value, tooltip: null, enabled: true, generation }],
      alignment: "left",
      temporary: false,
    });
    render(<ScrollbackRenderer lines={[choiceLine("old", 1, "old"), choiceLine("current", 2, "current")]} assets={assets} onInput={onInput} />);
    const buttons = screen.getAllByRole("button");
    expect(buttons[0]).toBeEnabled();
    expect(buttons[1]).toBeEnabled();
    fireEvent.click(buttons[0]);
    fireEvent.click(buttons[1]);
    expect(onInput).toHaveBeenCalledTimes(2);
    expect(onInput).toHaveBeenNthCalledWith(1, { value: "old", source: "BUTTON" });
    expect(onInput).toHaveBeenCalledWith({ value: "current", source: "BUTTON" });
  });

  it("keeps PRINTC padding outside the underlined and clickable label", () => {
    const choiceLine: RealtimeLine = {
      lineId: "padded-choice",
      nodes: [{ type: "button", children: [{ type: "text", text: "   ACTION   ", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], value: "action", tooltip: null, enabled: true, generation: 1 }],
      alignment: "left",
      temporary: false,
    };
    render(<ScrollbackRenderer lines={[choiceLine]} assets={assets} onInput={() => undefined} />);

    const button = screen.getByRole("button", { name: "ACTION" });
    expect(button.textContent).toBe("ACTION");
    expect(button.querySelector(".console-choice-label")?.textContent).toBe("ACTION");
    expect(button.previousSibling?.textContent).toBe("   ");
    expect(button.nextSibling?.textContent).toBe("   ");
  });

  it("keeps the ERB foreground color on each button label", () => {
    const color = (red: number, green: number, blue: number) => ({ red, green, blue, alpha: 255 });
    const styledLine: RealtimeLine = {
      lineId: "styled-buttons",
      nodes: [
        { type: "button", children: [{ type: "text", text: "明亮", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: color(255, 255, 255), background: null, buttonColor: color(255, 255, 0) } }], value: "bright", tooltip: null, enabled: false, generation: 0 },
        { type: "button", children: [{ type: "text", text: "暗い", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: color(96, 96, 96), background: null, buttonColor: color(255, 255, 0) } }], value: "dark", tooltip: null, enabled: false, generation: 0 },
      ],
      alignment: "left",
      temporary: false,
    };

    render(<ScrollbackRenderer lines={[styledLine]} assets={assets} onInput={() => undefined} />);

    const labels = screen.getAllByRole("button").map(button => button.querySelector(".console-text"));
    expect(labels[0]).toHaveStyle({ color: "rgb(255, 255, 255)" });
    expect(labels[1]).toHaveStyle({ color: "rgb(96, 96, 96)" });
  });

  it("keeps sprite destination offsets without inflating the text line height", () => {
    const spriteLine: RealtimeLine = {
      lineId: "clock-line",
      nodes: [{ type: "sprite", assetId: "path-YXNzZXQ", sourceRect: { x: 0, y: 0, width: 54, height: 16 }, destination: { x: 12, y: 4, width: 54, height: 16 }, frame: 0, zIndex: 0, opacity: 1, altText: "clock", hoverAssetId: null, hoverSourceRect: null, mappingAssetId: null, mappingSourceRect: null, animationFrames: [] }],
      alignment: "right",
      temporary: false,
    };

    render(<ScrollbackRenderer lines={[spriteLine]} assets={clockAssets} onInput={() => undefined} />);

    const slot = document.querySelector(".console-sprite-slot") as HTMLElement;
    expect(slot).not.toBeNull();
    // The desktop ConsoleImagePart is an overlay: it keeps its horizontal
    // footprint but must not push the following display lines down.
    expect(slot.style.position).toBe("relative");
    expect(slot.style.display).toBe("inline-block");
    expect(slot.style.width).toBe("54px");
    expect(slot.style.height).toBe("0px");
    expect(slot.style.overflow).toBe("visible");
    expect(screen.getByRole("img", { name: "clock" })).toHaveStyle({ left: "12px", top: "4px", position: "absolute" });
  });

  it("renders native Emuera div layout and button x positioning", () => {
    const stateLine: RealtimeLine = {
      lineId: "div-line",
      nodes: [{
        type: "div",
        children: [{ type: "button", children: [{ type: "text", text: "Go", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], value: "go", tooltip: null, enabled: false, generation: 0, positionX: 42 }],
        bounds: { x: 1, y: 2, width: 30, height: 12 },
        zIndex: 3,
        background: { red: 1, green: 2, blue: 3, alpha: 255 },
        isRelative: false,
        box: null,
      }],
      alignment: "left",
      temporary: false,
    };
    render(<ScrollbackRenderer lines={[stateLine]} assets={assets} onInput={() => undefined} />);
    const div = document.querySelector(".console-emuera-div") as HTMLElement;
    expect(div).toBeInTheDocument();
    expect(div.style.position).toBe("absolute");
    expect(div.style.left).toBe("1px");
    expect(screen.getByRole("button", { name: "Go" })).toHaveStyle({ position: "relative", left: "42px" });
  });

  it("keeps buttons clickable inside pointer-transparent Emuera div layers", () => {
    const onInput = vi.fn();
    const stateLine: RealtimeLine = {
      lineId: "nested-div-button",
      nodes: [{
        type: "positionedInlineSegment",
        positionX: 0,
        measuredWidth: 0,
        action: null,
        children: [{
          type: "div",
          children: [{
            type: "button",
            children: [{ type: "text", text: "Invite", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }],
            value: "110",
            tooltip: null,
            enabled: true,
            generation: 1,
          }],
          bounds: { x: 12, y: 8, width: 120, height: 30 },
          zIndex: 1,
          background: null,
          isRelative: true,
          box: null,
        }],
      }],
      alignment: "left",
      temporary: false,
    };

    render(<ScrollbackRenderer lines={[stateLine]} assets={assets} onInput={onInput} />);

    const button = screen.getByRole("button", { name: "Invite" });
    expect(button).toHaveStyle({ pointerEvents: "auto" });
    fireEvent.click(button);
    expect(onInput).toHaveBeenCalledWith({ value: "110", source: "BUTTON" });
  });

  it("keeps virtual rows and line shells out of hit testing for overflowing panels", () => {
    render(<ScrollbackRenderer lines={[line("overflowing-panel", "panel")]} assets={assets} onInput={() => undefined} />);

    expect(document.querySelector(".console-virtual-row")).toHaveStyle({ pointerEvents: "none" });
    expect(document.querySelector(".console-line")).toHaveStyle({ pointerEvents: "none" });
  });

  it("places multiple pos layers at one x coordinate so they can composite", () => {
    const layeredLine: RealtimeLine = {
      lineId: "layered",
      nodes: [
        { type: "button", children: [{ type: "text", text: "back", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], value: "", tooltip: null, enabled: false, generation: 0, positionX: 54 },
        { type: "button", children: [{ type: "text", text: "front", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }], value: "", tooltip: null, enabled: false, generation: 0, positionX: 54 },
      ],
      alignment: "left",
      temporary: false,
    };
    render(<ScrollbackRenderer lines={[layeredLine]} assets={assets} onInput={() => undefined} />);
    expect([...document.querySelectorAll<HTMLElement>(".console-nonbutton")].map(node => [node.style.position, node.style.left])).toEqual([
      ["absolute", "54px"],
      ["absolute", "54px"],
    ]);
  });

  it("keeps physical nonbutton layers opaque and pointer-transparent", () => {
    const physicalLayer = (text: string): RealtimeNode => ({
      type: "positionedInlineSegment",
      positionX: 54,
      measuredWidth: 54,
      action: { value: "", tooltip: null, enabled: false, generation: 0 },
      children: [{ type: "text", text, style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }],
    });
    const layeredLine: RealtimeLine = {
      lineId: "physical-layered",
      nodes: [physicalLayer("back"), physicalLayer("front")],
      alignment: "left",
      temporary: false,
    };

    render(<ScrollbackRenderer lines={[layeredLine]} assets={assets} onInput={() => undefined} />);

    const layers = [...document.querySelectorAll<HTMLElement>(".positioned-inline-segment")];
    expect(layers.map(layer => [layer.tagName, layer.textContent, layer.style.left, layer.classList.contains("console-nonbutton")])).toEqual([
      ["SPAN", "back", "54px", true],
      ["SPAN", "front", "54px", true],
    ]);
    expect(document.querySelectorAll(".positioned-inline-action")).toHaveLength(0);
    expect(document.querySelectorAll("button:disabled")).toHaveLength(0);
  });

  it("renders structured upstream island nodes and enables nested buttons", () => {
    const button = {
      type: "button" as const,
      children: [{ type: "text" as const, text: "Island Go", style: { decorations: [], fontFamily: "default", fontSize: 16, lineHeight: 20, foreground: null, background: null } }],
      value: "island-go",
      tooltip: null,
      enabled: true,
      generation: 4,
    };
    const structuredLine: RealtimeLine = {
      lineId: "structured-island",
      nodes: [{ type: "htmlIsland", nodes: [button] }],
      alignment: "left",
      temporary: false,
    };
    render(<ScrollbackRenderer lines={[structuredLine]} assets={assets} onInput={() => undefined} />);
    expect(screen.getByRole("button", { name: "Island Go" })).toBeEnabled();
  });
});

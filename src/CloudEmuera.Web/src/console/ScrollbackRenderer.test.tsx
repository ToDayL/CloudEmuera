import { fireEvent, render, screen } from "@testing-library/react";
import { createRef } from "react";
import { describe, expect, it, vi } from "vitest";
import { AssetResolver } from "./AssetResolver";
import { ScrollbackRenderer, leadingVisualOverflow, trailingVisualOverflow, trimTrailingEmptyLines } from "./ScrollbackRenderer";
import type { RealtimeLine } from "../realtime/protocol";

const assets = new AssetResolver("s1", { schemaVersion: 1, assets: [], fonts: [], fontDiagnostics: [] });
const clockAssets = new AssetResolver("s1", { schemaVersion: 1, assets: [{ assetId: "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", mediaType: "image/png", byteLength: 128, contentDigest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", eTag: null }], fonts: [], fontDiagnostics: [] });
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

  it("re-synchronizes after the connected snapshot changes its measured height", () => {
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
      (element as HTMLElement & { scrollHeight: number }).scrollHeight = 500;
      notifyResize?.();
      expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 400, behavior: "auto" });
    } finally {
      vi.unstubAllGlobals();
    }
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
      nodes: [{ type: "sprite", assetId: "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sourceRect: { x: 0, y: 0, width: 180, height: 180 }, destination: { x: 0, y: 32, width: 180, height: 180 }, frame: 0, zIndex: 0, opacity: 1, altText: "portrait", hoverAssetId: null, hoverSourceRect: null, mappingAssetId: null, mappingSourceRect: null, animationFrames: [] }],
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
    expect(document.querySelector<HTMLElement>(".console-overflow-reserve")).toHaveStyle({ height: "192px" });
    expect(document.querySelector<HTMLElement>(".positioned-inline-segment")).toHaveStyle({ overflow: "visible" });
  });

  it("reserves leading space for a title image with a negative ypos", () => {
    const titleLine: RealtimeLine = {
      lineId: "title-line",
      nodes: [{ type: "sprite", assetId: "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sourceRect: { x: 0, y: 0, width: 486, height: 648 }, destination: { x: 0, y: -594, width: 486, height: 648 }, frame: 0, zIndex: 0, opacity: 1, altText: "title", hoverAssetId: null, hoverSourceRect: null, mappingAssetId: null, mappingSourceRect: null, animationFrames: [] }],
      alignment: "left",
      temporary: false,
      lineHeight: 20,
    };

    expect(leadingVisualOverflow([titleLine])).toBe(594);
    render(<ScrollbackRenderer lines={[titleLine]} assets={clockAssets} onInput={() => undefined} />);
    expect(document.querySelector<HTMLElement>(".console-overflow-leading")).toHaveStyle({ height: "594px" });
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
      nodes: [{ type: "sprite", assetId: "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sourceRect: { x: 0, y: 0, width: 54, height: 16 }, destination: { x: 12, y: 4, width: 54, height: 16 }, frame: 0, zIndex: 0, opacity: 1, altText: "clock", hoverAssetId: null, hoverSourceRect: null, mappingAssetId: null, mappingSourceRect: null, animationFrames: [] }],
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

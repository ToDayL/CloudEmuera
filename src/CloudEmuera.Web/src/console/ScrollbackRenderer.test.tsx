import { fireEvent, render, screen } from "@testing-library/react";
import { createRef } from "react";
import { describe, expect, it, vi } from "vitest";
import { AssetResolver } from "./AssetResolver";
import { ScrollbackRenderer } from "./ScrollbackRenderer";
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
  it("follows new output only while the reader is at the latest position", () => {
    const { ref, element } = scrollContainer(true);
    const view = render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    view.rerender(<ScrollbackRenderer lines={[line("one", "one"), line("two", "two")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    expect(element.scrollTo).toHaveBeenCalledWith({ top: 200, behavior: "auto" });
  });

  it("shows a back-to-latest control after the reader scrolls upward", () => {
    const { ref, element } = scrollContainer(true);
    render(<ScrollbackRenderer lines={[line("one", "one")]} assets={assets} onInput={() => undefined} scrollContainerRef={ref} />);
    element.scrollTop = 10;
    fireEvent.scroll(element);
    const button = screen.getByRole("button", { name: "↓ 回到最新" });
    fireEvent.click(button);
    expect(element.scrollTo).toHaveBeenLastCalledWith({ top: 200, behavior: "smooth" });
  });
});

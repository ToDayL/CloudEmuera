import { describe, expect, it } from "vitest";
import { backgroundStyle, orderCanvasDrawables, orderHitRegions, topmostRasterAtPoint } from "./CanvasRenderer";
import { render } from "@testing-library/react";
import { ShapeSvg } from "./ScrollbackRenderer";
import type { RealtimeDrawable } from "../realtime/protocol";

const bounds = { x: 0, y: 0, width: 10, height: 10 };

describe("CanvasRenderer scene ordering", () => {
  it("uses one stable zIndex plus drawableId order across canvas and DOM drawables", () => {
    const drawables: RealtimeDrawable[] = [
      { type: "htmlIsland", drawableId: "html", bounds, zIndex: 5, opacity: 1, root: { type: "text", text: "safe" } },
      { type: "shape", drawableId: "shape", bounds, zIndex: 5, opacity: 1, shape: "rectangle", points: [], fill: null, stroke: null },
      { type: "sprite", drawableId: "sprite", bounds, zIndex: 1, opacity: 1, assetId: "asset", sourceRect: bounds, frame: 0, animationFrames: [] },
      { type: "raster", drawableId: "raster", bounds, zIndex: 5, opacity: 1, pngData: "iVBORw0KGgo=" },
    ];
    expect(orderCanvasDrawables(drawables).map(drawable => drawable.drawableId)).toEqual(["sprite", "html", "raster", "shape"]);
  });

  it("keeps overlapping hit controls in a deterministic stable order", () => {
    const region = (regionId: string, enabled: boolean) => ({ regionId, enabled, bounds, inputValue: regionId });
    expect(orderHitRegions([region("z", true), region("a", true), region("hidden", false)])).toEqual([
      region("a", true),
      region("z", true),
    ]);
  });

  it("selects the topmost hover raster by zIndex and stable drawable ID", () => {
    const raster = (drawableId: string, zIndex: number, hoverPngData?: string): RealtimeDrawable => ({ type: "raster", drawableId, bounds, zIndex, opacity: 1, pngData: "iVBORw0KGgo=", hoverPngData });
    expect(topmostRasterAtPoint([raster("b", 2, "hover"), raster("a", 2, "hover"), raster("top", 3, "hover")], { x: 5, y: 5 })?.drawableId).toBe("top");
    expect(topmostRasterAtPoint([raster("b", 2, "hover"), raster("a", 2, "hover")], { x: 5, y: 5 })?.drawableId).toBe("b");
    expect(topmostRasterAtPoint([raster("b", 2, "hover"), raster("a", 2, "hover")], { x: 20, y: 20 })).toBeUndefined();
  });

  it("covers every background mode and shape primitive", () => {
    for (const mode of ["stretch", "contain", "cover", "center", "repeat"] as const) {
      const style = backgroundStyle("/asset", mode, 0.75);
      expect(style.opacity).toBe(0.75);
      expect(style.backgroundRepeat).toBe(mode === "repeat" ? "repeat" : "no-repeat");
    }
    for (const shape of ["rectangle", "space", "ellipse", "line", "polygon"]) {
      const view = render(<ShapeSvg shape={shape} bounds={bounds} points={shape === "line" ? [{ x: 0, y: 0 }, { x: 10, y: 10 }] : shape === "polygon" ? [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 5, y: 10 }] : []} />);
      expect(view.container.querySelector("svg")).toBeInTheDocument();
      view.unmount();
    }
  });
});

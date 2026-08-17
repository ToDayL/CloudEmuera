import { describe, expect, it } from "vitest";
import { imageHasLoaded, inlineSpriteSlotStyle, inlineSpriteStyle, spriteFrameAt } from "./SpriteRenderer";

const frames = [
  { assetId: "a", sourceRect: { x: 0, y: 0, width: 8, height: 8 }, offset: { x: 0, y: 0 }, durationMilliseconds: 100 },
  { assetId: "b", sourceRect: { x: 8, y: 0, width: 8, height: 8 }, offset: { x: 1, y: 2 }, durationMilliseconds: 200 },
];

describe("spriteFrameAt", () => {
  it("advances through bounded animation durations and wraps", () => {
    expect(spriteFrameAt(frames, 0, 0)).toBe(0);
    expect(spriteFrameAt(frames, 0, 99)).toBe(0);
    expect(spriteFrameAt(frames, 0, 100)).toBe(1);
    expect(spriteFrameAt(frames, 0, 299)).toBe(1);
    expect(spriteFrameAt(frames, 0, 300)).toBe(0);
  });

  it("starts at the declared frame and handles an empty animation", () => {
    expect(spriteFrameAt(frames, 1, 0)).toBe(1);
    expect(spriteFrameAt([], 4, 500)).toBe(-1);
  });
});

describe("imageHasLoaded", () => {
  it("recognizes a cached decoded image without waiting for another load event", () => {
    expect(imageHasLoaded({ complete: true, naturalWidth: 1200 })).toBe(true);
    expect(imageHasLoaded({ complete: true, naturalWidth: 0 })).toBe(false);
    expect(imageHasLoaded({ complete: false, naturalWidth: 1200 })).toBe(false);
  });
});

describe("inlineSpriteStyle", () => {
  it("keeps the runtime destination offset for inline HTML sprites", () => {
    expect(inlineSpriteStyle({ x: 12, y: -4, width: 54, height: 16 })).toEqual({
      position: "absolute",
      left: 12,
      top: -4,
    });
  });
});

describe("inlineSpriteSlotStyle", () => {
  it("keeps the horizontal footprint while removing the image from the line height", () => {
    expect(inlineSpriteSlotStyle({ x: 12, y: -4, width: 54, height: 16 })).toEqual({
      position: "relative",
      display: "inline-block",
      width: 54,
      height: 0,
      overflow: "visible",
      verticalAlign: "text-top",
    });
  });
});

import { useLayoutEffect, useMemo, useRef, useState } from "react";
import type { CSSProperties } from "react";
import type { AssetResolver } from "./AssetResolver";
import type { RealtimeRect, SpriteAnimationFrame } from "../realtime/protocol";

export interface SpriteVisual {
  assetId: string;
  sourceRect: RealtimeRect;
  frame: number;
  animationFrames: SpriteAnimationFrame[];
  opacity: number;
  hoverAssetId?: string | null;
  hoverSourceRect?: RealtimeRect | null;
}

// A realtime CLEARLINE/reprint replaces the React line key. Keep decoded
// images at module scope so a remounted portrait can draw synchronously from
// the browser cache instead of showing a blank canvas for one paint.
const spriteImageCache = new Map<string, HTMLImageElement>();

export function spriteFrameAt(frames: SpriteAnimationFrame[], initialFrame: number, elapsedMilliseconds: number): number {
  if (frames.length === 0) return -1;
  const total = frames.reduce((sum, frame) => sum + frame.durationMilliseconds, 0);
  if (total <= 0) return Math.min(frames.length - 1, Math.max(0, initialFrame));
  let remaining = Math.max(0, elapsedMilliseconds) % total;
  const start = ((initialFrame % frames.length) + frames.length) % frames.length;
  for (let offset = 0; offset < frames.length; offset++) {
    const index = (start + offset) % frames.length;
    if (remaining < frames[index].durationMilliseconds) return index;
    remaining -= frames[index].durationMilliseconds;
  }
  return start;
}

export function imageHasLoaded(image: Pick<HTMLImageElement, "complete" | "naturalWidth">): boolean {
  return image.complete && image.naturalWidth !== 0;
}

/**
 * HTML_PRINT images are inline display parts. Their destination rectangle is
 * an offset within the physical line, not a new block position. Relative
 * positioning preserves that offset without changing the line's measured
 * width/height (matching the desktop ConsoleImagePart drawing model).
 */
export function inlineSpriteStyle(destination: RealtimeRect): CSSProperties {
  return {
    position: "absolute",
    left: destination.x,
    top: destination.y,
  };
}

/**
 * The desktop ConsoleImagePart never contributes to the display line's height;
 * it is drawn as an overlay at `ypos` relative to the line top. The text-flow
 * slot keeps the part's horizontal footprint (so alignment and wrapping match
 * upstream) while its zero height stops the image from pushing later lines
 * down. The canvas itself is positioned absolutely inside the slot.
 */
export function inlineSpriteSlotStyle(destination: RealtimeRect): CSSProperties {
  return {
    position: "relative",
    display: "inline-block",
    width: destination.width,
    height: 0,
    overflow: "visible",
    verticalAlign: "text-top",
  };
}

export function SpriteCanvas({ sprite, assets, width, height, alt, className, style, onRenderError }: {
  sprite: SpriteVisual;
  assets: AssetResolver;
  width: number;
  height: number;
  alt: string;
  className?: string;
  style?: CSSProperties;
  onRenderError?: (message: string) => void;
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const images = useRef(new Map<string, HTMLImageElement>());
  const [imageRevision, setImageRevision] = useState(0);
  const [animationFrame, setAnimationFrame] = useState(-1);
  const [hovered, setHovered] = useState(false);
  const startedAt = useRef<number | null>(null);
  const animationSignature = sprite.animationFrames.map(frame => `${frame.assetId}:${rectKey(frame.sourceRect)}:${frame.offset.x},${frame.offset.y}:${frame.durationMilliseconds}`).join("|");
  const sourceRectSignature = rectKey(sprite.sourceRect);
  const hoverSourceSignature = sprite.hoverSourceRect ? rectKey(sprite.hoverSourceRect) : "";
  const sourceSignature = `${sprite.assetId}|${sprite.hoverAssetId ?? ""}|${sprite.animationFrames.map(frame => frame.assetId).join(",")}`;
  const sourceUrls = useMemo(() => {
    const ids = [sprite.assetId, ...sprite.animationFrames.map(frame => frame.assetId)];
    if (sprite.hoverAssetId) ids.push(sprite.hoverAssetId);
    return [...new Set(ids)].map(assetId => assets.url(assetId)).filter((url): url is string => Boolean(url));
  }, [assets, sourceSignature]);

  useLayoutEffect(() => {
    let cancelled = false;
    for (const url of images.current.keys()) if (!sourceUrls.includes(url)) images.current.delete(url);
    for (const url of sourceUrls) {
      if (images.current.has(url)) continue;
      const cached = spriteImageCache.get(url);
      if (cached && imageHasLoaded(cached)) {
        images.current.set(url, cached);
        continue;
      }
      const image = new Image();
      const publishLoadedImage = () => {
        if (cancelled || image.naturalWidth === 0 || images.current.get(url) === image) return;
        spriteImageCache.set(url, image);
        images.current.set(url, image);
        setImageRevision(revision => revision + 1);
      };
      image.onload = publishLoadedImage;
      image.onerror = () => { if (!cancelled) onRenderError?.("Sprite 资源加载失败，已停止渲染该节点。"); };
      image.src = url;
      // A browser may satisfy a presentation-manifest request from its
      // memory/disk cache before the effect gets another render opportunity.
      // Check the completed image explicitly so a cached SpriteNode cannot
      // remain a blank canvas waiting for an onload notification.
      if (imageHasLoaded(image)) publishLoadedImage();
    }
    return () => { cancelled = true; };
  }, [onRenderError, sourceUrls]);

  useLayoutEffect(() => {
    startedAt.current = null;
    const initial = spriteFrameAt(sprite.animationFrames, sprite.frame, 0);
    setAnimationFrame(initial);
    if (sprite.animationFrames.length === 0) return;
    let animationFrame = 0;
    let lastFrame = initial;
    const tick = (now: number) => {
      if (startedAt.current === null) startedAt.current = now;
      const nextFrame = spriteFrameAt(sprite.animationFrames, sprite.frame, now - startedAt.current);
      if (nextFrame !== lastFrame) {
        lastFrame = nextFrame;
        setAnimationFrame(nextFrame);
      }
      animationFrame = requestAnimationFrame(tick);
    };
    animationFrame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(animationFrame);
  }, [animationSignature]);

  useLayoutEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || width <= 0 || height <= 0) return;
    const ratio = Math.max(1, Math.min(3, window.devicePixelRatio || 1));
    const pixelWidth = Math.max(1, Math.round(width * ratio));
    const pixelHeight = Math.max(1, Math.round(height * ratio));
    // Assigning canvas dimensions clears the bitmap. Do it only when the
    // physical size changes; animation ticks and realtime snapshots must keep
    // the previous frame visible while the next frame is painted.
    if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
    if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
    const context = canvas.getContext("2d");
    if (!context) return;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    const animation = animationFrame >= 0 ? sprite.animationFrames[animationFrame] : null;
    const assetId = hovered && sprite.hoverAssetId ? sprite.hoverAssetId : animation?.assetId ?? sprite.assetId;
    const url = assets.url(assetId);
    const image = url ? images.current.get(url) : undefined;
    if (!url) { onRenderError?.("Sprite 资源未通过 Session presentation manifest 授权。"); return; }
    // Keep the last complete frame visible while a replacement asset is
    // loading. Clearing before this check produces a visible flash on every
    // realtime snapshot that changes the sprite object identity.
    if (!image) return;
    context.clearRect(0, 0, width, height);
    const source = hovered && sprite.hoverAssetId && sprite.hoverSourceRect ? sprite.hoverSourceRect : animation?.sourceRect ?? sprite.sourceRect;
    context.save();
    context.globalAlpha = sprite.opacity;
    context.drawImage(image, source.x, source.y, source.width, source.height, 0, 0, width, height);
    context.restore();
  }, [assets, animationFrame, hovered, imageRevision, onRenderError, sprite.assetId, sourceRectSignature, sprite.frame, sprite.opacity, sprite.hoverAssetId, hoverSourceSignature, animationSignature, width, height]);

  // `width`/`height` are logical pixels used for the backing bitmap. A
  // canvas scene is responsive, so its caller may override the CSS size with
  // 100% while retaining these logical dimensions for drawImage.
  return <canvas ref={canvasRef} className={className} style={{ width, height, opacity: sprite.opacity, ...style }} role="img" aria-label={alt} onPointerEnter={() => setHovered(true)} onPointerLeave={() => setHovered(false)} />;
}

function rectKey(rect: RealtimeRect): string {
  return `${rect.x},${rect.y},${rect.width},${rect.height}`;
}

import { useEffect, useMemo, useRef, useState } from "react";
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
  const [elapsed, setElapsed] = useState(0);
  const [hovered, setHovered] = useState(false);
  const startedAt = useRef<number | null>(null);
  const animationSignature = sprite.animationFrames.map(frame => `${frame.assetId}:${rectKey(frame.sourceRect)}:${frame.offset.x},${frame.offset.y}:${frame.durationMilliseconds}`).join("|");
  const sourceSignature = `${sprite.assetId}|${sprite.hoverAssetId ?? ""}|${sprite.animationFrames.map(frame => frame.assetId).join(",")}`;
  const sourceUrls = useMemo(() => {
    const ids = [sprite.assetId, ...sprite.animationFrames.map(frame => frame.assetId)];
    if (sprite.hoverAssetId) ids.push(sprite.hoverAssetId);
    return [...new Set(ids)].map(assetId => assets.url(assetId)).filter((url): url is string => Boolean(url));
  }, [assets, sourceSignature]);

  useEffect(() => {
    let cancelled = false;
    for (const url of images.current.keys()) if (!sourceUrls.includes(url)) images.current.delete(url);
    for (const url of sourceUrls) {
      if (images.current.has(url)) continue;
      const image = new Image();
      image.onload = () => { if (!cancelled) { images.current.set(url, image); setImageRevision(revision => revision + 1); } };
      image.onerror = () => { if (!cancelled) onRenderError?.("Sprite 资源加载失败，已停止渲染该节点。"); };
      image.src = url;
    }
    return () => { cancelled = true; };
  }, [onRenderError, sourceUrls]);

  useEffect(() => {
    startedAt.current = null;
    setElapsed(0);
    if (sprite.animationFrames.length === 0) return;
    let animationFrame = 0;
    const tick = (now: number) => {
      if (startedAt.current === null) startedAt.current = now;
      setElapsed(now - startedAt.current);
      animationFrame = requestAnimationFrame(tick);
    };
    animationFrame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(animationFrame);
  }, [animationSignature]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || width <= 0 || height <= 0) return;
    const ratio = Math.max(1, Math.min(3, window.devicePixelRatio || 1));
    canvas.width = Math.max(1, Math.round(width * ratio));
    canvas.height = Math.max(1, Math.round(height * ratio));
    const context = canvas.getContext("2d");
    if (!context) return;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);
    const frameIndex = spriteFrameAt(sprite.animationFrames, sprite.frame, elapsed);
    const animation = frameIndex >= 0 ? sprite.animationFrames[frameIndex] : null;
    const assetId = hovered && sprite.hoverAssetId ? sprite.hoverAssetId : animation?.assetId ?? sprite.assetId;
    const url = assets.url(assetId);
    const image = url ? images.current.get(url) : undefined;
    if (!url) { onRenderError?.("Sprite 资源未通过 Session presentation manifest 授权。"); return; }
    if (!image) return;
    const source = hovered && sprite.hoverAssetId && sprite.hoverSourceRect ? sprite.hoverSourceRect : animation?.sourceRect ?? sprite.sourceRect;
    context.save();
    context.globalAlpha = sprite.opacity;
    context.drawImage(image, source.x, source.y, source.width, source.height, 0, 0, width, height);
    context.restore();
  }, [assets, elapsed, hovered, imageRevision, onRenderError, sprite, width, height]);

  return <canvas ref={canvasRef} className={className} style={{ ...style, width, height, opacity: sprite.opacity }} role="img" aria-label={alt} onPointerEnter={() => setHovered(true)} onPointerLeave={() => setHovered(false)} />;
}

function rectKey(rect: RealtimeRect): string {
  return `${rect.x},${rect.y},${rect.width},${rect.height}`;
}

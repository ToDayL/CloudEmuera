import { useEffect, useRef, useState } from "react";
import type { CSSProperties } from "react";
import type { AssetResolver } from "./AssetResolver";
import { SafeHtmlRenderer } from "./SafeHtmlRenderer";
import type { BackgroundLayer, CanvasScene, HitRegion, RealtimeDrawable, WindowMetadata } from "../realtime/protocol";
import { NodeRenderer, type ConsoleInputEvent } from "./ScrollbackRenderer";
import { SpriteCanvas } from "./SpriteRenderer";
import { useConsoleTooltipTarget } from "./TooltipLayer";

const pngSignature = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export function CanvasRenderer({ scene, backgroundLayers, windowMetadata, assets, onInput, onRenderError, interactive = true }: {
  scene: CanvasScene;
  backgroundLayers: BackgroundLayer[];
  windowMetadata: WindowMetadata;
  assets: AssetResolver;
  onInput: (event: ConsoleInputEvent) => void;
  onRenderError?: (message: string) => void;
  interactive?: boolean;
}) {
  const [hoveredRasterId, setHoveredRasterId] = useState<string | null>(null);

  if (!hasCanvasContent(scene, backgroundLayers)) return null;

  const updateRasterHover = (event: React.PointerEvent<HTMLDivElement>) => {
    const root = event.currentTarget.getBoundingClientRect();
    if (root.width <= 0 || root.height <= 0) return;
    const point = { x: (event.clientX - root.left) * windowMetadata.viewportWidth / root.width, y: (event.clientY - root.top) * windowMetadata.viewportHeight / root.height };
    const hit = topmostRasterAtPoint(scene.drawables, point);
    setHoveredRasterId(hit?.drawableId ?? null);
  };

  const ordered = orderCanvasDrawables(scene.drawables);
  return <div className="canvas-renderer" style={{ aspectRatio: `${windowMetadata.viewportWidth} / ${windowMetadata.viewportHeight}` }} onPointerMove={updateRasterHover} onPointerLeave={() => setHoveredRasterId(null)}>
    <div className="canvas-background-layer" aria-hidden="true">
      {[...backgroundLayers].sort((a, b) => a.depth - b.depth || stableStringOrder(a.layerId, b.layerId)).map(layer => {
        const url = assets.url(layer.assetId);
        if (!url) return <span className="canvas-missing-background" key={layer.layerId} />;
        return <div key={layer.layerId} className="canvas-background" style={backgroundStyle(url, layer.mode, layer.opacity)} />;
      })}
    </div>
    {ordered.map((drawable, index) => {
      const layer = 100 + index;
      if (drawable.type === "sprite") return <div key={drawable.drawableId} className="canvas-sprite-drawable" style={{ ...drawableStyle(drawable.bounds, drawable.opacity, windowMetadata), zIndex: layer }} aria-hidden="true"><SpriteCanvas sprite={drawable} assets={assets} alt="游戏精灵" width={drawable.bounds.width} height={drawable.bounds.height} style={{ width: "100%", height: "100%" }} onRenderError={onRenderError} /></div>;
      if (drawable.type === "htmlIsland") {
        const islandNodes = drawable.nodes;
        return <div key={drawable.drawableId} className="canvas-html-island" style={{ ...drawableStyle(drawable.bounds, drawable.opacity, windowMetadata), zIndex: layer }}>
          {islandNodes ? islandNodes.map((node, nodeIndex) => <NodeRenderer key={`${drawable.drawableId}-${nodeIndex}`} node={node} assets={assets} onInput={onInput} onRenderError={onRenderError} />) : <SafeHtmlRenderer node={drawable.root!} assets={assets} onRenderError={onRenderError} />}
        </div>;
      }
      return <DrawableCanvas key={drawable.drawableId} drawable={drawable} layer={layer} windowMetadata={windowMetadata} hoveredRasterId={hoveredRasterId} onRenderError={onRenderError} />;
    })}
    <div className="canvas-hit-layer" aria-label="游戏交互区域" style={{ zIndex: 10_000 }}>
      {interactive && orderHitRegions(scene.hitRegions).map(region => <CanvasHitTarget key={region.regionId} region={region} windowMetadata={windowMetadata} onInput={onInput} />)}
    </div>
  </div>;
}

function CanvasHitTarget({ region, windowMetadata, onInput }: { region: HitRegion; windowMetadata: WindowMetadata; onInput: (event: ConsoleInputEvent) => void }) {
  const target = useConsoleTooltipTarget(region.tooltip, 0);
  return <button ref={target.ref as React.RefCallback<HTMLButtonElement>} {...target.props} className="canvas-hit console-tooltip-target" type="button" style={hitStyle(region.bounds, windowMetadata)} onClick={event => {
    const root = event.currentTarget.parentElement?.parentElement?.getBoundingClientRect();
    if (!root || root.width <= 0 || root.height <= 0) return;
    const x = clamp((event.clientX - root.left) * windowMetadata.viewportWidth / root.width, 0, windowMetadata.viewportWidth);
    const y = clamp((event.clientY - root.top) * windowMetadata.viewportHeight / root.height, 0, windowMetadata.viewportHeight);
    onInput({ value: region.inputValue, source: "POINTER", pointer: { x, y, button: 0, pressed: true } });
  }}><span className="sr-only">{region.tooltip ?? "交互区域"}</span>{target.badge}</button>;
}

export function hasCanvasContent(scene: CanvasScene, backgroundLayers: readonly BackgroundLayer[]): boolean {
  return backgroundLayers.length > 0 || scene.drawables.length > 0 || scene.hitRegions.length > 0;
}

export function orderCanvasDrawables(drawables: readonly RealtimeDrawable[]): RealtimeDrawable[] {
  return [...drawables].sort((a, b) => a.zIndex - b.zIndex || stableStringOrder(a.drawableId, b.drawableId));
}

/** Hit regions have no independent depth in the closed protocol; stable ID is the deterministic tie-break. */
export function orderHitRegions(regions: readonly HitRegion[]): HitRegion[] {
  return regions.filter(region => region.enabled).sort((a, b) => stableStringOrder(a.regionId, b.regionId));
}

export function topmostRasterAtPoint(drawables: readonly RealtimeDrawable[], point: { x: number; y: number }): Extract<RealtimeDrawable, { type: "raster" }> | undefined {
  return [...drawables]
    .filter((drawable): drawable is Extract<RealtimeDrawable, { type: "raster" }> => drawable.type === "raster" && Boolean(drawable.hoverPngData) && pointInRect(point, drawable.bounds))
    .sort((a, b) => b.zIndex - a.zIndex || stableStringOrder(b.drawableId, a.drawableId))[0];
}

function DrawableCanvas({ drawable, layer, windowMetadata, hoveredRasterId, onRenderError }: { drawable: Extract<RealtimeDrawable, { type: "shape" | "raster" }>; layer: number; windowMetadata: WindowMetadata; hoveredRasterId: string | null; onRenderError?: (message: string) => void }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const canvas = canvasRef.current;
    const context = canvas?.getContext("2d");
    if (!canvas || !context) return;
    let disposed = false;
    const objectUrls = new Set<string>();
    context.clearRect(0, 0, windowMetadata.viewportWidth, windowMetadata.viewportHeight);
    drawDrawable(context, drawable, objectUrls, () => disposed, onRenderError, hoveredRasterId);
    return () => {
      disposed = true;
      for (const url of objectUrls) URL.revokeObjectURL(url);
      objectUrls.clear();
    };
  }, [drawable, hoveredRasterId, onRenderError, windowMetadata]);
  return <canvas ref={canvasRef} className="canvas-drawable-layer" width={windowMetadata.viewportWidth} height={windowMetadata.viewportHeight} aria-hidden="true" style={{ zIndex: layer }} />;
}

function drawDrawable(context: CanvasRenderingContext2D, drawable: Extract<RealtimeDrawable, { type: "shape" | "raster" }>, objectUrls: Set<string>, isDisposed: () => boolean, onRenderError?: (message: string) => void, hoveredRasterId?: string | null): void {
  const { bounds } = drawable;
  if (drawable.type === "shape") {
    context.save();
    context.globalAlpha = drawable.opacity;
    context.fillStyle = color(drawable.fill) ?? "transparent";
    context.strokeStyle = color(drawable.stroke) ?? "transparent";
    context.lineWidth = 1;
    if (drawable.shape === "ellipse") { context.beginPath(); context.ellipse(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2, bounds.width / 2, bounds.height / 2, 0, 0, Math.PI * 2); context.fill(); context.stroke(); }
    else if (drawable.shape === "line") { const first = drawable.points[0] ?? { x: bounds.x, y: bounds.y }; const second = drawable.points[1] ?? { x: bounds.x + bounds.width, y: bounds.y + bounds.height }; context.beginPath(); context.moveTo(first.x, first.y); context.lineTo(second.x, second.y); context.stroke(); }
    else if (drawable.shape === "polygon") { context.beginPath(); drawable.points.forEach((point, index) => index === 0 ? context.moveTo(point.x, point.y) : context.lineTo(point.x, point.y)); context.closePath(); context.fill(); context.stroke(); }
    else if (drawable.shape !== "space") { context.fillRect(bounds.x, bounds.y, bounds.width, bounds.height); context.strokeRect(bounds.x, bounds.y, bounds.width, bounds.height); }
    context.restore();
    return;
  }
  if (drawable.type === "raster") {
    const url = createPngBlobUrl(hoveredRasterId === drawable.drawableId && drawable.hoverPngData ? drawable.hoverPngData : drawable.pngData);
    if (!url) { onRenderError?.("Raster 不是有效的 PNG，已停止渲染该画布。"); return; }
    objectUrls.add(url);
    const image = new Image();
    image.onload = () => {
      objectUrls.delete(url); URL.revokeObjectURL(url);
      if (isDisposed() || image.naturalWidth > 8192 || image.naturalHeight > 8192) { onRenderError?.("Raster 尺寸超过浏览器安全上限。"); return; }
      context.save(); context.globalAlpha = drawable.opacity; context.drawImage(image, bounds.x, bounds.y, bounds.width, bounds.height); context.restore();
    };
    image.onerror = () => { objectUrls.delete(url); URL.revokeObjectURL(url); onRenderError?.("Raster 解码失败，已停止渲染该画布。"); };
    image.src = url;
    return;
  }
}

export function backgroundStyle(url: string, mode: "stretch" | "contain" | "cover" | "center" | "repeat", opacity: number): CSSProperties {
  return {
    opacity,
    backgroundImage: `url("${url}")`,
    backgroundPosition: "center",
    backgroundRepeat: mode === "repeat" ? "repeat" : "no-repeat",
    backgroundSize: mode === "stretch" ? "100% 100%" : mode === "contain" ? "contain" : mode === "cover" ? "cover" : "auto",
  };
}

function drawableStyle(bounds: { x: number; y: number; width: number; height: number }, opacity: number, windowMetadata: WindowMetadata): CSSProperties {
  return { left: `${bounds.x / windowMetadata.viewportWidth * 100}%`, top: `${bounds.y / windowMetadata.viewportHeight * 100}%`, width: `${bounds.width / windowMetadata.viewportWidth * 100}%`, height: `${bounds.height / windowMetadata.viewportHeight * 100}%`, opacity };
}

function hitStyle(bounds: { x: number; y: number; width: number; height: number }, windowMetadata: WindowMetadata): CSSProperties {
  return drawableStyle(bounds, 1, windowMetadata);
}

function createPngBlobUrl(value: string): string | null {
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(value) || value.length % 4 !== 0) return null;
  if (typeof URL.createObjectURL !== "function") return null;
  try {
    const binary = atob(value);
    if (binary.length < pngSignature.length) return null;
    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
    if (!bytes.slice(0, pngSignature.length).every((byte, index) => byte === pngSignature[index])) return null;
    return URL.createObjectURL(new Blob([bytes], { type: "image/png" }));
  } catch { return null; }
}

function color(value: { red: number; green: number; blue: number; alpha: number } | null | undefined): string | undefined {
  return value ? `rgba(${value.red},${value.green},${value.blue},${value.alpha / 255})` : undefined;
}

function clamp(value: number, minimum: number, maximum: number): number { return Math.min(maximum, Math.max(minimum, value)); }

function stableStringOrder(left: string, right: string): number { return left < right ? -1 : left > right ? 1 : 0; }


function pointInRect(point: { x: number; y: number }, rect: { x: number; y: number; width: number; height: number }): boolean {
  return point.x >= rect.x && point.y >= rect.y && point.x <= rect.x + rect.width && point.y <= rect.y + rect.height;
}

import type { SessionPresentationAssetDto, SessionPresentationFontDto, SessionPresentationManifestDto } from "../api/generated";

export type PresentationAsset = SessionPresentationAssetDto;
export type PresentationFont = SessionPresentationFontDto;
export type PresentationManifest = SessionPresentationManifestDto;

export interface ResolvedPresentationFont extends PresentationFont {
  cssFamily: string;
  aliases: string[];
}

const safeAssetId = /^[A-Za-z0-9._~-]{1,128}$/;
const safeFamily = /^[A-Za-z0-9._~ -]{1,128}$/;
const safeCssFamily = /^cloudemuera-font-[a-f0-9]{16}$/;
const allowedMedia = /^(?:image\/(?:png|jpeg|gif|webp|bmp)|audio\/(?:ogg|mpeg|wav|webm|flac)|font\/(?:woff2?|ttf|otf))$/;
const lxgwRuntimeFamilyName = "LXGW WenKai Mono";

/** Resolves only assets authorized by the session presentation manifest. */
export class AssetResolver {
  private readonly assets: ReadonlyMap<string, PresentationAsset>;
  private readonly fonts: readonly ResolvedPresentationFont[];
  private readonly fontDiagnostics: readonly string[];
  private readonly runtimeFontFamilyName?: string;

  constructor(private readonly sessionId: string, manifest: PresentationManifest | null | undefined, private readonly runtimeFontFamily?: string, runtimeFontFamilyName?: string) {
    this.runtimeFontFamilyName = runtimeFontFamilyName;
    this.assets = new Map((manifest?.assets ?? []).filter(asset => safeAssetId.test(asset.assetId) && allowedMedia.test(asset.mediaType) && Number.isSafeInteger(asset.byteLength) && asset.byteLength >= 0 && /^sha256:[0-9A-Fa-f]{64}$/.test(asset.contentDigest)).map(asset => [asset.assetId, asset]));
    this.fonts = (manifest?.fonts ?? [])
      .filter(font => safeFamily.test(font.family) && safeAssetId.test(font.assetId))
      .map(font => ({
        ...font,
        cssFamily: safeCssFamily.test(font.cssFamily ?? "") ? font.cssFamily! : `cloudemuera-font-${font.assetId.slice(7, 23).toLowerCase()}`,
        aliases: Array.isArray(font.aliases) ? font.aliases : [],
      }));
    this.fontDiagnostics = manifest?.fontDiagnostics ?? [];
  }

  url(assetId: string | null | undefined): string | null {
    if (!assetId || !safeAssetId.test(assetId) || assetId.includes("..") || !this.assets.has(assetId)) return null;
    return `/api/v1/sessions/${encodeURIComponent(this.sessionId)}/assets/${encodeURIComponent(assetId)}`;
  }

  asset(assetId: string | null | undefined): PresentationAsset | null {
    if (!assetId || !safeAssetId.test(assetId)) return null;
    return this.assets.get(assetId) ?? null;
  }

  has(assetId: string | null | undefined): boolean {
    return this.url(assetId) !== null;
  }

  fontFamily(logicalFamily: string): string {
    // Runtime output is measured against the selected bundled face. A game
    // presentation manifest may still contain legacy font metadata for old
    // resources, but it must never become a CSS or Runtime font choice.
    if (this.runtimeFontFamily) return this.runtimeFontFamily;
    const font = this.manifestFonts().find(item => item.family === logicalFamily || item.aliases.includes(logicalFamily));
    // p1-11 originally shipped the compatibility family game-default while
    // RuntimeAdapter emitted default. Keep old frozen Sessions readable while
    // new manifests use an asset-scoped CSS family for every font file.
    if (!font && logicalFamily === "default") {
      const legacy = this.manifestFonts().find(item => item.family === "game-default");
      if (legacy) return legacy.cssFamily;
    }
    return font?.cssFamily ?? this.runtimeFontFamily ?? "sans-serif";
  }

  manifestFonts(): readonly ResolvedPresentationFont[] {
    return this.fonts
      .filter(font => safeFamily.test(font.family) && font.aliases.every(alias => safeFamily.test(alias)) && ["sans-serif", "serif", "monospace"].includes(font.fallback) && this.assets.get(font.assetId)?.mediaType.startsWith("font/") === true)
      .map(font => font);
  }

  diagnostics(): readonly string[] {
    return this.fontDiagnostics;
  }

  usesLxgwBlockGlyphCompatibility(): boolean {
    return this.runtimeFontFamilyName === lxgwRuntimeFamilyName;
  }
}

export function presentationManifestUrl(sessionId: string): string {
  return `/api/v1/sessions/${encodeURIComponent(sessionId)}/presentation-manifest`;
}

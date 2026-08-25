const safePathAssetId = /^path-[A-Za-z0-9_-]{1,2043}$/;

/** Resolves the path reference emitted by the Worker without a presentation index. */
export class AssetResolver {
  constructor(private readonly sessionId: string, private readonly runtimeFontFamily?: string) {}

  url(assetId: string | null | undefined): string | null {
    if (!assetId || !safePathAssetId.test(assetId)) return null;
    return `/api/v1/sessions/${encodeURIComponent(this.sessionId)}/assets/${encodeURIComponent(assetId)}`;
  }

  has(assetId: string | null | undefined): boolean {
    return this.url(assetId) !== null;
  }

  fontFamily(logicalFamily: string): string {
    return this.runtimeFontFamily ?? "sans-serif";
  }

  diagnostics(): readonly string[] {
    return [];
  }
}

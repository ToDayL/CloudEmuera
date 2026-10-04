import { expect, test, type Page } from "@playwright/test";

async function prepare(page: Page) {
  let downloads = 0;
  // Route interception disables HTTP cache, so this proves the persistent
  // font cache works independently of browser HTTP cache heuristics.
  await page.route("**/api/v1/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.startsWith("/api/v1/runtime-fonts")) {
      if (path.endsWith(".woff2")) downloads++;
      if (new URL(route.request().url()).hostname === "font-proxy.test") {
        const response = await route.fetch({ url: `http://api:28647${path}` });
        await route.fulfill({ response });
      } else await route.continue();
      return;
    }
    const user = { id: "usr-font-test", username: "tester", email: "tester@example.com", role: "PLAYER", status: "ACTIVE", mustChangePassword: false, stateVersion: 0 };
    const defaults = { fontFaceId: "sarasa-fixed-sc-1.0.40-regular", fontSize: 18, lineHeight: 19, fontSizeLineHeightMode: "OVERRIDE", widthMode: "ADAPTIVE", customWidth: null, convertBackslashToYen: true };
    await route.fulfill({ json: path === "/api/v1/auth/me" ? user : path.endsWith("session-startup-defaults") ? defaults : { items: [] } });
  });
  return () => downloads;
}

async function previewReady(page: Page) {
  await expect(page.locator(".runtime-font-preview-text")).toHaveAttribute("style", /cloudemuera-runtime-/, { timeout: 30_000 });
}

test("PLAY-013: verified fonts persist across reload and corrupt cache recovers", async ({ page }) => {
  const downloads = await prepare(page);
  await page.goto("/sessions/new");
  await previewReady(page);
  expect(downloads()).toBe(1);
  await page.reload();
  await previewReady(page);
  expect(downloads()).toBe(1);
  await page.evaluate(async () => {
    const db = await new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open("cloudemuera-runtime-fonts", 1);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction("faces", "readwrite");
      const store = tx.objectStore("faces");
      const request = store.openCursor();
      request.onsuccess = () => {
        if (request.result) {
          const value = request.result.value;
          new Uint8Array(value.bytes)[0] ^= 0xff;
          request.result.update(value);
        }
      };
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
    db.close();
  });
  await page.reload();
  await previewReady(page);
  expect(downloads()).toBe(2);
});

test("PLAY-013: denied font storage falls back to downloads", async ({ page }) => {
  const downloads = await prepare(page);
  await page.addInitScript(() => Object.defineProperty(window, "indexedDB", { get() { throw new DOMException("Denied", "SecurityError"); } }));
  await page.goto("/sessions/new");
  await previewReady(page);
  await page.reload();
  await previewReady(page);
  expect(downloads()).toBe(2);
});


test("PLAY-013: persistent font cache evicts the oldest face beyond six entries", async ({ page }) => {
  await prepare(page);
  await page.goto("/login");
  await page.evaluate(async () => {
    const db = await new Promise<IDBDatabase>(resolve => {
      const request = indexedDB.open("cloudemuera-runtime-fonts", 1);
      request.onupgradeneeded = () => request.result.createObjectStore("faces", { keyPath: "digest" }).createIndex("lastUsed", "lastUsed");
      request.onsuccess = () => resolve(request.result);
    });
    await new Promise<void>(resolve => {
      const tx = db.transaction("faces", "readwrite");
      for (let index = 0; index < 6; index++) tx.objectStore("faces").put({ digest: `old-${index}`, bytes: new ArrayBuffer(1), lastUsed: index });
      tx.oncomplete = () => resolve();
    });
    db.close();
  });
  await page.goto("/sessions/new");
  await previewReady(page);
  const keys = await page.evaluate(async () => {
    const db = await new Promise<IDBDatabase>(resolve => {
      const request = indexedDB.open("cloudemuera-runtime-fonts", 1);
      request.onsuccess = () => resolve(request.result);
    });
    const keys = await new Promise<IDBValidKey[]>(resolve => {
      const request = db.transaction("faces").objectStore("faces").getAllKeys();
      request.onsuccess = () => resolve(request.result);
    });
    db.close();
    return keys;
  });
  expect(keys).toHaveLength(6);
  expect(keys).not.toContain("old-0");
  expect(keys).toContain("old-1");
});


for (const { secure, blockWasm, label } of [
  { secure: false, blockWasm: false, label: "HTTP WASM Worker" },
  { secure: true, blockWasm: false, label: "native HTTPS crypto" },
  { secure: false, blockWasm: true, label: "HTTP JavaScript fallback with WASM blocked" },
]) {
  test(`PLAY-013: real font verification uses ${label}`, async ({ page }, testInfo) => {
    const downloads = await prepare(page);
    page.on("pageerror", error => console.log(`PAGE ERROR: ${error.message}`));
    page.on("console", message => { if (message.type() === "error") console.log(`BROWSER ERROR: ${message.text()}`); });
    await page.addInitScript(() => {
      const timings: { engine: string; implementation?: string; milliseconds: number; bytes: number }[] = [];
      Object.defineProperty(window, "fontDigestTimings", { value: timings });
      const OriginalWorker = window.Worker;
      window.Worker = class extends OriginalWorker {
        private started = 0;
        private bytes = 0;
        constructor(url: string | URL, options?: WorkerOptions) {
          super(url, options);
          this.addEventListener("message", event => timings.push({ engine: "worker", implementation: event.data.implementation, milliseconds: performance.now() - this.started, bytes: this.bytes }));
        }
        postMessage(message: unknown, transfer: Transferable[]) {
          this.started = performance.now();
          this.bytes = (message as ArrayBuffer).byteLength;
          super.postMessage(message, transfer);
        }
      };
      if (crypto.subtle) {
        const original = crypto.subtle.digest.bind(crypto.subtle);
        crypto.subtle.digest = async (algorithm, data) => {
          const started = performance.now();
          const result = await original(algorithm, data);
          timings.push({ engine: "native", milliseconds: performance.now() - started, bytes: data.byteLength });
          return result;
        };
      }
    });
    const apiResponse = await page.request.get("http://api:28647/api/v1/version");
    const csp = apiResponse.headers()["content-security-policy"];
    expect(csp).toContain("script-src 'self'");
    // The HTTP and HTTPS test origins both serve the built SPA with the API's
    // actual CSP. Route all resources to avoid classifying a fulfilled document
    // as public while its Docker-network subresources are private in Chromium.
    const origin = `${secure ? "https" : "http"}://font-proxy.test`;
    await page.route(`${origin}/**`, async route => {
      const source = new URL(route.request().url());
      if (source.pathname.startsWith("/api/")) { await route.fallback(); return; }
      const response = await route.fetch({ url: `http://web:5173${source.pathname}${source.search}`, headers: { ...route.request().headers(), host: "web:5173" } });
      let policy = csp;
      if (source.pathname.startsWith("/assets/RuntimeFontDigest.worker-")) {
        const workerResponse = await page.request.get(`http://api:28647${source.pathname}`);
        policy = workerResponse.headers()["content-security-policy"];
        expect(policy).toContain("'wasm-unsafe-eval'");
        if (blockWasm) policy = policy.replace(" 'wasm-unsafe-eval'", "");
      }
      const headers = { ...response.headers(), "content-security-policy": policy };
      delete headers["content-length"];
      delete headers["content-encoding"];
      await route.fulfill({ status: response.status(), body: await response.body(), headers });
    });
    await page.goto(`${origin}/sessions/new`);
    expect(await page.evaluate(() => Boolean(crypto.subtle))).toBe(secure);
    await previewReady(page);
    const first = await page.evaluate(() => (window as unknown as { fontDigestTimings: { engine: string; implementation?: string; milliseconds: number; bytes: number }[] }).fontDigestTimings);
    expect(first).toHaveLength(1);
    expect(first[0].engine).toBe(secure ? "native" : "worker");
    if (!secure) expect(first[0].implementation).toBe(blockWasm ? "javascript" : "wasm");
    expect(first[0].bytes).toBeGreaterThan(5_000_000);
    // A broad regression bound in the test container, not a device SLA.
    expect(first[0].milliseconds).toBeLessThan(3_000);
    await page.reload();
    await previewReady(page);
    expect(downloads()).toBe(1);
    const cached = await page.evaluate(() => (window as unknown as { fontDigestTimings: unknown[] }).fontDigestTimings);
    await testInfo.attach("font-verification-timing", { body: JSON.stringify({ first, cached }), contentType: "application/json" });
    console.log(`${testInfo.project.name} ${label}: ${JSON.stringify({ first, cached })}`);
  });
}

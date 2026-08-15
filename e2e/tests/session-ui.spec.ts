import { expect, test } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const temporaryPassword = "session-ui-temporary-password";
const administratorPassword = "session-ui-administrator-password";
const adminEmail = "session-ui-admin@example.test";

function crc32(bytes: Uint8Array): number {
  let value = 0xffffffff;
  for (const byte of bytes) {
    value ^= byte;
    for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0);
  }
  return (value ^ 0xffffffff) >>> 0;
}

type ZipEntry = [string, string | Uint8Array];

function zipStored(entries: ZipEntry[]): Uint8Array {
  const encoded = entries.map(([name, content]) => ({ name: new TextEncoder().encode(name), content: typeof content === "string" ? new TextEncoder().encode(content) : content }));
  const localBytes = encoded.reduce((sum, entry) => sum + 30 + entry.name.length + entry.content.length, 0);
  const centralBytes = encoded.reduce((sum, entry) => sum + 46 + entry.name.length, 0);
  const result = new Uint8Array(localBytes + centralBytes + 22);
  const view = new DataView(result.buffer);
  const u16 = (offset: number, value: number) => view.setUint16(offset, value, true);
  const u32 = (offset: number, value: number) => view.setUint32(offset, value >>> 0, true);
  let localOffset = 0;
  let centralOffset = localBytes;
  for (const entry of encoded) {
    const checksum = crc32(entry.content);
    u32(localOffset, 0x04034b50); u16(localOffset + 4, 20); u16(localOffset + 6, 0); u16(localOffset + 8, 0);
    u16(localOffset + 10, 0); u16(localOffset + 12, 0); u32(localOffset + 14, checksum);
    u32(localOffset + 18, entry.content.length); u32(localOffset + 22, entry.content.length);
    u16(localOffset + 26, entry.name.length); u16(localOffset + 28, 0);
    result.set(entry.name, localOffset + 30); result.set(entry.content, localOffset + 30 + entry.name.length);

    u32(centralOffset, 0x02014b50); u16(centralOffset + 4, 20); u16(centralOffset + 6, 20); u16(centralOffset + 8, 0); u16(centralOffset + 10, 0);
    u16(centralOffset + 12, 0); u16(centralOffset + 14, 0); u32(centralOffset + 16, checksum);
    u32(centralOffset + 20, entry.content.length); u32(centralOffset + 24, entry.content.length);
    u16(centralOffset + 28, entry.name.length); u16(centralOffset + 30, 0); u16(centralOffset + 32, 0);
    u16(centralOffset + 34, 0); u16(centralOffset + 36, 0); u32(centralOffset + 38, 0); u32(centralOffset + 42, localOffset);
    result.set(entry.name, centralOffset + 46);
    localOffset += 30 + entry.name.length + entry.content.length;
    centralOffset += 46 + entry.name.length;
  }
  u32(centralOffset, 0x06054b50); u16(centralOffset + 4, 0); u16(centralOffset + 6, 0); u16(centralOffset + 8, encoded.length); u16(centralOffset + 10, encoded.length);
  u32(centralOffset + 12, centralBytes); u32(centralOffset + 16, localBytes); u16(centralOffset + 20, 0);
  return result;
}

async function csrf(page: import("@playwright/test").Page): Promise<string> {
  return page.evaluate(async () => (await (await fetch("/api/v1/auth/csrf", { credentials: "same-origin" })).json() as { token: string }).token);
}

async function jsonRequest(page: import("@playwright/test").Page, path: string, method: string, body: unknown, headers: Record<string, string> = {}) {
  return page.evaluate(async ({ path, method, body, headers }) => {
    const response = await fetch(`/api/v1${path}`, { method, credentials: "same-origin", headers: { "Content-Type": "application/json", ...headers }, body: JSON.stringify(body) });
    return { status: response.status, body: await response.json().catch(() => null) as Record<string, unknown> | null };
  }, { path, method, body, headers });
}

async function uploadPackage(page: import("@playwright/test").Page, bytes: Uint8Array, token: string, suffix: string) {
  return page.evaluate(async ({ bytes, token, suffix }) => {
    const response = await fetch("/api/v1/game-package-ingestions", {
      method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/zip", "X-CSRF-TOKEN": token, "Idempotency-Key": `session-ui-ingest-${suffix}` }, body: new Uint8Array(bytes),
    });
    return { status: response.status, body: await response.json() as { ingestionId: string; manifest: { contentDigest: string } } };
  }, { bytes: Array.from(bytes), token, suffix });
}

const fixturePng = new Uint8Array(readFileSync(fileURLToPath(new URL("../../tests/fixtures/runtime/em-ee-core/resources/cloudemuera-em-ee.png", import.meta.url))));

type FixtureKind = "basic" | "timed" | "rich";
type PreparedGame = { id: string; name: string; stateVersion?: number; status?: string; hasCurrentContent?: boolean };

async function prepareGame(page: import("@playwright/test").Page, suffix: string, kind: FixtureKind = "basic"): Promise<PreparedGame> {
  const gameName = `P1-11 Session Fixture ${suffix}`;
  const existing = await findPreparedGame(page, gameName);
  if (existing?.status === "ACTIVE" && existing.hasCurrentContent) return existing;
  let token = await csrf(page);
  const created = existing
    ? null
    : await jsonRequest(page, "/games", "POST", { name: gameName, visibility: "PRIVATE" }, { "X-CSRF-TOKEN": token, "Idempotency-Key": `session-ui-game-${suffix}` });
  if (created) expect(created.status).toBe(201);
  const game = existing ?? (created?.body as { id: string; stateVersion: number } | undefined);
  if (!game?.id || game.stateVersion === undefined) throw new Error(`Fixture game ${gameName} did not return a usable state version.`);
  const script = kind === "timed"
    ? "@SYSTEM_TITLE\nPRINTL TIMED-READY\nTINPUT 20000, 7, 1, \"TIME-UP\"\nPRINTFORML TIMED-RESULT={RESULT}\nPRINTL TIMED-AFTER\nQUIT\n"
    : kind === "rich"
      ? "@SYSTEM_TITLE\nPRINTL RICH-READY\nHTML_PRINT \"<b>RICH-HTML</b><i>RICH-ITALIC</i>\"\nHTML_PRINT_ISLAND \"<strong>RICH-ISLAND</strong>\"\nPRINT_IMG \"RICH\"\nPRINT_RECT 10,10,40,40\nSETBGIMAGE RICH,0,128\nPRINTL RICH-INPUT\nINPUT\nIF RESULT == 13\nPRINT_RECT 10px,10px,40px,40px\nENDIF\nPRINTL RICH-AFTER\nQUIT\n"
      : "@SYSTEM_TITLE\nPRINTL SESSION-READY\nINPUT\nPRINTL SESSION-INPUT\nQUIT\n";
  const entries: ZipEntry[] = [
    ["CSV/GAMEBASE.CSV", "title,session-ui\n"],
    ["ERB/START.ERB", script],
    ["emuera.config", "Use sav folder:NO\n"],
  ];
  if (kind === "rich") entries.push(["resources/sprites.csv", "RICH,rich.png,0,0,2,2\n"], ["resources/rich.png", fixturePng]);
  const archive = zipStored(entries);
  token = await csrf(page);
  const ingested = await uploadPackage(page, archive, token, suffix);
  expect(ingested.status).toBe(201);
  token = await csrf(page);
  const bound = await jsonRequest(page, `/games/${game.id}/package`, "PUT", { ingestionId: ingested.body.ingestionId, contentDigest: ingested.body.manifest.contentDigest }, { "X-CSRF-TOKEN": token, "If-Match": `"${game.stateVersion}"`, "Idempotency-Key": `session-ui-bind-${suffix}` });
  expect(bound.status).toBe(200);
  token = await csrf(page);
  const validation = await jsonRequest(page, `/games/${game.id}:validate`, "POST", {}, { "X-CSRF-TOKEN": token, "If-Match": `"${(bound.body as { stateVersion: number }).stateVersion}"`, "Idempotency-Key": `session-ui-validate-${suffix}` });
  expect(validation.status).toBe(200);
  expect((validation.body as { canActivate: boolean }).canActivate).toBe(true);
  token = await csrf(page);
  const activated = await jsonRequest(page, `/games/${game.id}:activate`, "POST", {}, { "X-CSRF-TOKEN": token, "If-Match": `"${(validation.body as { stateVersion: number }).stateVersion}"`, "Idempotency-Key": `session-ui-activate-${suffix}` });
  expect(activated.status).toBe(200);
  return { id: game.id, name: gameName };
}

async function findPreparedGame(page: import("@playwright/test").Page, namePrefix: string): Promise<PreparedGame | null> {
  const result = await page.evaluate(async () => {
    const response = await fetch("/api/v1/games", { credentials: "same-origin" });
    return { status: response.status, body: await response.json().catch(() => null) as { items?: Array<{ id?: string; name?: string; stateVersion?: number; status?: string; hasCurrentContent?: boolean }> } | null };
  });
  if (result.status !== 200) return null;
  const game = result.body?.items?.find(item => typeof item.id === "string" && typeof item.name === "string" && item.name.startsWith(namePrefix));
  return game?.id && game.name ? { id: game.id, name: game.name, stateVersion: game.stateVersion, status: game.status, hasCurrentContent: game.hasCurrentContent } : null;
}

async function waitForPreparedGame(page: import("@playwright/test").Page, namePrefix: string): Promise<PreparedGame | null> {
  for (let attempt = 0; attempt < 90; attempt++) {
    const game = await findPreparedGame(page, namePrefix);
    if (game) return game;
    await new Promise(resolve => setTimeout(resolve, 1_000));
  }
  return null;
}

async function createSession(page: import("@playwright/test").Page, game: PreparedGame, suffix: string): Promise<string> {
  await page.goto(`/sessions/new?game=${encodeURIComponent(game.id)}`);
  await expect(page.getByRole("heading", { name: "创建 Session" })).toBeVisible();
  await page.getByLabel("游戏").selectOption({ label: game.name });
  await page.getByLabel("Session 名称").fill(`P1-11 Browser Journey ${suffix}`);
  await page.getByRole("button", { name: "创建并开始" }).click();
  await expect(page).toHaveURL(/\/sessions\/(?!new(?:[/?]|$))[^/?]+$/, { timeout: 60_000 });
  return new URL(page.url()).pathname.split("/").pop()!;
}

async function getSessionState(page: import("@playwright/test").Page, sessionId: string): Promise<{ state: string; workerEpoch: number }> {
  return page.evaluate(async id => {
    const response = await fetch(`/api/v1/sessions/${encodeURIComponent(id)}`, { credentials: "same-origin" });
    if (!response.ok) throw new Error(`Session detail request failed: ${response.status}`);
    return await response.json() as { state: string; workerEpoch: number };
  }, sessionId);
}

async function leaveSessionPage(page: import("@playwright/test").Page): Promise<void> {
  const closeButton = page.getByRole("button", { name: "关闭 Session" });
  if (await closeButton.isEnabled()) {
    await page.evaluate(() => { window.confirm = () => true; });
    await closeButton.click({ force: true });
  }
  await expect.poll(async () => /\/sessions$/.test(page.url()) || await page.getByText("已关闭").first().isVisible() || await page.getByText("Session 实时流已结束").first().isVisible(), { timeout: 30_000 }).toBe(true);
  if (!/\/sessions$/.test(page.url())) await page.goto("/sessions");
}

async function login(page: import("@playwright/test").Page): Promise<void> {
  for (const password of [administratorPassword, temporaryPassword]) {
    await page.goto("/login");
    await page.getByLabel("登录邮箱").fill(adminEmail);
    await page.getByLabel("密码").fill(password);
    await page.getByRole("button", { name: "登录" }).click();
    try {
      await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible({ timeout: 2_000 });
      return;
    } catch {
      if (password !== temporaryPassword) continue;
      await expect(page.getByRole("heading", { name: "修改密码" })).toBeVisible();
      await page.getByLabel("当前密码").fill(temporaryPassword);
      await page.getByLabel("新密码", { exact: true }).fill(administratorPassword);
      await page.getByLabel("确认新密码").fill(administratorPassword);
      await page.getByRole("button", { name: "保存新密码" }).click();
      await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible({ timeout: 10_000 });
      return;
    }
  }
  throw new Error("The session UI E2E account could not authenticate.");
}

const preparedGames = new Map<string, PreparedGame>();

test.beforeAll(async ({ browser }, testInfo) => {
  testInfo.setTimeout(120_000);
  const page = await browser.newPage();
  try {
    await login(page);
    const project = testInfo.project.name;
    let basic: PreparedGame | null = preparedGames.get("chromium:basic") ?? null;
    if (!basic && project !== "chromium") {
      basic = await waitForPreparedGame(page, "P1-11 Session Fixture shared-basic-chromium-");
    }
    preparedGames.set(`${project}:basic`, basic ?? await prepareGame(page, `shared-basic-${project}`));
    if (project === "chromium") {
      preparedGames.set(`${project}:timed`, await prepareGame(page, `shared-timed-${project}`, "timed"));
      preparedGames.set(`${project}:rich`, await prepareGame(page, `shared-rich-${project}`, "rich"));
    }
  } finally {
    await page.close();
  }
});

function preparedGame(project: string, kind: FixtureKind = "basic"): PreparedGame {
  const game = preparedGames.get(`${project}:${kind}`);
  if (!game) throw new Error(`Missing prepared ${kind} fixture for ${project}.`);
  return game;
}

test("P1-11 real Session create, console input, close, save, and reopen", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page);
  const suffix = testInfo.project.name.replace(/[^A-Za-z0-9-]/g, "-");
  const game = preparedGame(testInfo.project.name);
  const sessionId = await createSession(page, game, suffix);
  await expect(page.getByRole("main", { name: "游戏控制台" })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".scrollback")).toContainText("SESSION-READY", { timeout: 30_000 });

  const input = page.getByRole("spinbutton", { name: "游戏输入" });
  await expect(input).toBeVisible({ timeout: 15_000 });
  await input.fill("7");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".scrollback")).toContainText("SESSION-INPUT", { timeout: 30_000 });

  await leaveSessionPage(page);

  await page.goto(`/sessions/${sessionId}/saves`);
  await expect(page.getByRole("heading", { name: "原生存档" })).toBeVisible();
  await page.getByLabel("导入文件").setInputFiles({ name: "global.sav", mimeType: "application/octet-stream", buffer: Buffer.from("0\n0\n") });
  await page.getByLabel("目标路径").fill("global.sav");
  await page.getByRole("button", { name: "导入 / 替换" }).click();
  await expect(page.getByText("global.sav")).toBeVisible({ timeout: 30_000 });
  const download = page.waitForEvent("download");
  await page.getByRole("link", { name: "下载 global.sav" }).click();
  await expect((await download).suggestedFilename()).toBe("global.sav");

  await page.goto("/sessions");
  const sessionRow = page.locator("article.session-row").filter({ hasText: `P1-11 Browser Journey ${suffix}` });
  await sessionRow.getByRole("button", { name: "继续游戏" }).click();
  await expect(page).toHaveURL(new RegExp(`/sessions/${sessionId}$`));
  await expect(page.getByRole("main", { name: "游戏控制台" })).toBeVisible({ timeout: 30_000 });
  await leaveSessionPage(page);
});

test("P1-11 timed prompt survives refresh and closes on the Worker deadline", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  test.skip(testInfo.project.name !== "chromium", "The timed prompt vertical runs once in desktop Chromium.");
  await login(page);
  const suffix = `timed-${testInfo.project.name}`;
  const game = preparedGame(testInfo.project.name, "timed");
  const sessionId = await createSession(page, game, suffix);
  await expect(page.locator(".scrollback")).toContainText("TIMED-READY", { timeout: 30_000 });
  await expect(page.getByRole("timer")).toBeVisible({ timeout: 15_000 });
  await page.reload();
  await expect(page.getByRole("timer")).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".scrollback")).toContainText("TIMED-AFTER", { timeout: 30_000 });
  await expect(page.locator(".scrollback")).toContainText("TIMED-RESULT=7", { timeout: 30_000 });
  await expect(page.getByText("Session 实时流已结束")).toBeVisible({ timeout: 30_000 });
  await page.goto("/sessions");
  await expect(page).toHaveURL(/\/sessions$/);
  void sessionId;
});

test("P1-11 rich fixture renders HTML, Sprite, Shape, background, and input", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  test.skip(testInfo.project.name !== "chromium", "The rich renderer vertical runs once in desktop Chromium.");
  await login(page);
  const suffix = `rich-${testInfo.project.name}`;
  const game = preparedGame(testInfo.project.name, "rich");
  await createSession(page, game, suffix);
  await expect(page.locator(".scrollback")).toContainText("RICH-READY", { timeout: 30_000 });
  await expect(page.getByText("RICH-HTML")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText("RICH-ITALIC")).toBeVisible();
  await expect(page.getByText("RICH-ISLAND")).toBeVisible();
  await expect(page.locator("canvas.console-sprite")).toBeVisible();
  await expect(page.locator("svg.console-shape")).toBeVisible();
  await expect(page.locator(".canvas-background")).toBeVisible();
  await expect(page.locator(".scrollback")).toContainText("RICH-INPUT", { timeout: 30_000 });
  await page.getByRole("spinbutton", { name: "游戏输入" }).fill("7");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".scrollback")).toContainText("RICH-AFTER", { timeout: 30_000 });
  await expect(page.getByText("Session 实时流已结束")).toBeVisible({ timeout: 30_000 });
  await page.goto("/sessions");
});

test("P1-11 crashed Worker reopens the same Session with a fenced epoch", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  test.skip(testInfo.project.name !== "chromium", "The crash/reopen vertical runs once in desktop Chromium.");
  await login(page);
  const game = preparedGame(testInfo.project.name, "rich");
  const sessionId = await createSession(page, game, `crash-reopen-${testInfo.project.name}`);
  await expect(page.locator(".scrollback")).toContainText("RICH-READY", { timeout: 30_000 });
  await expect(page.locator(".scrollback")).toContainText("RICH-INPUT", { timeout: 30_000 });
  const running = await getSessionState(page, sessionId);
  expect(running.state).toBe("RUNNING");

  await page.getByRole("spinbutton", { name: "游戏输入" }).fill("13");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.getByText("Session 实时流已结束")).toBeVisible({ timeout: 30_000 });
  await expect.poll(async () => (await getSessionState(page, sessionId)).state, { timeout: 30_000 }).toBe("CRASHED");
  await expect(page.getByText("已崩溃", { exact: true }).first()).toBeVisible({ timeout: 30_000 });
  const crashed = await getSessionState(page, sessionId);
  expect(crashed.workerEpoch).toBe(running.workerEpoch);

  await page.goto("/sessions");
  const sessionRow = page.locator("article.session-row").filter({ hasText: `P1-11 Browser Journey crash-reopen-${testInfo.project.name}` });
  await sessionRow.getByRole("button", { name: "继续游戏" }).click();
  await expect(page).toHaveURL(new RegExp(`/sessions/${sessionId}$`));
  await expect(page.locator(".scrollback")).toContainText("RICH-READY", { timeout: 30_000 });
  await expect.poll(async () => (await getSessionState(page, sessionId)).state, { timeout: 30_000 }).toBe("RUNNING");
  const reopened = await getSessionState(page, sessionId);
  expect(reopened.workerEpoch).toBe(crashed.workerEpoch + 1);
  await expect(page.locator(".scrollback")).not.toContainText("RICH-AFTER");

  await page.getByRole("spinbutton", { name: "游戏输入" }).fill("7");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".scrollback")).toContainText("RICH-AFTER", { timeout: 30_000 });
  await leaveSessionPage(page);
});

test("P1-11 two browser contexts converge on one accepted input", async ({ page, browser }, testInfo) => {
  test.setTimeout(120_000);
  test.skip(testInfo.project.name !== "chromium", "The concurrent-client vertical runs once in desktop Chromium.");
  await login(page);
  const suffix = `concurrent-${testInfo.project.name}`;
  const game = preparedGame(testInfo.project.name);
  const sessionId = await createSession(page, game, suffix);
  await expect(page.getByRole("spinbutton", { name: "游戏输入" })).toBeVisible({ timeout: 30_000 });
  const secondContext = await browser.newContext({ storageState: await page.context().storageState() });
  const secondPage = await secondContext.newPage();
  try {
    await secondPage.goto(`/sessions/${sessionId}`);
    await expect(secondPage.getByRole("spinbutton", { name: "游戏输入" })).toBeVisible({ timeout: 30_000 });
    await Promise.all([
      page.getByRole("spinbutton", { name: "游戏输入" }).fill("5").then(() => page.getByRole("button", { name: "发送" }).click()),
      secondPage.getByRole("spinbutton", { name: "游戏输入" }).fill("6").then(() => secondPage.getByRole("button", { name: "发送" }).click()),
    ]);
    await expect(page.locator(".scrollback")).toContainText("SESSION-INPUT", { timeout: 30_000 });
    await expect.poll(async () => `${await page.locator(".console-receipt").allTextContents()} ${await secondPage.locator(".console-receipt").allTextContents()}`, { timeout: 30_000 }).toMatch(/已接受|冲突|已失效/);
  } finally {
    await secondContext.close();
    await page.goto("/sessions");
  }
});

test("P1-11 mobile input survives offline display and online reconnect", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  test.skip(!["mobile-chrome", "mobile-safari"].includes(testInfo.project.name), "The network/mobile vertical runs on mobile browser projects.");
  await login(page);
  const suffix = `mobile-network-${testInfo.project.name}`;
  const game = preparedGame(testInfo.project.name);
  await createSession(page, game, suffix);
  await expect(page.getByRole("spinbutton", { name: "游戏输入" })).toBeVisible({ timeout: 30_000 });
  await page.evaluate(() => window.dispatchEvent(new Event("offline")));
  await expect(page.getByText("浏览器离线").first()).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new Event("online")));
  await expect(page.getByText("浏览器离线").first()).toBeHidden({ timeout: 30_000 });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole("spinbutton", { name: "游戏输入" })).toBeVisible();
  await page.getByRole("spinbutton", { name: "游戏输入" }).fill("7");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".scrollback")).toContainText("SESSION-INPUT", { timeout: 30_000 });
  await page.goto("/sessions");
});

test("P1-11 unauthorized user cannot open another user's Session", async ({ page, browser }, testInfo) => {
  test.setTimeout(120_000);
  test.skip(testInfo.project.name !== "chromium", "The authorization vertical runs once in desktop Chromium.");
  await login(page);
  const suffix = `unauthorized-${testInfo.project.name}`;
  const game = preparedGame(testInfo.project.name);
  const sessionId = await createSession(page, game, suffix);
  const temporaryPlayerPassword = "session-ui-player-temporary-password";
  const permanentPlayerPassword = "session-ui-player-permanent-password";
  const playerEmail = `session-ui-player-${Date.now()}@example.test`;
  const token = await csrf(page);
  const created = await jsonRequest(page, "/admin/users", "POST", { username: `session-ui-player-${Date.now()}`, email: playerEmail, temporaryPassword: temporaryPlayerPassword, role: "PLAYER" }, { "X-CSRF-TOKEN": token });
  expect(created.status).toBe(201);
  const playerContext = await browser.newContext();
  const playerPage = await playerContext.newPage();
  try {
    await playerPage.goto("/login");
    await playerPage.getByLabel("登录邮箱").fill(playerEmail);
    await playerPage.getByLabel("密码").fill(temporaryPlayerPassword);
    await playerPage.getByRole("button", { name: "登录" }).click();
    await expect(playerPage.getByRole("heading", { name: "修改密码" })).toBeVisible({ timeout: 30_000 });
    await playerPage.getByLabel("当前密码").fill(temporaryPlayerPassword);
    await playerPage.getByLabel("新密码", { exact: true }).fill(permanentPlayerPassword);
    await playerPage.getByLabel("确认新密码").fill(permanentPlayerPassword);
    await playerPage.getByRole("button", { name: "保存新密码" }).click();
    await expect(playerPage.getByRole("heading", { name: "游戏库" })).toBeVisible({ timeout: 30_000 });
    await playerPage.goto(`/sessions/${sessionId}`);
    await expect(playerPage.getByRole("heading", { name: "Session 不可用" })).toBeVisible({ timeout: 30_000 });
  } finally {
    await playerContext.close();
    await page.goto("/sessions");
  }
});

test("P1-11 ended stream invalidates the Session detail state", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  test.skip(testInfo.project.name !== "chromium", "The ended-stream vertical runs once in desktop Chromium.");
  await login(page);
  const suffix = `ended-${testInfo.project.name}`;
  const game = preparedGame(testInfo.project.name);
  await createSession(page, game, suffix);
  await expect(page.getByRole("spinbutton", { name: "游戏输入" })).toBeVisible({ timeout: 30_000 });
  await page.getByRole("spinbutton", { name: "游戏输入" }).fill("7");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.getByText("Session 实时流已结束")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText("已关闭", { exact: true }).first()).toBeVisible({ timeout: 30_000 });
  await page.goto("/sessions");
});

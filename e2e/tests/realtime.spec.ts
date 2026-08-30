import { expect, test } from "@playwright/test";

const temporaryPassword = "temporary-password";
const administratorPassword = "administrator-password";

async function loginForRealtime(page: import("@playwright/test").Page) {
  // The identity E2E journey changes this account's bootstrap password. The
  // two tests may start concurrently, so tolerate that short transition and
  // retry both valid credentials within a bounded interval.
  for (let attempt = 0; attempt < 12; attempt++) {
    for (const password of [administratorPassword, temporaryPassword]) {
      await page.goto("/login");
      await page.getByLabel("登录邮箱").fill("identity-admin@example.test");
      await page.getByLabel("密码").fill(password);
      await page.getByRole("button", { name: "登录" }).click();

      try {
        await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible({ timeout: 1_500 });
        return;
      } catch {
        if (password !== temporaryPassword)
          continue;
        try {
          await expect(page.getByRole("heading", { name: "修改密码" })).toBeVisible({ timeout: 500 });
          await page.getByLabel("当前密码").fill(temporaryPassword);
          await page.getByLabel("新密码", { exact: true }).fill(administratorPassword);
          await page.getByLabel("确认新密码").fill(administratorPassword);
          await page.getByRole("button", { name: "保存新密码" }).click();
          await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible({ timeout: 2_000 });
          return;
        } catch {
          // Another worker may have completed the password transition.
        }
      }
    }
    await page.waitForTimeout(250);
  }

  throw new Error("The realtime smoke test could not authenticate the bootstrap administrator.");
}

test("authenticated browser can complete the realtime v4 hello handshake", async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  test.skip(!["chromium", "mobile-chrome"].includes(testInfo.project.name), "The protocol smoke runs in the two E2E journeys.");
  await loginForRealtime(page);

  const hello = await page.evaluate(() => new Promise<{ type: string; protocolVersion: number }>((resolve, reject) => {
    const socket = new WebSocket(
      `${window.location.origin.replace(/^http/, "ws")}/api/v1/realtime`,
      "cloudemuera.realtime.v6");
    const timeout = window.setTimeout(() => {
      socket.close();
      reject(new Error("realtime hello timed out"));
    }, 10_000);

    socket.onopen = () => socket.send(JSON.stringify({
      protocolVersion: 6,
      type: "client.hello",
      messageId: "msg_e2e_realtime_hello",
      payload: {
        supportedProtocolVersions: [6],
          capabilityDigest: "9a5d4b9b8eef946adc5566bc9ae2aa88881bbfd9f1ec27628c52564956de6ef8",
        supportedCapabilities: [],
      },
    }));
    socket.onmessage = event => {
      const message = JSON.parse(String(event.data)) as { type: string; protocolVersion: number };
      if (message.type !== "server.hello") return;
      window.clearTimeout(timeout);
      socket.close(1000, "test_complete");
      resolve(message);
    };
    socket.onerror = () => {
      window.clearTimeout(timeout);
      reject(new Error("realtime WebSocket failed"));
    };
  }));

  expect(hello).toMatchObject({ type: "server.hello", protocolVersion: 6 });
});

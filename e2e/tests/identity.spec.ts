import { expect, test } from "@playwright/test";

const temporaryPassword = "temporary-password";
const administratorPassword = "administrator-password";
const playerTemporaryPassword = "player-temporary-password";
const playerPassword = "player-password-updated";

async function login(page: import("@playwright/test").Page, email: string, password: string) {
  await page.goto("/login");
  await page.getByLabel("登录邮箱").fill(email);
  await page.getByLabel("密码").fill(password);
  await page.getByRole("button", { name: "登录" }).click();
}

test("bootstrap admin completes password change, manages a player, and player cannot enter admin UI", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === "mobile-chrome", "Mobile has its own post-bootstrap journey.");
  await login(page, "identity-admin@example.test", temporaryPassword);
  await expect(page.getByRole("heading", { name: "修改密码" })).toBeVisible();
  await page.getByLabel("当前密码").fill(temporaryPassword);
  await page.getByLabel("新密码", { exact: true }).fill(administratorPassword);
  await page.getByLabel("确认新密码").fill(administratorPassword);
  await page.getByRole("button", { name: "保存新密码" }).click();
  await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible();

  await page.goto("/admin/users");
  await expect(page.getByRole("heading", { name: "用户管理" })).toBeVisible();
  await page.getByLabel("用户名").fill("e2e-player");
  await page.getByLabel("登录邮箱").fill("e2e-player@example.test");
  await page.getByLabel("临时密码").fill(playerTemporaryPassword);
  await page.getByRole("button", { name: "创建用户" }).click();
  await expect(page.getByText("e2e-player@example.test")).toBeVisible();

  await page.getByRole("button", { name: /identity-admin/ }).click();
  await expect(page).toHaveURL(/\/login(?:\?|$)/);
  await login(page, "e2e-player@example.test", playerTemporaryPassword);
  await page.getByLabel("当前密码").fill(playerTemporaryPassword);
  await page.getByLabel("新密码", { exact: true }).fill(playerPassword);
  await page.getByLabel("确认新密码").fill(playerPassword);
  await page.getByRole("button", { name: "保存新密码" }).click();
  await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible();
  await page.goto("/admin/users");
  await expect(page).toHaveURL(/\/games$/);
  await expect(page.getByRole("link", { name: "用户管理" })).toHaveCount(0);
});

test("mobile viewport logs in by email, reports password confirmation errors, and logs out", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile-chrome", "This is the required mobile P1-02 journey.");
  await login(page, "identity-admin@example.test", administratorPassword);
  await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible();
  await page.goto("/admin/users");
  await page.getByLabel("用户名").fill("mobile-player");
  await page.getByLabel("登录邮箱").fill("mobile-player@example.test");
  await page.getByLabel("临时密码").fill("mobile-player-temporary-password");
  await page.getByRole("button", { name: "创建用户" }).click();
  await page.getByRole("button", { name: "打开导航" }).click();
  await page.getByRole("button", { name: /identity-admin/ }).click();
  await login(page, "mobile-player@example.test", "mobile-player-temporary-password");
  await expect(page.getByRole("heading", { name: "修改密码" })).toBeVisible();
  await page.getByLabel("当前密码").fill("mobile-player-temporary-password");
  await page.getByLabel("新密码", { exact: true }).fill("mobile-player-new-password");
  await page.getByLabel("确认新密码").fill("different-password-value");
  await page.getByRole("button", { name: "保存新密码" }).click();
  await expect(page.getByRole("alert")).toHaveText("两次输入的新密码不一致。");
  await page.getByLabel("确认新密码").fill("mobile-player-new-password");
  await page.getByRole("button", { name: "保存新密码" }).click();
  await expect(page.getByRole("heading", { name: "游戏库" })).toBeVisible();
  await page.getByRole("button", { name: "打开导航" }).click();
  await page.getByRole("button", { name: /mobile-player/ }).click();
  await expect(page).toHaveURL(/\/login(?:\?|$)/);
});

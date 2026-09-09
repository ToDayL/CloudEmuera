import { expect, test } from "@playwright/test";

test("persists an explicit login locale and account setting across reloads", async ({ page }) => {
  test.setTimeout(60_000);
  await page.goto("/login");

  await page.getByRole("combobox").first().selectOption("ja-JP");
  await expect(page.getByRole("heading", { name: "CloudEmuera にログイン" })).toBeVisible();
  await page.locator('input[type="email"]').fill("i18n-admin@example.test");
  await page.locator('input[type="password"]').fill("temporary-password");
  await page.getByRole("button", { name: "ログイン" }).click();

  await expect(page).toHaveURL(/\/change-password$/);
  await expect(page.getByRole("heading", { name: "パスワード変更" })).toBeVisible();
  const passwordInputs = page.locator('input[type="password"]');
  await passwordInputs.nth(0).fill("temporary-password");
  await passwordInputs.nth(1).fill("i18n-password-123");
  await passwordInputs.nth(2).fill("i18n-password-123");
  await page.getByRole("button", { name: "新しいパスワードを保存" }).click();

  await expect(page).toHaveURL(/\/games$/);
  await expect(page.getByRole("heading", { name: "ゲーム", exact: true })).toBeVisible();
  await page.goto("/settings");
  await page.getByLabel("表示言語").selectOption("en-US");
  await expect(page.getByRole("heading", { name: "Settings" })).toBeVisible();
  await expect(page.locator(".settings-success")).toHaveText("Interface language saved.");
  await page.reload();
  await expect(page.getByRole("heading", { name: "Settings" })).toBeVisible();

  await page.getByLabel("Interface language").selectOption("zh-CN");
  await expect(page.getByRole("heading", { name: "设置" })).toBeVisible();
});

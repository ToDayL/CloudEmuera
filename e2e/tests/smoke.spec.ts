import { expect, test } from "@playwright/test";

test("shows the protected CloudEmuera login shell", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole("heading", { name: "登录 CloudEmuera" })).toBeVisible();
});

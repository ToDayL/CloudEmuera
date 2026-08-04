import { expect, test } from "@playwright/test";

test("shows the CloudEmuera development shell", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "CloudEmuera" })).toBeVisible();
});


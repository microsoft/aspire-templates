import { test, expect } from "@playwright/test";

test("embedded checkout drives warehouse, truck, delivery, decline and reconnect", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto("/");
  await expect(page.getByText("Services connected")).toBeVisible();
  await page.screenshot({ path: "test-results/district-ready.png", fullPage: true });
  const created = page.waitForResponse(response => response.url().endsWith("/api/orders") && response.request().method() === "POST");
  await page.getByRole("button", { name: "Place simulated order" }).click();
  const order = await (await created).json();
  const inspector = page.locator(".timeline");
  await expect(inspector.getByRole("heading", { name: "Picking inventory" })).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ path: "test-results/district-warehouse.png", fullPage: true });
  await expect(inspector.getByRole("heading", { name: "On the road" })).toBeVisible({ timeout: 30_000 });
  const marker = page.locator(".truck-label", { hasText: order.id.slice(0, 8).toUpperCase() });
  await expect(marker).toBeVisible();
  const firstPosition = await marker.evaluate(element => ({ x: element.getBoundingClientRect().x, y: element.getBoundingClientRect().y }));
  await page.waitForTimeout(1500);
  const nextPosition = await marker.evaluate(element => ({ x: element.getBoundingClientRect().x, y: element.getBoundingClientRect().y }));
  expect(Math.hypot(nextPosition.x - firstPosition.x, nextPosition.y - firstPosition.y)).toBeGreaterThan(2);
  await page.screenshot({ path: "test-results/district-truck.png", fullPage: true });
  await expect(inspector.getByRole("heading", { name: "Delivered", exact: true })).toBeVisible({ timeout: 30_000 });
  await page.reload();
  await expect(inspector.getByRole("heading", { name: "Delivered", exact: true })).toBeVisible();
  await expect(inspector.locator(".event-list li")).toHaveCount(6);
  await page.context().setOffline(true);
  await expect(page.getByText("Live connection paused.", { exact: false })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Place simulated order" })).toBeDisabled();
  await page.context().setOffline(false);
  await expect(page.getByText("Services connected")).toBeVisible();
  await page.getByLabel("Mock payment scenario").selectOption("declined");
  await page.getByRole("button", { name: "Place simulated order" }).click();
  await expect(inspector.getByRole("heading", { name: "Payment declined", exact: true })).toBeVisible({ timeout: 15_000 });
  await page.screenshot({ path: "test-results/district-declined.png", fullPage: true });
  await page.getByRole("button", { name: "Retry with approval" }).click();
  await expect(inspector.getByRole("heading", { name: "Delivered", exact: true })).toBeVisible({ timeout: 60_000 });
  await page.screenshot({ path: "test-results/district-delivered.png", fullPage: true });
  expect(errors).toEqual([]);
});

test("unknown deep link, narrow viewport, reduced motion and WebGL loss remain usable", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/?order=00000000-0000-0000-0000-000000000001");
  await expect(page.getByText("Services connected")).toBeVisible();
  await expect(page.getByText("That order is no longer available.", { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: "Place simulated order" })).toBeEnabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.locator("canvas").evaluate(canvas => {
    const gl = (canvas as HTMLCanvasElement).getContext("webgl2");
    gl?.getExtension("WEBGL_lose_context")?.loseContext();
  });
  await expect(page.getByRole("heading", { name: "The district is in accessible mode." })).toBeVisible();
  await expect(page.getByRole("button", { name: "Place simulated order" })).toBeEnabled();
  await page.screenshot({ path: "test-results/district-accessible-mobile.png", fullPage: true });
});

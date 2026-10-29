import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e", timeout: 120_000, workers: 1,
  use: {
    baseURL: process.env.COMMERCE_URL ?? "http://localhost:5000",
    viewport: { width: 1440, height: 1100 },
    video: "on", trace: "retain-on-failure"
  }
});

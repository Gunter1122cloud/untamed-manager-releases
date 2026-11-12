import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  workers: 1,
  reporter: "list",
  timeout: 30_000,
  outputDir: "test-results",
  use: {
    ...devices["Desktop Chrome"],
    baseURL: "http://127.0.0.1:4173",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    // CI has just built the SAME production React bundle packaged into
    // WebView2. Serve dist without Vite HMR or per-request module transforms:
    // a transient dev-server blank page must not mask native acceptance tests.
    // Local developer runs retain the faster editable dev server.
    command: process.env.CI
      ? "npm run preview -- --port 4173 --strictPort"
      : "npm run dev -- --port 4173 --strictPort",
    url: "http://127.0.0.1:4173",
    reuseExistingServer: !process.env.CI,
    timeout: 90_000,
  }
});

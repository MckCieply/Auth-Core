import { defineConfig, devices } from '@playwright/test';

declare const process: { env: Record<string, string | undefined> };

// The tests run against the built app behind Caddy, on HTTPS: WebKit sends the Secure refresh cookie back over HTTPS only.
// The certificate comes from Caddy's own authority, which no browser trusts: ignoreHTTPSErrors.
export default defineConfig({
  testDir: './e2e',
  // One worker, in file order: the stack is shared, and so are the mail limits of an address.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'https://localhost:8443',
    ignoreHTTPSErrors: true,
    // No trace: it would write the tokens and passwords of the run to disk. For the same reason scripts/e2e-web.sh sets
    // PLAYWRIGHT_NO_COPY_PROMPT=1: a failed test would otherwise write an ARIA snapshot (error-context.md) that holds the
    // value of every input, typed passwords included. Set it too when you run `npx playwright test` by hand.
    trace: 'off',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['iPhone 15'] } },
  ],
});

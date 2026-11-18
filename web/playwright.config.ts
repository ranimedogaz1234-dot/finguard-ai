import { defineConfig, devices } from '@playwright/test';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * End-to-end tests run against the real ASP.NET Core API serving the production frontend build
 * (run `npm run build` first) with a fresh, seeded SQLite database.
 */
const PORT = 5090;
const db = join(tmpdir(), `finguard-e2e-${Date.now()}.db`);

export default defineConfig({
  testDir: './e2e',
  timeout: 45_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } },
  ],
  webServer: {
    // Run from the API folder so its content root (appsettings.json, wwwroot with the built SPA) is resolved correctly.
    command: `dotnet run --no-launch-profile --urls http://localhost:${PORT}`,
    cwd: '../src/FinGuard.Api',
    url: `http://localhost:${PORT}/health`,
    timeout: 180_000,
    reuseExistingServer: false,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ConnectionStrings__Default: `Data Source=${db}`,
      Demo__Seed: 'true',
      Demo__Password: 'FinGuard-Demo-2026!',
      RateLimits__AuthPerMinute: '1000',
    },
  },
});

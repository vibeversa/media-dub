import { defineConfig, devices } from '@playwright/test';

// Playwright smoke for the Task 018 shell (@shell): authenticated shell
// renders nav, Admin hides for non-admin. Hermetic — no backend: the session
// is seeded via the `dubbing.e2e.session` localStorage key (see
// StoreProvider.readE2eSessionSeed) because auth session logic lands in
// Task 019.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: {
    command: 'npm run dev -- --port 5173 --strictPort',
    port: 5173,
    reuseExistingServer: !process.env['CI'],
    timeout: 120_000,
  },
});

// Task 040A: root Playwright configuration for the cross-layer rig.
//
// Scope is deliberately narrow. The frontend's own `frontend/e2e/*.spec.ts` are
// mock-based (they stub routes with `page.route`) and are unaffected: this config
// only picks up `tests/cross-layer/**`, and the existing specs keep running under
// Playwright's defaults from `frontend/` exactly as before.
//
// Tag conventions (040A/040B):
//   @cross-layer-harness  the rig smoke in this task
//   @cross-layer          the seven named seam specs added by 040B
//   @cross-layer-ai       seam specs that need a non-default mock-AI scenario
//
// There is no `webServer` block: the stack is started with `docker compose` by
// the documented run command, because the rig's services are health-gated and
// need a database migration and a seed before any browser work is meaningful.
// globalSetup then fails closed if the stack is not actually up.

import { defineConfig, devices } from '@playwright/test';

const CROSS_LAYER_PORT = 54173;

// 127.0.0.1, not localhost: Docker Desktop resets IPv6 connections to published
// ports, so Chromium's `localhost` (which resolves to ::1 first) never reaches
// the container. Must match VITE_API_BASE_URL's origin in
// frontend/.env.cross-layer and the API's CORS allow-list.
const CROSS_LAYER_ORIGIN = `http://127.0.0.1:${CROSS_LAYER_PORT}`;

export default defineConfig({
  testDir: './tests/cross-layer',
  testMatch: /.*\.spec\.ts/,

  // A cold stack can spend real time building the seeder, the frontend bundle and
  // the run's first pipeline stages. The default 30s would abort a green rig.
  timeout: 180_000,
  expect: { timeout: 30_000 },

  // Seams share one seeded tenant and one API; parallel workers would race on
  // the reset and on the processing run. Serial by design, not by omission.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,

  reporter: process.env.CI ? [['github'], ['list']] : [['list']],
  globalSetup: './tests/cross-layer/globalSetup.ts',

  outputDir: './tests/cross-layer/.artifacts',

  use: {
    baseURL: CROSS_LAYER_ORIGIN,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Deterministic clock-independent rendering matters more than raw speed for
    // seam assertions; animations are disabled so a progress bar cannot make a
    // seam look like it did not advance.
    launchOptions: { args: ['--disable-lcd-text'] },
  },

  projects: [
    {
      name: 'cross-layer-chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});

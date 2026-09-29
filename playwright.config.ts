// Root Playwright configuration: the cross-layer rig (040A/040B), the visual
// matrix (041B), the accessibility audit (041C) and the performance gate
// (041D). All four run against the same real stack, so they share one
// `globalSetup` - which is what seeds the deterministic data the visual
// baselines, the a11y audit and the perf fixtures all build on.
//
// The frontend's own `frontend/e2e/*.spec.ts` are mock-based (they stub routes
// with `page.route`) and are unaffected: they keep running under Playwright's
// defaults from `frontend/` exactly as before.
//
// Tag conventions:
//   @cross-layer-harness  the rig smoke (040A)
//   @cross-layer          the seven named seam specs (040B)
//   @cross-layer-ai       seam specs that need a non-default mock-AI scenario
//   @visual               the visual matrix and its structural checks (041B)
//   @a11y                 the WCAG 2.2 AA audit and its manual checks (041C)
//   @perf                 the six performance budgets and their structural
//                         practice assertions (041D)
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
  // Three trees, so the root is the testDir and the inclusions are explicit.
  // Left implicit, Playwright would also collect `frontend/e2e/**` (019-036's
  // mock-based feature specs), which belong to a different config entirely.
  testDir: '.',
  testMatch: [
    'tests/cross-layer/**/*.spec.ts',
    'e2e/visual/**/*.spec.ts',
    'e2e/a11y/**/*.spec.ts',
    'e2e/perf/**/*.spec.ts',
  ],

  // A cold stack can spend real time building the seeder, the frontend bundle and
  // the run's first pipeline stages. The default 30s would abort a green rig.
  timeout: 180_000,
  expect: { timeout: 30_000 },

  // Seams share one seeded tenant and one API; parallel workers would race on
  // the reset and on the processing run. Serial by design, not by omission. The
  // visual matrix also needs one worker: it signs in once per file and holds a
  // single in-memory session.
  //
  // 041D adds a second reason for one worker: `POST /auth/login` is limited to
  // 5/min per IP, and the perf suite's session is cached per `Browser` - i.e.
  // per worker. A second worker would be a second sign-in and a second context
  // racing the same rig, not an extra throughput.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,

  reporter: process.env.CI ? [['github'], ['list']] : [['list']],
  globalSetup: './tests/cross-layer/globalSetup.ts',

  outputDir: './tests/cross-layer/.artifacts',

  // 041B: baselines are flat and self-describing - the test title already
  // carries screen, breakpoint, theme and direction, so no extra folder
  // hierarchy would add information. The `{platform}` token is kept out so a
  // baseline is not silently forked per OS; the font policy is in
  // `e2e/visual/README.md` instead.
  snapshotPathTemplate: '{testDir}/e2e/visual/__screenshots__/{arg}{ext}',

  use: {
    baseURL: CROSS_LAYER_ORIGIN,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Deterministic clock-independent rendering matters more than raw speed for
    // seam assertions; animations are disabled so a progress bar cannot make a
    // seam look like it did not advance.
    launchOptions: { args: ['--disable-lcd-text'] },
  },

  // Two projects, and they are DISJOINT, so nothing runs twice.
  //
  // `cross-layer-chromium` runs the rig seams, the visual matrix and the a11y
  // audit. The a11y audit deliberately does not get its own project: it would
  // run every spec twice, doubling the cross-layer and visual runtime to change
  // nothing, and the two settings it needs are already what the visual matrix
  // uses - `devices['Desktop Chrome']` and the `reducedMotion: 'reduce'` context
  // option in `e2e/visual/support/session.ts`. 041C's specs set their own
  // viewport explicitly so the audit never depends on a shared default.
  //
  // `perf-chromium` exists for one reason: **traces**. The task requires perf
  // traces to be scrubbed of URLs, tokens and media bytes before any CI attach
  // (038's allowlist). Playwright's `trace: 'retain-on-failure'` produces an
  // *unscrubbed* archive - response bodies, screencast frames, `Authorization`
  // headers and presigned URLs - and it owns the trace lifecycle for the whole
  // context, so a spec cannot start its own on the same context. Turning the
  // runner's tracing off for this project is what lets `e2e/perf/support/trace.ts`
  // own the lifecycle: capture, scrub, verify, delete the raw copy. The cost is
  // that a non-gate perf failure (a structural practice assertion, say) attaches
  // no trace; the gate's own breach path is the one that attaches one, and it
  // attaches a scrubbed one.
  //
  // Both projects run with `workers: 1`, so the two never contend for the rig
  // or for the login rate limit.
  projects: [
    {
      name: 'cross-layer-chromium',
      testIgnore: 'e2e/perf/**',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'perf-chromium',
      testMatch: 'e2e/perf/**/*.spec.ts',
      use: { ...devices['Desktop Chrome'], trace: 'off' },
    },
  ],
});

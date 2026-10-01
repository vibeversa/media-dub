// Task 046: the Playwright configuration for the feature-E2E harness.
//
// WHERE THIS SITS RELATIVE TO THE OTHER TWO CONFIGS
// -------------------------------------------------
// The repository has three Playwright configurations and they are deliberately
// not merged:
//
//   playwright.config.ts           the cross-layer rig (040A/040B) plus the
//                                  visual (041B), a11y (041C) and perf (041D)
//                                  gates: one real stack, one `globalSetup`,
//                                  `workers: 1`.
//   frontend/playwright.config.ts  the mock-based feature specs (019-036) that run
//                                  against the Vite dev server with no backend.
//   e2e/playwright.config.ts       THIS file. The shared `support/` helpers, the
//                                  tag vocabulary, and the three required
//                                  browsers.
//
// Merging them would mean either the rig's `workers: 1` applying to every feature
// spec (a suite that parallelises safely paying for one that cannot), or the
// feature harness inheriting the rig's `globalSetup` and trying to boot a Docker
// stack for a spec that needed nothing but a dev server.
//
// WHAT THIS CONFIG OWNS (R2)
// --------------------------
// Tagged runs across the required browsers. A tag is a `@name` in the test
// *title*, so it needs no plugin, is greppable, and appears in the report:
//
//   npx playwright test --config e2e/playwright.config.ts --grep=@smoke
//   npx playwright test --config e2e/playwright.config.ts --grep=@visual
//   npx playwright test --config e2e/playwright.config.ts --grep=@uploads
//
// The browser split is by *role*, not by duplication: `chromium` runs the whole
// functional matrix, and `webkit`/`firefox` run the tagged subsets whose entire
// point is engine coverage (rendering, streaming, layout). Running every spec on
// three engines triples the wall-clock to re-test what 041B/041C already gate on
// Chromium, which is how a suite gets slow enough that people stop running it.
//
// NO `webServer` HERE
// -------------------
// Unlike `frontend/playwright.config.ts`, this harness does not start its own
// server: a feature E2E spec runs against a stack the operator brought up, and
// starting a second server would silently serve a bundle built for a different
// API origin. `E2E_BASE_URL` names the target and defaults to the cross-layer
// rig's frontend.

import { defineConfig, devices } from '@playwright/test';

/**
 * The tag vocabulary, in one place.
 *
 * Exported rather than only documented, so a spec imports the constant instead of
 * typing the string: a renamed tag is then a type error in every spec rather than
 * a `--grep` that quietly matches nothing. `support/smoke.spec.ts` asserts every
 * entry is `@`-prefixed, because a tag without the prefix is invisible to every
 * `--grep` in the repository - the worst possible failure, since the suite looks
 * like it ran.
 */
export const TAGS = {
  /** Runs on every CI push. Seconds, not minutes. */
  smoke: '@smoke',
  /** Visual baselines; the 041B matrix owns the assertions. */
  visual: '@visual',
  /** Needs the API up as well as the frontend. */
  api: '@api',
  /** Touches object storage (upload or export). */
  storage: '@storage',
  /** Feature-area tags. A feature task adds its own here. */
  uploads: '@uploads',
  projects: '@projects',
  processing: '@processing',
  review: '@review',
  exports: '@exports',
  voices: '@voices',
  accessibility: '@a11y',
  performance: '@perf',
} as const;

/** Every tag this harness recognises. */
export const ALL_TAGS: readonly string[] = Object.values(TAGS);

/**
 * Tags that cannot pass without the full stack (API, PostgreSQL, storage).
 *
 * `support/reset.ts` uses this to decide whether a spec can be satisfied
 * hermetically. A spec that declares one of these and finds no stack must FAIL,
 * never skip: a skip here is a green job that ran nothing, which is the failure
 * `scripts/require-docker.sh` exists to make impossible in the backend tiers.
 */
export const REQUIRES_STACK: readonly string[] = [TAGS.api, TAGS.storage];

/**
 * Tags that also run on WebKit and Firefox.
 *
 * The cross-engine subset. Playwright has no per-project "run only these tags"
 * key other than `grep`, so this is expressed as a grep on the two non-default
 * projects and as a matrix below; keeping the list in one place is what stops the
 * three spellings from drifting.
 */
export const CROSS_ENGINE_TAGS: readonly string[] = [
  TAGS.visual,
  TAGS.accessibility,
  TAGS.performance,
];

/**
 * Which tags reach which browser.
 *
 * Exported so a spec author can ask the question without reading the `projects`
 * array, and asserted by `support/smoke.spec.ts` so a project renamed in the
 * config without updating this table is a failing test.
 */
export const ENGINE_TAG_MATRIX: Readonly<Record<'chromium' | 'webkit' | 'firefox', readonly string[]>> = {
  chromium: ALL_TAGS,
  webkit: CROSS_ENGINE_TAGS,
  firefox: CROSS_ENGINE_TAGS,
};

/**
 * A grep matching any of `tags`.
 *
 * The trailing negative lookahead is load-bearing and was missing in the first
 * version: without it `@a11y` also matches `@a11y-extra`, so adding a tag silently
 * widened every existing selection that included its prefix. Escaping alone does
 * not help - `-` is not a regex metacharacter - so the boundary has to be stated.
 *
 * The character class excludes `-` as well as `\w`, because tags are
 * hyphen-separated and `@perf-extra` is exactly the kind of near-duplicate that
 * gets added later.
 */
export function tagGrep(tags: readonly string[]): RegExp {
  const alternatives = tags.map((tag) => tag.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|');
  return new RegExp(`(${alternatives})(?![\\w-])`);
}

const baseURL = process.env['E2E_BASE_URL'] ?? 'http://127.0.0.1:54173';

export default defineConfig({
  // `testDir: '.'` is the `e2e` directory, so the two patterns below resolve to
  // `e2e/support/**` and `e2e/<feature>/**`.
  testDir: '.',
  // `support/**` is the harness's own smoke; `*/**/*.spec.ts` is every feature
  // tree, so a feature task adds a directory and its specs are collected with no
  // config edit. The catch is that the second pattern also reaches the three
  // directories the ROOT config owns, so those are excluded by name below.
  testMatch: ['support/**/*.spec.ts', '*/**/*.spec.ts'],
  //
  // `visual/`, `a11y/` and `perf/` belong to the root config: they carry
  // baselines, waivers and budgets this harness does not understand, and their
  // global setup is the rig's. Excluded by directory name rather than by a
  // `grepInvert`, because a grep would have to enumerate every tag those trees
  // use and would silently widen if one were added.
  //
  // Without this the a11y suite is collected here and fails at import time with
  // "No seed environment snapshot" - which points at the rig rather than at the
  // collection mistake that caused it.
  testIgnore: ['visual/**', 'a11y/**', 'perf/**', '.artifacts/**'],

  // A cold rig can spend real time building the frontend bundle and the seeder;
  // the default 30s would abort a green run.
  timeout: 120_000,
  expect: { timeout: 15_000 },

  // Serial by default. The rig is one shared stack: two workers signing in race
  // the 5/min login rate limit, and two workers mutating one seeded tenant leak
  // into each other. A suite that genuinely parallelises opts in locally with
  // `test.describe.configure`, rather than the whole project opting out.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env['CI'],
  // Zero, always. `e2e/support/quarantine.md` is the only sanctioned way to land
  // a flaky gate: an owner, an issue and an expiry recorded in
  // `docs/ci-quarantine.md`. A retry is a silent quarantine with no owner and no
  // expiry, which is the thing the policy exists to forbid.
  retries: 0,

  reporter: process.env['CI'] ? [['github'], ['list']] : [['list']],
  outputDir: './.artifacts',

  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Deterministic rendering matters more than raw speed for assertions;
    // animations off so a progress bar cannot make a check look like it did not
    // advance.
    launchOptions: { args: ['--disable-lcd-text'] },
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      // Engine coverage, not a second copy of the suite. The grep is the whole
      // mechanism, so the two projects cannot drift into "run everything twice".
      name: 'webkit',
      grep: tagGrep(CROSS_ENGINE_TAGS),
      use: { ...devices['Desktop Safari'] },
    },
    {
      name: 'firefox',
      grep: tagGrep(CROSS_ENGINE_TAGS),
      use: { ...devices['Desktop Firefox'] },
    },
  ],
});
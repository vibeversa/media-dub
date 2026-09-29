// Task 041B: the visual session.
//
// Four constraints shape this file, each found by running it rather than by
// reading the code.
//
// 1. **Tokens are never persisted.** `authStore` keeps the access and refresh
//    tokens in memory and writes only a logout *timestamp* to localStorage
//    ("the sole storage write is a timestamp logout broadcast"). A full document
//    load therefore starts anonymous.
//
// 2. **`POST /auth/login` is rate limited to 5/min per IP.** 144 matrix cells
//    signing in individually would need half an hour of deliberate waiting.
//
// 3. **A `page.goto` is a full document load**, so (1) makes it a logout. The
//    probe is unambiguous: after `page.goto('/projects')` the browser lands on
//    `/login?next=%2Fprojects`.
//
// 4. **A screen's root element is not the same as its content being ready.** The
//    workspace mounts `page-project-details` and then fills in cost, config and
//    activity panels asynchronously. Capturing on mount produced baselines that
//    sometimes included those panels and sometimes did not, which is the worst
//    possible failure for a visual gate - it is green and meaningless.
//
// So the matrix runs on ONE sign-in, navigates entirely in-app, and waits for the
// rendered element set to stop changing before every capture.
//
// The login screen is the one screen that cannot be reached this way: it has no
// shell, and the theme and direction switchers live in the shell. It gets its own
// never-authenticated page, where the axes are set through localStorage and a
// reload - which is free, because there is no session to lose there.

import type { Browser, BrowserContext, Page } from '@playwright/test';

import {
  DIRECTION_LOCALE,
  FIXED_INSTANT,
  LOCALE_BUTTON_NAME,
  type DirectionName,
  type ThemeName,
} from './matrix.js';

/** The seeded rig identity. Synthetic, per the seeder; never a real account. */
const TENANT_ID = '11111111-1111-1111-1111-111111111111';
const EXTERNAL_SUBJECT = 'cross-layer-owner';

/** localStorage keys the app's store reads at boot (`stores/index.ts`). */
const THEME_KEY = 'dubbing.theme';
const LOCALE_KEY = 'dubbing.locale';

/** A route guaranteed to render the shell, used as a stepping stone. */
const SHELL_ROUTE = '/dashboard';

/** How long the rendered element set must be unchanged before a capture. */
const CONTENT_QUIET_MS = 600;

/** Ceiling on the stability wait, so a permanently-churning screen still fails loudly. */
const CONTENT_STABILITY_TIMEOUT_MS = 20_000;

export class VisualSessionError extends Error {
  readonly detail: string;

  constructor(message: string, detail = '') {
    super(detail.length === 0 ? message : `${message}\n${detail}`);
    this.name = 'VisualSessionError';
    this.detail = detail;
  }
}

export interface VisualAxes {
  readonly theme: string;
  readonly direction: string;
}

export interface VisualSession {
  readonly context: BrowserContext;
  /** Authenticated. Use for the 11 screens that have a shell. */
  readonly page: Page;
  /** Never authenticated. Use for the login screen, which has no shell. */
  readonly anonPage: Page;
  readonly close: () => Promise<void>;
}

export async function openVisualSession(browser: Browser): Promise<VisualSession> {
  const context = await browser.newContext({
    // Fixed pixel ratio, locale and timezone keep text rasterisation and date
    // formatting stable between runs; reducedMotion removes scroll-driven motion.
    reducedMotion: 'reduce',
    deviceScaleFactor: 1,
    locale: 'en-US',
    timezoneId: 'UTC',
  });
  const page = await context.newPage();
  // The clock is pinned before any navigation so anything rendered from the
  // current time is already stable when the first screen paints.
  await page.clock.setFixedTime(new Date(FIXED_INSTANT));
  await signIn(page);

  const anonPage = await context.newPage();
  await anonPage.clock.setFixedTime(new Date(FIXED_INSTANT));

  return {
    context,
    page,
    anonPage,
    close: async () => {
      await context.close();
    },
  };
}

/** Attempts, and the pause before each retry. */
const SIGN_IN_ATTEMPTS = 4;
const SIGN_IN_BACKOFF_MS = [2_000, 15_000, 30_000];

/**
 * Signs in, retrying through the login rate limit.
 *
 * `POST /auth/login` is limited to 5/min per IP. Normally the suite signs in once,
 * but Playwright restarts the worker after a test failure and re-runs `beforeAll`,
 * so one early failure could burn the budget and turn a single flaky cell into a
 * suite reporting "117 did not run". Backing off keeps a transient 429 from being
 * fatal, and is the difference between one red cell and a suite that looks dead.
 */
async function signIn(page: Page): Promise<void> {
  for (let attempt = 1; attempt <= SIGN_IN_ATTEMPTS; attempt += 1) {
    await page.goto('/login', { waitUntil: 'domcontentloaded' });
    await page.getByTestId('page-login').waitFor({ state: 'visible', timeout: 30_000 });

    await page.getByTestId('auth-tenant-id').fill(TENANT_ID);
    await page.getByTestId('auth-external-subject').fill(EXTERNAL_SUBJECT);
    await page.getByTestId('auth-submit').click();

    const signedIn = await page
      .getByTestId('app-shell')
      .waitFor({ state: 'visible', timeout: 30_000 })
      .then(() => true)
      .catch(() => false);
    if (signedIn) {
      return;
    }

    if (attempt < SIGN_IN_ATTEMPTS) {
      await page.waitForTimeout(SIGN_IN_BACKOFF_MS[attempt - 1] ?? 30_000);
    }
  }

  throw new VisualSessionError(
    'The visual session could not sign in against the rig.',
    `landed on ${page.url()} after ${SIGN_IN_ATTEMPTS} attempts. Is the stack up and seeded? ` +
      '`docker compose -f tests/cross-layer/docker-compose.cross.yml up -d`, then let the ' +
      'Playwright globalSetup run.',
  );
}

/** Current axes as the stylesheet sees them. */
export async function readAxes(page: Page): Promise<VisualAxes> {
  return page.evaluate(() => ({
    theme: document.documentElement.getAttribute('data-theme') ?? 'light',
    direction: document.documentElement.getAttribute('dir') ?? 'ltr',
  }));
}

/** A cheap fingerprint of what is currently rendered. */
async function contentFingerprint(page: Page): Promise<string> {
  return page.evaluate(() => {
    const nodes = [...document.querySelectorAll('[data-testid]')];
    const visible = nodes.filter((node) => {
      const style = window.getComputedStyle(node);
      return style.visibility !== 'hidden' && style.display !== 'none';
    });
    return `${visible.length}:${visible
      .map((node) => `${node.getAttribute('data-testid')}=${(node.textContent ?? '').length}`)
      .sort()
      .join('|')}`;
  });
}

/**
 * Waits until the rendered element set stops changing.
 *
 * The workspace mounts its root and then fills panels asynchronously, so a capture
 * taken on mount is a coin flip. Polling a fingerprint of visible testids and their
 * text lengths catches both a panel appearing and its text streaming in.
 *
 * Fails loudly rather than capturing a churning screen: a visual baseline of a
 * page that is still changing is worse than no baseline, because it will fail at a
 * random moment later and be blamed on something else.
 */
export async function waitForContentStable(page: Page): Promise<void> {
  await page.evaluate(() => document.fonts.ready.then(() => undefined));

  const deadline = Date.now() + CONTENT_STABILITY_TIMEOUT_MS;
  let previous = await contentFingerprint(page);
  let stableSince = Date.now();

  for (;;) {
    await page.waitForTimeout(120);
    const current = await contentFingerprint(page);
    if (current === previous) {
      if (Date.now() - stableSince >= CONTENT_QUIET_MS) {
        return;
      }
    } else {
      previous = current;
      stableSince = Date.now();
    }
    if (Date.now() > deadline) {
      throw new VisualSessionError(
        'The screen never stopped changing, so no trustworthy baseline could be taken.',
        `url=${page.url()}. waited ${CONTENT_STABILITY_TIMEOUT_MS}ms for the rendered element ` +
          `set to be stable for ${CONTENT_QUIET_MS}ms. Something on this screen polls or ` +
          'animates continuously; either fix it or give this screen its own quiet window.',
      );
    }
  }
}

/**
 * Navigates within the app without a document load, so the in-memory session
 * survives. React Router resolves `popstate`, and the store is a module
 * singleton, so neither the tokens nor the current theme/locale are disturbed.
 */
export async function navigateInApp(page: Page, path: string, readyTestId: string): Promise<void> {
  await page.evaluate((target) => {
    window.history.pushState({}, '', target);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, path);

  try {
    await page.getByTestId(readyTestId).waitFor({ state: 'visible', timeout: 30_000 });
  } catch (error) {
    const axes = await readAxes(page);
    throw new VisualSessionError(
      `In-app navigation to ${path} did not render '${readyTestId}'.`,
      `url=${page.url()} theme=${axes.theme} dir=${axes.direction}. ` +
        `Original: ${(error as Error).message}`,
    );
  }
  await waitForContentStable(page);
}

/**
 * Puts the shell into `theme` with the shell's own toggle.
 *
 * The toggle is a switch, not a setter, so the current value is read first and the
 * button is only pressed when it differs.
 */
export async function applyThemeViaShell(page: Page, theme: ThemeName): Promise<void> {
  await requireShell(page, 'theme-switcher');
  if ((await readAxes(page)).theme === theme) {
    return;
  }
  await page.getByTestId('theme-switcher').click();
  await page.waitForFunction(
    (expected) => document.documentElement.getAttribute('data-theme') === expected,
    theme,
    { timeout: 10_000 },
  );
  await waitForContentStable(page);
}

/**
 * Puts the shell into `direction` with the shell's own locale switcher.
 *
 * The buttons have no `data-testid`, so they are addressed by their accessible
 * name from `nav.json`. That is also a useful assertion in itself: if a
 * translator changes the string, the switcher becomes unreachable and this fails
 * loudly rather than quietly pinning English-only baselines.
 */
export async function applyDirectionViaShell(page: Page, direction: DirectionName): Promise<void> {
  await requireShell(page, 'locale-switcher');
  if ((await readAxes(page)).direction === direction) {
    return;
  }
  await page
    .getByTestId('locale-switcher')
    .getByRole('button', { name: LOCALE_BUTTON_NAME[direction] })
    .click();
  await page.waitForFunction(
    (expected) => document.documentElement.getAttribute('dir') === expected,
    direction,
    { timeout: 10_000 },
  );

  const locale = await page.evaluate(() => document.documentElement.getAttribute('lang'));
  if (locale !== DIRECTION_LOCALE[direction]) {
    throw new VisualSessionError(
      `Direction ${direction} applied but document lang is '${locale}'.`,
      `Expected '${DIRECTION_LOCALE[direction]}'. The locale switcher and the dir attribute ` +
        'have diverged, so the RTL axis would be pinning the wrong thing.',
    );
  }
  await waitForContentStable(page);
}

async function requireShell(page: Page, controlTestId: string): Promise<void> {
  if ((await page.getByTestId(controlTestId).count()) === 0) {
    throw new VisualSessionError(
      `The shell control '${controlTestId}' is not on the current page (${page.url()}).`,
      'The authenticated page must be inside the app shell. If the session was lost, a ' +
        'full page load happened - use navigateInApp, not page.goto.',
    );
  }
}

/**
 * Sets both axes on a never-authenticated page and lands on `path`.
 *
 * Used only for the login screen, which renders no shell and therefore has no
 * switchers. The store reads `dubbing.theme` and `dubbing.locale` at boot, so
 * localStorage plus a load is the same mechanism the app itself uses to restore a
 * user's preference - no test-only back door.
 */
export async function renderShelllessScreen(
  page: Page,
  path: string,
  theme: ThemeName,
  direction: DirectionName,
  readyTestId: string,
): Promise<VisualAxes> {
  await page.goto('/login', { waitUntil: 'domcontentloaded' });
  await page.evaluate(
    ([themeKey, localeKey, themeValue, localeValue]) => {
      window.localStorage.setItem(themeKey, themeValue);
      window.localStorage.setItem(localeKey, localeValue);
    },
    [THEME_KEY, LOCALE_KEY, theme, DIRECTION_LOCALE[direction]] as const,
  );
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.getByTestId(readyTestId).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForContentStable(page);
  return readAxes(page);
}

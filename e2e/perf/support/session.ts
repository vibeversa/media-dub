// Task 041D: the perf session.
//
// Three session shapes, and the reason for each is a rig constraint rather than
// a preference:
//
//   1. **The shared signed-in session.** Tokens live in memory only, so a
//      document load is a logout and every budget that needs the shell has to
//      sign in. `POST /auth/login` is rate limited to 5/min per IP, and this
//      suite has six signed-in specs, so signing in per file would spend half
//      the run waiting. The session is therefore cached per `Browser` - one
//      sign-in per worker process - using 041B's own helper, retry and all, so
//      there is exactly one sign-in implementation in the repository.
//
//   2. **A cold context**, for `app-interactive`. A cold load is a cold load:
//      a new context, the HTTP cache disabled, and a desktop-broadband network
//      profile applied over CDP. It deliberately does NOT reuse the signed-in
//      context, because a document load in that context is a logout (see
//      `budgets.ts`).
//
//   3. **Per-spec pages.** Every spec takes a fresh page off the shared context
//      and closes it. Routes, clocks and query caches are per page, so a page
//      per spec is what stops one budget's fixture from being another's
//      environment.
//
// The clock is pinned to the seeder's instant, exactly as 041B does, and for
// the same reason: a `RelativeTime` cell that moves between runs is a
// measurement that cannot be compared with the previous one. `performance.now()`
// is untouched by the pin, so no budget is affected.

import { existsSync, readFileSync } from 'node:fs';
import { cpus, totalmem, release, type as osType } from 'node:os';
import { join } from 'node:path';
import type { Browser, BrowserContext, CDPSession, Page } from '@playwright/test';

import { DESKTOP_BROADBAND, type BandwidthProfile } from '../budgets.js';
import {
  openVisualSession,
  waitForContentStable,
  type VisualSession,
} from '../../visual/support/session.js';
import { PROJECT_ID } from '../../visual/support/matrix.js';

export { PROJECT_ID };

export interface PerfSession {
  readonly visual: VisualSession;
  /**
   * The one authenticated page for this worker.
   *
   * Deliberately shared, not "a fresh page per spec". The access and refresh
   * tokens live in the *page's* JavaScript heap (`features/auth/authStore.ts`
   * keeps them in a module-level zustand store and never persists them), so a
   * new page is an anonymous page and a fresh sign-in - and
   * `POST /auth/login` is limited to 5/min per IP. Six specs x one sign-in each
   * is a suite that spends its time waiting for 429s.
   *
   * The sharing is safe because each spec installs its own synthetic routes and
   * removes them in a `finally`, and each spec that needs project-scoped data
   * uses its own synthetic project id, so no react-query key is shared between
   * two fixtures of different sizes.
   */
  readonly page: Page;
  close: () => Promise<void>;
}

const sessions = new WeakMap<Browser, Promise<PerfSession>>();

/**
 * The one signed-in session for this worker.
 *
 * Keyed on the `Browser` instance, which is per worker: a worker restart after
 * a failure gets a new browser and therefore a new sign-in, which is correct -
 * the in-memory tokens died with the old context.
 */
export function getPerfSession(browser: Browser): Promise<PerfSession> {
  const existing = sessions.get(browser);
  if (existing !== undefined) {
    return existing;
  }
  const created = openVisualSession(browser).then(
    (visual): PerfSession => ({
      visual,
      page: visual.page,
      close: async () => {
        await visual.close();
      },
    }),
  );
  sessions.set(browser, created);
  return created;
}

/** One viewport for every budget, set explicitly so a budget cannot drift. */
export const PERF_VIEWPORT = { width: 1440, height: 900 } as const;

/**
 * Resolves a repository-relative path from the run's working directory.
 *
 * `import.meta.url` is deliberately not used: Playwright transpiles these
 * modules and may relocate them, and `process.cwd()` is what Playwright
 * actually sets. The walk looks for `playwright.config.ts` so it cannot latch
 * onto a parent checkout.
 */
export function repoFile(relative: string): string {
  let current = process.cwd();
  for (let depth = 0; depth < 10; depth += 1) {
    if (existsSync(join(current, 'playwright.config.ts'))) {
      return join(current, relative);
    }
    const parent = join(current, '..');
    if (parent === current) {
      break;
    }
    current = parent;
  }
  throw new Error(`Could not locate the repository root from ${process.cwd()} to read ${relative}.`);
}

export function readRepoFile(relative: string): Buffer {
  return readFileSync(repoFile(relative));
}

export interface ColdContext {
  readonly context: BrowserContext;
  readonly page: Page;
  readonly close: () => Promise<void>;
}

/**
 * A context that has never seen the application before.
 *
 * `Network.setCacheDisabled` is what makes the second and third runs of
 * `app-interactive` mean the same as the first; without it, run two measures a
 * warm HTTP cache and the median of three is a median of a cold load and two
 * cache hits, which is not a measurement of anything.
 */
export async function openColdContext(
  browser: Browser,
  profile: BandwidthProfile = DESKTOP_BROADBAND,
): Promise<ColdContext> {
  const context = await browser.newContext({
    deviceScaleFactor: 1,
    locale: 'en-US',
    timezoneId: 'UTC',
    viewport: { ...PERF_VIEWPORT },
  });
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
  await applyNetworkProfile(cdp, profile);
  return {
    context,
    page,
    close: async () => {
      await cdp.detach().catch(() => undefined);
      await context.close();
    },
  };
}

/** Applies a bandwidth profile to a page through CDP. */
export async function applyNetworkProfile(cdp: CDPSession, profile: BandwidthProfile): Promise<void> {
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false,
    latency: profile.latencyMs,
    downloadThroughput: profile.downloadBytesPerSecond,
    uploadThroughput: profile.uploadBytesPerSecond,
  });
}

/**
 * The host the numbers came from.
 *
 * R3 forbids idealized-hardware numbers, and a timing budget without the
 * machine it was taken on is exactly that. CPU is deliberately NOT throttled (a
 * desktop profile is a 1x CPU profile), so the host is the only defence, and it
 * is recorded with every sample and into every perf issue.
 */
export function hostProfile(): string {
  const list = cpus();
  const model = list[0]?.model.trim() ?? 'unknown cpu';
  return [
    `os: ${osType()} ${release()}`,
    `cpu: ${model}`,
    `cores: ${String(list.length)}`,
    `memory: ${String(Math.round(totalmem() / (1024 * 1024 * 1024)))} GiB`,
    `node: ${process.version}`,
    'cpu throttling: none (desktop profile is 1x); network throttling: see the budget',
  ].join('\n');
}

/** Host profile as a single line, for sample detail cells. */
export function hostProfileOneLine(): string {
  const list = cpus();
  return `${osType()} ${list.length} cores, ${list[0]?.model.trim() ?? 'unknown cpu'}`;
}

/**
 * In-app navigation, then settle.
 *
 * Same mechanism 041B uses, for the same reason: a `page.goto` is a logout, and
 * a capture taken before the panels stop changing is a coin flip. Settling is
 * part of every "open" measurement because the budget is "the workspace is
 * open", not "the workspace's root element mounted".
 */
export async function navigateInAppAndSettle(page: Page, path: string, readyTestId: string): Promise<void> {
  await page.evaluate((target) => {
    window.history.pushState({}, '', target);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, path);
  await page.getByTestId(readyTestId).waitFor({ state: 'visible', timeout: 30_000 });
  await waitForContentStable(page);
}

/**
 * Opens a project tab and waits for it to *stop changing*.
 *
 * The structural assertions use this rather than the timing probe's
 * `measureOpen`, because `measureOpen` returns as soon as the ready testid is
 * present - and the previous test's screen is already present when the route
 * changes, so that is a race, not a wait. The perf session shares one page
 * across specs (the tokens live in its heap), so "the previous test's screen is
 * still up" is the normal case here, not an edge case.
 */
export async function openProjectTab(page: Page, projectId: string, tab: string, readyTestId: string): Promise<void> {
  await navigateInAppAndSettle(page, `/projects/${projectId}/${tab}`, readyTestId);
}

/**
 * Opens the transcript tab and asserts the editor rendered rather than its error
 * state, because every transcript assertion below depends on the editor and an
 * error state fails them with a message about a missing search box instead of
 * the real cause.
 */
export async function openTranscriptTab(page: Page, projectId: string): Promise<void> {
  await openProjectTab(page, projectId, 'transcript', 'page-project-transcript');
  const errored = await page.getByTestId('transcript-error').count();
  if (errored > 0) {
    const message = await page.getByTestId('transcript-error').innerText();
    throw new Error(
      `The transcript tab for ${projectId} rendered its error state instead of the editor: ` +
        `${message.replace(/\s+/g, ' ').trim().slice(0, 200)}. The synthetic segment endpoint did not answer.`,
    );
  }
  await page.getByTestId('transcript-list').waitFor({ state: 'visible', timeout: 30_000 });
}

/** Opens the timeline tab and waits for the player and waveform to be on the page. */
export async function openTimelineTab(page: Page, projectId: string): Promise<void> {
  await openProjectTab(page, projectId, 'timeline', 'page-project-timeline');
  await page.getByTestId('timeline-workspace').waitFor({ state: 'visible', timeout: 30_000 });
}

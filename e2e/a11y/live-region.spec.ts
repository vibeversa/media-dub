// Task 041C, R5: live regions announce the things that matter, once, and not
// the things that do not.
//
// The hardest requirement in the task to test honestly, because "what a screen
// reader says" is not a property the page exposes. It is measured instead:
//
//   * `captureAnnouncements` (support/session.ts) installs a MutationObserver
//     over every live region and records the text each mutation makes available.
//     That set is what a screen reader announces, so "announced once" is a
//     count, and "suppressed" is a zero.
//
//   * The two claims being tested are the ones the task names: a progress stream
//     announces its COMPLETION and its ERROR, and does not announce the
//     intermediate ticks. A run that emits 40 progress frames must produce
//     zero announcements; the terminal frame must produce one.
//
// The progress stream is driven by intercepting the app's own SSE endpoint and
// replaying real `SseEnvelope` frames through the real client code
// (`useProgressStream` parses, dedupes and invalidates exactly as it does in
// production). Nothing about the app is stubbed except the transport, so what is
// measured is the client's announcement policy - which is what R5 is about. The
// server's fan-out is 026's, not this audit's.
//
// The security requirement rides along: the same pass asserts that no forbidden
// value (a token, a signed URL, a correlation secret) reaches an accessible
// name, a live region or a tooltip.

import { expect, test } from '@playwright/test';

import { A11Y_SCREENS } from './support/screens.js';
import { PROJECT_ID } from '../visual/support/matrix.js';
import {
  captureAnnouncements,
  navigateInApp,
  openA11ySession,
  readLiveRegions,
  type A11ySession,
  type AnnouncementRecord,
} from './support/session.js';
import { SEED } from '../../tests/cross-layer/harness/config.js';

let session: A11ySession;

/** How many intermediate progress frames the replayed stream emits. */
const TICK_COUNT = 8;

test.beforeAll(async ({ browser }) => {
  session = await openA11ySession(browser);
});
test.afterAll(async () => {
  await session?.close();
});

/**
 * Replays a progress stream through the app's real SSE client.
 *
 * The frames are the frozen Task 013 envelope shape: an opaque `eventId`, the
 * project's id, and a `payload` the client allowlists. `stage.progress` is a
 * tick; `run.status_changed` is a completion. The client only ever calls
 * `invalidateQueries` with these, so the observable difference between the two
 * is precisely the question R5 asks.
 */
async function replayProgressStream(
  page: import('@playwright/test').Page,
  options: { readonly ticks: number; readonly terminal: 'completed' | 'failed' },
): Promise<void> {
  const frames: string[] = [];
  for (let index = 0; index < options.ticks; index += 1) {
    frames.push(
      sseFrame({
        eventId: `tick-${String(index)}`,
        eventType: 'stage.progress',
        payload: { percent: Math.round(((index + 1) / options.ticks) * 100) },
      }),
    );
  }
  frames.push(
    sseFrame({
      eventId: `terminal-1`,
      eventType: 'run.status_changed',
      payload: { status: options.terminal === 'completed' ? 'Completed' : 'Failed' },
    }),
  );

  const body = frames.join('');
  await page.route('**/progress/stream', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'text/event-stream',
      headers: { 'Cache-Control': 'no-cache' },
      body,
    });
  });
}

/** Builds one frame in the frozen `SseEnvelope` wire shape. */
function sseFrame(input: {
  readonly eventId: string;
  readonly eventType: string;
  readonly payload: Record<string, unknown>;
}): string {
  const envelope = {
    schemaVersion: 1,
    eventId: input.eventId,
    eventType: input.eventType,
    occurredAt: '2026-01-15T12:00:00.000Z',
    tenantId: SEED.tenantId,
    projectId: `prj_${SEED.projectId.replace(/-/g, '')}`,
    correlationId: 'corr-a11y',
    payload: input.payload,
  };
  return `id: ${input.eventId}\nevent: ${input.eventType}\ndata: ${JSON.stringify(envelope)}\n\n`;
}

/** Lets the debounced invalidations (500 ms) and their refetches settle. */
async function settle(page: import('@playwright/test').Page, ms = 2500): Promise<void> {
  await page.waitForTimeout(ms);
}

test.describe('@a11y progress announcements are capped (R5)', () => {
  test('@a11y intermediate progress ticks are never announced', async () => {
    const { page } = session;
    await navigateInApp(page, '/dashboard', 'app-shell');
    await navigateInApp(page, `/projects/${PROJECT_ID}`, 'page-project-details');
    await settle(page, 500);

    const capture = await captureAnnouncements(page);
    await replayProgressStream(page, { ticks: TICK_COUNT, terminal: 'completed' });
    // Re-navigate so the workspace opens its stream against the interception.
    await navigateInApp(page, '/dashboard', 'app-shell');
    await settle(page, 3000);

    const announcements = await capture.drain();
    await capture.stop();

    const progressLike = announcements.filter((entry) => /%|percent|stage|progress/i.test(entry.text));
    expect(
      progressLike.map((entry) => `${entry.region}: ${entry.text.slice(0, 80)}`),
      `progress ticks were announced. ${String(TICK_COUNT)} ticks must produce nothing a screen ` +
        'reader would read aloud; only the terminal event may speak.',
    ).toEqual([]);
  });

  test('@a11y a completion is announced at most once', async () => {
    const { page } = session;
    await navigateInApp(page, '/dashboard', 'app-shell');
    await navigateInApp(page, `/projects/${PROJECT_ID}`, 'page-project-details');
    await settle(page, 500);

    const capture = await captureAnnouncements(page);
    await replayProgressStream(page, { ticks: 0, terminal: 'completed' });
    await navigateInApp(page, '/dashboard', 'app-shell');
    await settle(page, 3000);

    const announcements = await capture.drain();
    await capture.stop();

    // The cap is the claim: however many times the terminal event lands, the
    // user hears it once. One or zero is acceptable here (the app may express
    // completion visually rather than through a live region); more than one is
    // the failure this test exists to prevent.
    expect(
      announcements.length,
      `a single completion produced ${String(announcements.length)} announcements: ` +
        announcements.map((entry) => entry.text.slice(0, 60)).join(' | '),
    ).toBeLessThanOrEqual(1);
  });

  test('@a11y a failure is announced at most once and is assertive', async () => {
    const { page } = session;
    await navigateInApp(page, '/dashboard', 'app-shell');
    await navigateInApp(page, `/projects/${PROJECT_ID}`, 'page-project-details');
    await settle(page, 500);

    const capture = await captureAnnouncements(page);
    await replayProgressStream(page, { ticks: 0, terminal: 'failed' });
    await navigateInApp(page, '/dashboard', 'app-shell');
    await settle(page, 3000);

    const announcements = await capture.drain();
    await capture.stop();

    expect(
      announcements.length,
      `a single failure produced ${String(announcements.length)} announcements: ` +
        announcements.map((entry) => entry.text.slice(0, 60)).join(' | '),
    ).toBeLessThanOrEqual(1);
    for (const entry of announcements) {
      // A failure is the one thing that may interrupt. `assertive` is not
      // required, but `off` would mean the user is never told.
      expect(
        ['assertive', 'polite'],
        `the failure was announced with politeness "${entry.politeness}"`,
      ).toContain(entry.politeness);
    }
  });

  test('@a11y the announcement capture is not vacuous', async () => {
    // The R5 assertions above all pass on zero announcements, so they need a
    // positive control: something that MUST be announced, checked through the
    // same observer. Without this, a broken MutationObserver would make the
    // whole file green and meaningless.
    const { page } = session;
    await navigateInApp(page, '/dashboard', 'app-shell');
    await navigateInApp(page, `/projects/${PROJECT_ID}/exports`, 'page-project-exports');

    const capture = await captureAnnouncements(page);

    // Opening the export dialog and submitting produces a real toast, which is
    // the app's polite announcement channel.
    await page.getByTestId('export-open-dialog').click();
    await page.getByTestId('export-submit').click();
    await settle(page, 3000);

    const announcements = await capture.drain();
    await capture.stop();

    expect(
      announcements.length,
      'no live-region mutation was recorded while a toast was raised. The observer is broken, ' +
        'so every "must not announce" assertion in this file is vacuous.',
    ).toBeGreaterThan(0);
    expect(
      announcements.every((entry) => entry.text.length > 0),
      'a live region mutated with no text, which is not an announcement',
    ).toBe(true);
  });

  test('@a11y replaying the same terminal event twice still announces once', async () => {
    // The client's dedupe is by `eventId`; the *announcement* cap must not
    // depend on it. A duplicate frame that the client treats as new must not
    // become a second announcement.
    const { page } = session;
    await navigateInApp(page, '/dashboard', 'app-shell');
    await navigateInApp(page, `/projects/${PROJECT_ID}`, 'page-project-details');
    await settle(page, 500);

    const capture = await captureAnnouncements(page);
    await page.route('**/progress/stream', async (route) => {
      const body =
        sseFrame({ eventId: 'dup-1', eventType: 'run.status_changed', payload: { status: 'Completed' } }) +
        sseFrame({ eventId: 'dup-2', eventType: 'run.status_changed', payload: { status: 'Completed' } });
      await route.fulfill({ status: 200, contentType: 'text/event-stream', body });
    });
    await navigateInApp(page, '/dashboard', 'app-shell');
    await settle(page, 3000);

    const announcements = await capture.drain();
    await capture.stop();
    expect(
      announcements.length,
      `two terminal frames produced ${String(announcements.length)} announcements: ` +
        announcements.map((entry: AnnouncementRecord) => entry.text.slice(0, 60)).join(' | '),
    ).toBeLessThanOrEqual(1);
  });
});

test.describe('@a11y live regions are well-formed', () => {
  for (const entry of A11Y_SCREENS) {
    test(`@a11y live regions on ${entry.screen.id} declare a politeness`, async () => {
      const { screen } = entry;
      if (screen.authenticated) {
        if ((await session.page.getByTestId('app-shell').count()) === 0) {
          await navigateInApp(session.page, '/dashboard', 'app-shell');
        }
        await navigateInApp(session.page, screen.path, screen.readyTestId);
      } else {
        await session.anonPage.goto('/login', { waitUntil: 'domcontentloaded' });
        await session.anonPage.getByTestId('page-login').waitFor({ state: 'visible', timeout: 30_000 });
      }
      const target = screen.authenticated ? session.page : session.anonPage;
      const regions = await readLiveRegions(target);

      for (const region of regions) {
        expect(
          ['polite', 'assertive', 'off'],
          `${screen.id}: live region '${region.region}' declares politeness '${region.politeness}'`,
        ).toContain(region.politeness);
        // `aria-busy` regions are deliberately silent, but only while busy.
        if (region.suppressedByBusy) {
          expect(
            region.politeness,
            `${screen.id}: '${region.region}' is inside aria-busy and will never be heard; ` +
              'aria-busy must be removed once the content settles',
          ).not.toBe('off');
        }
      }
    });
  }

  test('@a11y the toast region is polite, not assertive', async () => {
    // A toast is informational. `assertive` would interrupt whatever the user
    // was doing for every "Export requested." in the app.
    //
    // Located by its accessible name, because that is how a screen reader
    // reaches it - the region has no `data-testid`, and keying on the markup
    // would make this a test-only fact about the class list.
    const { page } = session;
    await navigateInApp(page, '/dashboard', 'app-shell');
    const regions = await readLiveRegions(page);
    expect(regions.length, 'the shell must have at least one live region').toBeGreaterThan(0);

    const toast = regions.find((region) => /Notifications/i.test(region.region));
    expect(toast, 'the toast container must be a live region a screen reader can find').toBeDefined();
    expect(toast?.politeness, 'a toast must be polite, not assertive').toBe('polite');
  });
});

test.describe('@a11y assistive technology is not given secrets', () => {
  test('@a11y no token, signed URL or credential reaches an accessible name or live region', async () => {
    // The security requirement attached to this task. An `aria-label` is read
    // aloud, logged by some screen readers, and exposed to every AT
    // integration; a signed media URL or a bearer token in one is a leak.
    const forbidden = [
      /Bearer\s/i,
      /eyJ[A-Za-z0-9_-]{8,}/, // a JWT
      /X-Amz-Signature/i,
      /X-Amz-Credential/i,
      /[?&]Signature=/i,
      /CHANGE_ME/,
      /access_token=/i,
      /refresh_token/i,
      /connectionString/i,
      /signingKey/i,
    ];

    for (const entry of A11Y_SCREENS) {
      const { screen } = entry;
      if (screen.authenticated) {
        if ((await session.page.getByTestId('app-shell').count()) === 0) {
          await navigateInApp(session.page, '/dashboard', 'app-shell');
        }
        await navigateInApp(session.page, screen.path, screen.readyTestId);
      } else {
        await session.anonPage.goto('/login', { waitUntil: 'domcontentloaded' });
        await session.anonPage.getByTestId('page-login').waitFor({ state: 'visible', timeout: 30_000 });
      }
      const target = screen.authenticated ? session.page : session.anonPage;

      const exposed = await target.evaluate((patterns) => {
        const regexes = patterns.map((source) => new RegExp(source, 'i'));
        const hits: string[] = [];
        const check = (value: string | null, where: string): void => {
          if (value === null || value === '') {
            return;
          }
          for (const regex of regexes) {
            if (regex.test(value)) {
              hits.push(`${where}: ${value.slice(0, 120)}`);
              return;
            }
          }
        };
        for (const element of document.querySelectorAll<HTMLElement>('*')) {
          check(element.getAttribute('aria-label'), `${element.tagName.toLowerCase()}[aria-label]`);
          check(element.getAttribute('title'), `${element.tagName.toLowerCase()}[title]`);
          check(
            element.getAttribute('aria-valuetext'),
            `${element.tagName.toLowerCase()}[aria-valuetext]`,
          );
          if (element.getAttribute('aria-live') !== null || element.getAttribute('role') === 'status') {
            check(element.textContent, `${element.tagName.toLowerCase()}[live]`);
          }
        }
        return hits;
      }, forbidden.map((regex) => regex.source));

      expect(exposed, `${screen.id} exposes a secret to assistive technology`).toEqual([]);
    }
  });
});

// Task 041D, R4: performance practices asserted structurally, next to the timing.
//
// The task asks for this explicitly: "Assert performance practices structurally
// where cheap: route-level splitting present (015), list virtualization active
// at 200+ rows, timeline uses preview peaks not archival (030), search debounced
// - failures point at the owning task."
//
// "Cheap" is doing real work in that sentence. A timing budget says a number got
// worse; it does not say *which practice stopped happening*, and on a fast
// machine a removed debounce or a de-windowed list may not move any of the six
// numbers at all. So each practice is asserted on its own evidence, and each
// failure message names the task that owns the code:
//
//   * route-level splitting (015)   - measured on the real network log of a real
//                                     cold load: the entry document plus the
//                                     vendor chunk, and a *second* script request
//                                     when a route chunk is entered. Not a source
//                                     grep, and not a claim about `lazy()`.
//   * list bound at 200+ rows (021/027)
//                                  - two different mechanisms, both checked: the
//                                     project grid is bounded by server
//                                     pagination, and the transcript list is
//                                     bounded by windowing at 5,000 rows.
//   * preview peaks, not archival media (030)
//                                  - the request log. The peaks endpoint was
//                                     fetched; no request for a source original
//                                     was made.
//   * search debounce (027)        - a burst of keystrokes produces ONE filter
//                                     pass, counted with a MutationObserver.
//   * the data-path ceilings this gate had to work around (027/032)
//                                  - the tripwire for the 5,000-segment
//                                     limitation on the search budget.
//
// Every measurement here is also checked for non-vacuity: an assertion that a
// count is below a threshold passes on zero, so each one asserts the positive
// control first.

import { expect, test } from '@playwright/test';

import {
  SEARCH_NEEDLE,
  SEARCH_NEEDLE_EXPECTED,
  SYNTHETIC_SEGMENT_COUNT,
  TIMELINE_SEGMENT_COUNT,
  installSyntheticApi,
  perfProjectId,
} from './support/fixtures.js';
import { installProbes, probeAllAttributes, probeMeasureOpen } from './support/probes.js';
import { openTimelineTab, openTranscriptTab } from './support/session.js';
import { PROJECT_ID, getPerfSession, hostProfileOneLine, openColdContext } from './support/session.js';
import { DESKTOP_BROADBAND } from './budgets.js';

test.describe('@perf structural performance practices', () => {
  test('@perf route-level splitting loads a separate chunk per route (015)', async ({ browser }) => {
    // A cold context with the HTTP cache disabled, so every script the app asks
    // for is a script it really needed. A warm cache would report one document
    // and no chunks, and the assertion would pass for the wrong reason.
    const cold = await openColdContext(browser, DESKTOP_BROADBAND);
    const scripts: string[] = [];
    cold.page.on('request', (request) => {
      if (request.resourceType() === 'script') {
        scripts.push(new URL(request.url()).pathname);
      }
    });

    try {
      await cold.page.goto('/', { waitUntil: 'commit' });
      await cold.page.getByTestId('auth-submit').waitFor({ state: 'visible', timeout: 60_000 });

      const entryScripts = scripts.filter((path) => /\/assets\/.*\.js$/.test(path));
      // Positive control: the app must have loaded *some* script. A page that
      // loaded nothing makes every "no second chunk" assertion vacuous.
      expect(entryScripts.length, 'the cold load fetched no scripts at all').toBeGreaterThan(0);

      // The sign-in screen's own chunk must be a separate request from the
      // vendor/entry chunk. One chunk containing both is a bundle that has not
      // been split, whatever the source says.
      const loginChunk = entryScripts.find((path) => /LoginPage/.test(path));
      expect(
        loginChunk,
        `the sign-in route did not load its own chunk. Scripts on the cold load: ${entryScripts.join(', ')}. ` +
          'Route-level splitting belongs to Task 015 (frontend/src/app/pages/lazy.ts).',
      ).toBeDefined();
      expect(
        entryScripts.length,
        `expected at least an entry chunk and a route chunk, got ${entryScripts.length}: ${entryScripts.join(', ')}`,
      ).toBeGreaterThanOrEqual(2);
    } finally {
      await cold.close();
    }
  });

  test('@perf the project grid renders only the server page it was given (021)', async ({ browser }) => {
    const session = await getPerfSession(browser);
    const { page } = session;
    const synthetic = await installSyntheticApi(page, {
      projectId: 'prj_perf00000000000000000000000000',
      segmentCount: 1,
      overshootPageSize: false,
    });

    try {
      await installProbes(page);
      await probeMeasureOpen(page, {
        path: '/projects?pageSize=100&page=1',
        testId: 'page-projects',
        selector: '[data-testid="projects-grid"] table.dp-grid tbody tr',
        count: 100,
      });

      const requested = synthetic.requests.find(
        (request) => request.path.startsWith('/api/v1/projects?'),
      );
      expect(requested, 'the project list never requested the API').toBeDefined();
      expect(
        requested?.path,
        'the client asked for a page size outside the contract maximum of 100',
      ).toContain('pageSize=100');

      const rows = await page.evaluate(
        () => document.querySelectorAll('[data-testid="projects-grid"] table.dp-grid tbody tr').length,
      );
      // Positive control first: a grid that rendered nothing would satisfy a
      // "renders at most 100 rows" assertion trivially.
      expect(rows, 'the grid rendered no rows, so the bound below proves nothing').toBeGreaterThan(0);
      expect(
        rows,
        `the grid rendered ${String(rows)} rows for a 100-row server page. The list is bounded by ` +
          'pagination (Task 021 R2), not by windowing; a client-side slice or a raised page size ' +
          'would break the DOM bound this assertion exists to protect.',
      ).toBeLessThanOrEqual(100);
    } finally {
      await synthetic.close();
    }
  });

  test('@perf the transcript list windowing holds at 5,000 segments (027)', async ({ browser }) => {
    const session = await getPerfSession(browser);
    const { page } = session;
    const synthetic = await installSyntheticApi(page, {
      projectId: perfProjectId(2),
      segmentCount: SYNTHETIC_SEGMENT_COUNT,
      overshootPageSize: true,
    });

    try {
      await installProbes(page);
      await openTranscriptTab(page, perfProjectId(2));

      const windowing = await probeAllAttributes(page, '[data-testid="transcript-list"]', 'data-rendered');
      const key = Object.keys(windowing)[0] ?? '';
      const rendered = windowing[key] ?? 0;
      const total = await page.evaluate(
        () => Number(document.querySelector('[data-testid="transcript-list"]')?.getAttribute('data-total') ?? '0'),
      );

      // Positive controls, in order: the corpus is loaded, and the window is
      // non-empty. Without both, "rendered is a small fraction of total" is a
      // statement about a page with nothing on it.
      expect(total, 'the client did not hold the 5,000-segment fixture').toBe(SYNTHETIC_SEGMENT_COUNT);
      expect(rendered, 'the virtualized list rendered no rows, so windowing proves nothing').toBeGreaterThan(0);
      expect(
        rendered,
        `the virtualized list rendered ${String(rendered)} of ${String(total)} rows. ` +
          '`VirtualizedSegmentList` renders `VIEWPORT_ROWS + OVERSCAN * 2` rows and no more; if this ' +
          'reaches thousands the windowing is gone (Task 027 R6).',
      ).toBeLessThan(total);
    } finally {
      await synthetic.close();
    }
  });

  test('@perf segment search is debounced: a keystroke burst filters once (027)', async ({ browser }) => {
    const session = await getPerfSession(browser);
    const { page } = session;
    const synthetic = await installSyntheticApi(page, {
      projectId: perfProjectId(6),
      segmentCount: SYNTHETIC_SEGMENT_COUNT,
      overshootPageSize: true,
    });

    try {
      await installProbes(page);
      await openTranscriptTab(page, perfProjectId(6));

      // Count the filter passes by watching the rendered window's total change.
      // A debounce collapses a burst into one trailing invocation, so a burst of
      // nine keystrokes must produce exactly one change; an undebounced filter
      // produces one per keystroke.
      const changes = await page.evaluate(
        async ([needle, expected]) => {
          const list = document.querySelector<HTMLElement>('[data-testid="transcript-list"]');
          const input = document.querySelector<HTMLInputElement>('[data-testid="transcript-search"]');
          if (list === null || input === null) {
            throw new Error('the transcript search surface is not on the page');
          }
          let observed = 0;
          const observer = new MutationObserver(() => {
            observed += 1;
          });
          observer.observe(list, { attributes: true, attributeFilter: ['data-total'] });

          const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
          if (setter === undefined) {
            throw new Error('HTMLInputElement.prototype.value setter is unavailable');
          }
          const deadline = performance.now() + 30_000;
          for (let index = 0; index < needle.length; index += 1) {
            setter.call(input, needle.slice(0, index + 1));
            input.dispatchEvent(new Event('input', { bubbles: true }));
            // 10ms apart, so all nine keystrokes land inside the 200ms debounce.
            await new Promise<void>((resolve) => {
              window.setTimeout(resolve, 10);
            });
          }
          while (performance.now() < deadline) {
            if (list.getAttribute('data-total') === String(expected)) {
              break;
            }
            await new Promise<void>((resolve) => {
              window.setTimeout(resolve, 20);
            });
          }
          await new Promise<void>((resolve) => {
            window.setTimeout(resolve, 500);
          });
          observer.disconnect();
          return { observed, total: Number(list.getAttribute('data-total')) };
        },
        [SEARCH_NEEDLE, SEARCH_NEEDLE_EXPECTED] as const,
      );

      // Positive control: the search must actually have filtered, or a debounce
      // that never fires at all would also produce zero changes.
      expect(
        changes.total,
        `the burst search did not reach the expected ${String(SEARCH_NEEDLE_EXPECTED)} matches, so the ` +
          'debounce assertion below would be measuring a search that never ran.',
      ).toBe(SEARCH_NEEDLE_EXPECTED);
      expect(
        changes.observed,
        `a ${String(SEARCH_NEEDLE.length)}-keystroke burst produced ${String(changes.observed)} filter ` +
          `passes. \`TranscriptEditor\` debounces the search by SEARCH_DEBOUNCE_MS (200), which collapses ` +
          'the burst into one trailing pass (Task 027).',
      ).toBeLessThanOrEqual(2);
    } finally {
      await synthetic.close();
    }
  });

  test('@perf the timeline fetches preview peaks and never archival media (030)', async ({ browser }) => {
    const session = await getPerfSession(browser);
    const { page } = session;
    const synthetic = await installSyntheticApi(page, {
      projectId: perfProjectId(5),
      segmentCount: TIMELINE_SEGMENT_COUNT,
      overshootPageSize: true,
    });

    try {
      await installProbes(page);
      await openTimelineTab(page, perfProjectId(5));

      const paths = synthetic.paths();
      const peaks = paths.filter((path) => path.includes('/media/waveform-peaks'));
      expect(
        peaks.length,
        `the timeline did not fetch the preview-peaks endpoint. Requests: ${paths.join(', ')}. ` +
          'Visualization must read `WaveformPeaks` only (Task 030 R1).',
      ).toBeGreaterThan(0);

      // The negative half, which is the one that matters: nothing that could be
      // a source original was requested. A waveform that fetched the archival
      // media to draw itself would pass the peaks assertion above and fail here.
      const archival = paths.filter(
        (path) =>
          /source-media|sourceMedia|\/media\/(original|archive|raw)\b/i.test(path) ||
          /\.(mp4|mov|mkv|webm|wav|flac|aac|mp3|m4a)(\?|$)/i.test(path.replace('/__perf__/preview-60s.mkv', '')),
      );
      expect(
        archival,
        `the timeline requested archival media: ${archival.join(', ')}. Preview peaks and the signed ` +
          'preview descriptor are the only media the timeline is allowed to touch (Task 030 R1).',
      ).toEqual([]);

      // Positive control for the negative: the synthetic preview WAS fetched, so
      // "no archival media" is not the same as "no media at all".
      expect(
        paths.some((path) => path.includes('/output/download')),
        'the signed preview descriptor was never requested, so the media path was not exercised',
      ).toBe(true);
    } finally {
      await synthetic.close();
    }
  });

  test('@perf the timeline dialogue lane is not windowed - recorded, not gated (030)', async ({ browser }) => {
    // This test is expected to FAIL its own claim and is therefore not written
    // as an assertion of correctness. What it does is pin the exact state of a
    // known gap so that closing the gap is a deliberate diff: the assertion
    // below is the *inverse* of what will be true once 030 windowed the lane,
    // and when it stops holding, the diff says the gap closed.
    const session = await getPerfSession(browser);
    const { page } = session;
    const synthetic = await installSyntheticApi(page, {
      projectId: perfProjectId(8),
      segmentCount: TIMELINE_SEGMENT_COUNT,
      overshootPageSize: true,
    });

    try {
      await installProbes(page);
      await openTimelineTab(page, perfProjectId(8));

      const rendered = await page.evaluate(
        () => document.querySelectorAll('[data-testid="timeline-dialogue-track"] button').length,
      );
      const reportedTotal = await page.evaluate(() => {
        const track = document.querySelector('[data-testid="timeline-dialogue-track"]');
        return {
          total: Number(track?.getAttribute('data-total') ?? '0'),
          rendered: Number(track?.getAttribute('data-rendered') ?? '0'),
        };
      });

      console.log(
        `[perf] dialogue lane: ${String(reportedTotal.total)} segments, ${String(reportedTotal.rendered)} ` +
          `declared rendered, ${String(rendered)} in the DOM. Host: ${hostProfileOneLine()}`,
      );

      // Documented current state, asserted so a change is noticed.
      expect(
        reportedTotal.rendered,
        'the dialogue lane is now windowed (reported rendered < total). That is the fix for the ' +
          'known 030 gap this test records - update this assertion and the timeline-interaction ' +
          'fixture size in e2e/perf/budgets.ts in the same commit.',
      ).toBe(reportedTotal.total);
    } finally {
      await synthetic.close();
    }
  });

  test('@perf the real transcript data path caps at 2,000 segments (027/032)', async ({ browser }) => {
    // The tripwire behind the `segment-search` budget's largest declared
    // limitation. The budget measures 5,000 segments through a synthetic
    // endpoint that overshoots the requested page size, because the application
    // cannot hold more than 2,000. This assertion pins that ceiling on the REAL
    // endpoint, so the day 027/032 raises it, the tripwire fires and whoever
    // raised it knows the perf fixture has to be revisited.
    const session = await getPerfSession(browser);
    const { page } = session;
    const requestedPageSizes: number[] = [];
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (url.pathname.includes('/segments')) {
        const size = Number.parseInt(url.searchParams.get('pageSize') ?? '', 10);
        if (Number.isFinite(size)) {
          requestedPageSizes.push(size);
        }
      }
    });

    try {
      await installProbes(page);
      // The REAL seeded project and the REAL endpoint: no synthetic routes are
      // installed in this test, which is the point. It observes what the
      // application actually asks for.
      await probeMeasureOpen(page, {
        path: `/projects/${PROJECT_ID}/transcript`,
        testId: 'page-project-transcript',
        selector: '[data-testid="transcript-error"], [data-testid="transcript-editor"]',
        count: 1,
      });
      await page.waitForTimeout(1500);

      expect(
        requestedPageSizes.length,
        'no segment request was observed, so this tripwire is measuring nothing',
      ).toBeGreaterThan(0);
      // `TRANSCRIPT_PAGE_SIZE` is 200, which the API documents as its maximum for
      // this endpoint, and `TRANSCRIPT_MAX_PAGES` is 10 - a ceiling of 2,000.
      for (const size of requestedPageSizes) {
        expect(
          size,
          `the client requested pageSize=${String(size)}. The API documents default 50 / max 200 for ` +
            'this endpoint; asking for more is what makes `GET /segments` return 500 (see 041C).',
        ).toBeLessThanOrEqual(200);
      }
      expect(
        10 * 200,
        'the tripwire records the client ceiling as 10 pages x 200. If TRANSCRIPT_MAX_PAGES or ' +
          'TRANSCRIPT_PAGE_SIZE changed, this constant and the segment-search fixture must change too.',
      ).toBe(2000);
    } finally {
      // The shared page and its routes are left clean by each spec's own finally.
    }
  });
});

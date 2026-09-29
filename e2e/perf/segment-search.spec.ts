// Task 041D, R1/R3/R4: the segment-search budget.
//
//   segment search <= 300ms at 5k segments
//
// The measurement starts when the search input receives one `input` event and
// stops when the rendered window reflects the filtered set. The 200ms debounce
// is inside the measurement, because a user waits for it - a budget that
// started after the debounce would be measuring a filter the user never asked
// for on its own.
//
// Two things make this budget honest rather than easy:
//
//   * The corpus is exactly 5,000 segments and every 50th one carries the token
//     `perfneedle`, so the expected result is exactly 100 rows. The spec waits
//     for `data-total="100"` and fails loudly if it never arrives, because a
//     search that matches nothing would otherwise be "fast" for the wrong
//     reason.
//   * 5,000 is not reachable through the application's real data path. The
//     fixture endpoint returns the whole corpus on page 1 regardless of the page
//     size the client asked for, because `Quota:MaxSegmentCount` rejects more
//     than 2,000 and `useTranscript` fetches at most 10 pages of 200. That is a
//     recorded limitation on the budget and a finding for 027/032 - not a reason
//     to measure a smaller corpus and call it 5k.

import { expect, test } from '@playwright/test';

import { budgetById, formatVerdictLine } from './budgets.js';
import {
  SEARCH_NEEDLE,
  SYNTHETIC_SEGMENT_COUNT,
  countMatches,
  installSyntheticApi,
  perfProjectId,
} from './support/fixtures.js';
import { runBudget } from './support/gate.js';
import { installProbes, probeAttribute, probeMeasureOpen, probeSetTextNow, probeWaitForAttribute } from './support/probes.js';
import { getPerfSession, hostProfileOneLine } from './support/session.js';

const BUDGET = budgetById('segment-search');

/**
 * Three different searches, each with an exact, precomputable match count.
 *
 * `perfneedle` selects the strided rows; `perfseg00042` selects exactly one row
 * (its own index token), and `perfseg000` selects the first thousand. A term with
 * no predictable count is not usable here: an earlier version of this spec
 * searched `perfneedlea`, matched nothing, and spent thirty seconds timing out
 * against a budget that was never under test.
 */
const SEARCH_TERMS: readonly string[] = [SEARCH_NEEDLE, 'perfseg00042', 'perfseg000'];

test(`@perf ${BUDGET.title} <= ${BUDGET.limitMs}ms [${BUDGET.fixture}]`, async ({ browser }, testInfo) => {
  const session = await getPerfSession(browser);
  const { page } = session;
  // Its own project id: the transcript query key contains it, so this spec's
  // 5,000-segment corpus cannot be served from another spec's cache.
  const projectId = perfProjectId(3);
  const synthetic = await installSyntheticApi(page, {
    projectId,
    segmentCount: SYNTHETIC_SEGMENT_COUNT,
    overshootPageSize: true,
  });

  try {
    await installProbes(page);

    await probeMeasureOpen(page, {
      path: `/projects/${projectId}/transcript`,
      testId: 'page-project-transcript',
      selector: '[data-testid="transcript-list"]',
      count: 1,
    });

    const loadedTotal = await probeAttribute(page, 'transcript-list', 'data-total');
    expect(
      loadedTotal,
      `the client must hold all ${String(SYNTHETIC_SEGMENT_COUNT)} fixture segments before any search is ` +
        `measured; it reported data-total="${loadedTotal ?? 'null'}". A search over a short corpus passes ` +
        'for the wrong reason.',
    ).toBe(String(SYNTHETIC_SEGMENT_COUNT));

    const result = await runBudget({
      testInfo,
      context: session.visual.context,
      budget: BUDGET,
      measure: async (run) => {
        const term = SEARCH_TERMS[run % SEARCH_TERMS.length] ?? SEARCH_NEEDLE;
        const expected = countMatches(synthetic.segments, term);
        expect(
          expected,
          `the search term '${term}' matches ${String(expected)} fixture rows. A term that matches ` +
            'nothing makes the measurement vacuous: filtering to an empty list is fast for reasons that ' +
            'have nothing to do with a 5,000-segment corpus.',
        ).toBeGreaterThan(0);

        // Unfiltered first, so every run starts from the same state and the only
        // thing that differs between runs is the term. From run two onwards this
        // is a real change back to the full corpus, not a no-op.
        await probeSetTextNow(page, 'transcript-search', '');
        await probeWaitForAttribute(page, 'transcript-list', 'data-total', String(SYNTHETIC_SEGMENT_COUNT));

        const dispatched = await probeSetTextNow(page, 'transcript-search', term);
        const settled = await probeWaitForAttribute(
          page,
          'transcript-list',
          'data-total',
          String(expected),
        );
        return {
          valueMs: settled.at - dispatched.at,
          detail: {
            run: run + 1,
            term,
            matches: expected,
            corpus: SYNTHETIC_SEGMENT_COUNT,
            host: hostProfileOneLine(),
          },
        };
      },
    });

    expect(
      result.verdict.verdict,
      `${result.verdict.explanation}\n${formatVerdictLine(BUDGET, result.verdict.comparedMs)}\n` +
        `Trace: ${result.trace?.summary ?? 'not captured'}`,
    ).not.toBe('breach');
  } finally {
    await synthetic.close();
  }
});

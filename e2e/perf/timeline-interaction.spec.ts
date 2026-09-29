// Task 041D, R1/R3/R4: the timeline interaction budget.
//
//   timeline interaction latency <= 100ms p95
//
// The fixture is a synthetic 1,200-segment transcript served at the transport
// layer. The timeline, the dialogue lane, the marker derivation, the memoized
// lanes and the zustand store are all the real ones.
//
// The measurement is input-to-paint for the timeline's *direct* interactions -
// the ones a user performs and expects to see answered immediately. It is
// deliberately not the debounced transport set: `features/timeline/types.ts`
// `TIMELINE_DEBOUNCE_MS` is 120ms, so ruler seeks, waveform scrubs, zoom and pan
// cannot be inside a 100ms budget without contradicting a deliberate product
// decision. Those are measured by the `media-seek` budget, which the debounce
// fits inside. The debounced latencies are still taken on every run and printed,
// so a debounce regression is visible here even though it does not fail here.
//
// The p95 is nearest-rank, not interpolated: a budget that fails a release
// should fail on a latency somebody actually observed.

import { expect, test } from '@playwright/test';

import { budgetById, formatVerdictLine } from './budgets.js';
import { TIMELINE_SEGMENT_COUNT, installSyntheticApi, perfProjectId } from './support/fixtures.js';
import { runBudget } from './support/gate.js';
import { percentile } from './support/measure.js';
import { installProbes, probeAttribute, probeClick, probeClickThenTextChange, probeMeasureOpen } from './support/probes.js';
import { PERF_VIEWPORT, getPerfSession, hostProfileOneLine } from './support/session.js';

const BUDGET = budgetById('timeline-interaction');

/** Interactions per run. Twenty-five, so a p95 is the third-worst sample. */
const INTERACTIONS_PER_RUN = 25;

test(`@perf ${BUDGET.title} <= ${BUDGET.limitMs}ms p95 [${BUDGET.fixture}]`, async ({ browser }, testInfo) => {
  const session = await getPerfSession(browser);
  const { page } = session;
  // Its own project id, so this 1,200-segment corpus can never be served to a
  // spec that expects a different one from the shared page's query cache.
  const projectId = perfProjectId(4);
  const synthetic = await installSyntheticApi(page, {
    projectId,
    segmentCount: TIMELINE_SEGMENT_COUNT,
    overshootPageSize: true,
  });

  try {
    await page.setViewportSize(PERF_VIEWPORT);
    await installProbes(page);

    await probeMeasureOpen(page, {
      path: `/projects/${projectId}/timeline`,
      testId: 'page-project-timeline',
      selector: '[data-testid="timeline-dialogue-track"] button',
      count: TIMELINE_SEGMENT_COUNT,
    });

    // The dialogue lane is not windowed, so prove the corpus really is loaded
    // before timing anything against it. A 100ms p95 measured over three
    // segments would pass for entirely the wrong reason.
    const renderedSegments = await page.evaluate(
      () => document.querySelectorAll('[data-testid="timeline-dialogue-track"] button').length,
    );
    expect(
      renderedSegments,
      `the timeline must render all ${String(TIMELINE_SEGMENT_COUNT)} fixture segments; it rendered ` +
        `${String(renderedSegments)}. See the budget's limitations: the dialogue lane is not ` +
        'windowed, which is a recorded finding for 030, not a smaller fixture.',
    ).toBe(TIMELINE_SEGMENT_COUNT);

    const debouncedEvidence: number[] = [];

    const result = await runBudget({
      testInfo,
      context: session.visual.context,
      budget: BUDGET,
      measure: async (run) => {
        const samples: number[] = [];
        for (let index = 0; index < INTERACTIONS_PER_RUN; index += 1) {
          const ordinal = run * INTERACTIONS_PER_RUN + index;
          // Selecting a segment updates the store synchronously, so the
          // selected attribute is observable and the interaction is verifiably
          // doing work rather than being a no-op on a hidden element.
          const segmentIndex = (ordinal * 7) % TIMELINE_SEGMENT_COUNT;
          const testId = `timeline-segment-${synthetic.segments[segmentIndex]?.id ?? ''}`;
          const measured = await probeClick(page, testId);
          const selected = await probeAttribute(page, testId, 'data-selected');
          expect(
            selected,
            `interaction ${String(ordinal + 1)} (${testId}) left the segment unselected, so the ` +
              'latency it produced is a no-op and must not be in a percentile.',
          ).toBe('true');
          samples.push(measured.ms);
        }

        // One debounced interaction per run, recorded as evidence and not
        // gated: zoom goes through `debounce(..., TIMELINE_DEBOUNCE_MS)`, so the
        // number that matters is measured to the paint that reflects the
        // debounced result, not to the paint after the click.
        const zoom = await probeClickThenTextChange(page, 'timeline-zoom-in', 'timeline-zoom-label');
        expect(
          zoom.ms,
          `debounced zoom resolved in ${String(Math.round(zoom.ms))}ms, which is shorter than ` +
            'TIMELINE_DEBOUNCE_MS (120). Either the debounce was removed - which is a product ' +
            'decision, not a perf one, and belongs to 030 - or the probe stopped at the wrong paint.',
        ).toBeGreaterThanOrEqual(100);
        debouncedEvidence.push(zoom.ms);

        return {
          valueMs: percentile(samples, 0.95),
          detail: {
            run: run + 1,
            interactions: samples.length,
            worstMs: Math.round(Math.max(...samples)),
            medianMs: Math.round(percentile(samples, 0.5)),
            debouncedZoomMs: Math.round(zoom.ms),
            segments: TIMELINE_SEGMENT_COUNT,
            host: hostProfileOneLine(),
          },
        };
      },
    });

    expect(
      result.verdict.verdict,
      `${result.verdict.explanation}\n${formatVerdictLine(BUDGET, result.verdict.comparedMs)}\n` +
        `Debounced transport interactions (evidence only, gated by the media-seek budget): ` +
        `${debouncedEvidence.map((value) => `${String(Math.round(value))}ms`).join(', ')}\n` +
        `Trace: ${result.trace?.summary ?? 'not captured'}`,
    ).not.toBe('breach');
  } finally {
    await synthetic.close();
  }
});

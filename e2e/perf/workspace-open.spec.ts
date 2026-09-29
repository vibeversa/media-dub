// Task 041D, R1: the workspace-open budget.
//
//   workspace open <= 2s
//
// This is the one budget with no synthetic fixture and no interception: the
// workspace aggregate is served by the real API from the real seed, over the
// real transport, from the real bundle. It is also the only budget that measures
// the application's own data layer end to end rather than its render path,
// which is why it is the budget most likely to move when the API changes.
//
// "Open" is the whole workspace, not its root element. `ProjectDetailsPage`
// mounts `page-project-details` and then fills the header, stepper, review,
// warnings, output, media, config, run-history and activity panels; a budget
// that stopped at the root element would have measured a skeleton.

import { expect, test } from '@playwright/test';

import { budgetById, formatVerdictLine } from './budgets.js';
import { runBudget } from './support/gate.js';
import { installProbes, probeMeasureOpen } from './support/probes.js';
import { PROJECT_ID, getPerfSession, hostProfileOneLine } from './support/session.js';

const BUDGET = budgetById('workspace-open');

/** Sections that must all be present before the workspace counts as open. */
const WORKSPACE_SECTIONS: readonly string[] = [
  'workspace-header',
  'workspace-main',
  'workspace-secondary',
  'workspace-review',
  'workspace-warnings',
  'workspace-output',
  'workspace-media',
  'workspace-config',
  'workspace-run-history',
  'workspace-activity',
];

test(`@perf ${BUDGET.title} <= ${BUDGET.limitMs}ms [${BUDGET.fixture}]`, async ({ browser }, testInfo) => {
  const session = await getPerfSession(browser);
  const { page } = session;

  try {
    await installProbes(page);

    // The workspace's own sections, by count: the last of them to appear is what
    // makes the budget a measurement of the workspace rather than of the router.
    const sectionSelector = WORKSPACE_SECTIONS.map((id) => `[data-testid="${id}"]`).join(',');

    const warm = await probeMeasureOpen(page, {
      path: `/projects/${PROJECT_ID}`,
      testId: 'page-project-details',
      selector: sectionSelector,
      count: WORKSPACE_SECTIONS.length,
    });
    const missing = await page.evaluate((ids: readonly string[]) => {
      return ids.filter((id) => document.querySelector(`[data-testid="${id}"]`) === null);
    }, WORKSPACE_SECTIONS);
    expect(
      missing,
      `the workspace did not render ${missing.join(', ')}; this budget would be measuring a ` +
        'partially rendered screen. (A section can legitimately be absent - the cost panel is ' +
        'permission-gated - so the count is what this asserts, and the names are the diagnosis.)',
    ).toEqual([]);
    void warm;

    const result = await runBudget({
      testInfo,
      context: session.visual.context,
      budget: BUDGET,
      measure: async (run) => {
        // From the dashboard, so each run is a real route transition rather than
        // a re-render of a workspace that is already mounted.
        await probeMeasureOpen(page, { path: '/dashboard', testId: 'page-dashboard' });
        const measured = await probeMeasureOpen(page, {
          path: `/projects/${PROJECT_ID}`,
          testId: 'page-project-details',
          selector: sectionSelector,
          count: WORKSPACE_SECTIONS.length,
        });
        return {
          valueMs: measured.ms,
          detail: { run: run + 1, sections: WORKSPACE_SECTIONS.length, host: hostProfileOneLine() },
        };
      },
    });

    expect(
      result.verdict.verdict,
      `${result.verdict.explanation}\n${formatVerdictLine(BUDGET, result.verdict.comparedMs)}\n` +
        `Trace: ${result.trace?.summary ?? 'not captured'}`,
    ).not.toBe('breach');
  } finally {
    // Nothing to tear down: this budget installs no routes and touches no
    // synthetic data, and the shared page is left on the last route it rendered.
  }
});

// Task 041D, R1/R3: the project-list render budget.
//
//   project-list render <= 1.5s at 200 rows
//
// The 200 projects are a synthetic tenant served at the transport layer
// (`support/fixtures.ts`); the grid, the cells, the `DataGrid` keyboard model
// and the React render are all the real ones. What is deliberately *not* in the
// number is server time, which is why the budget's fixture line says "synthetic".
//
// "At 200 rows" is read as a 200-row *tenant* rather than a 200-row page:
// `features/projects/api.ts` `parsePageSize` clamps the requested page size to
// the API maximum of 100, so the grid can never be handed 200 rows. Both halves
// of that are asserted before the budget runs - the fixture really does contain
// 200 projects, so the result set really is two pages, and the grid really does
// render exactly the page the server sent and no more - because a budget
// measured on a half-empty table is a number about nothing.

import { expect, test } from '@playwright/test';

import { budgetById, formatVerdictLine } from './budgets.js';
import { PROJECT_PAGE_SIZE, SYNTHETIC_PROJECT_COUNT, installSyntheticApi, perfProjectId } from './support/fixtures.js';
import { runBudget } from './support/gate.js';
import { installProbes, probeCount, probeMeasureOpen } from './support/probes.js';
import { getPerfSession, hostProfileOneLine } from './support/session.js';

const BUDGET = budgetById('project-list-render');

const ROW_SELECTOR = '[data-testid="projects-grid"] table.dp-grid tbody tr';

test(`@perf ${BUDGET.title} <= ${BUDGET.limitMs}ms [${BUDGET.fixture}]`, async ({ browser }, testInfo) => {
  const session = await getPerfSession(browser);
  const { page } = session;
  const synthetic = await installSyntheticApi(page, {
    projectId: perfProjectId(1),
    segmentCount: 1,
    overshootPageSize: false,
  });

  try {
    await installProbes(page);

    // Warm the route once so run one is not paying for the lazy chunk as well as
    // the render. The budget is "project-list render", not "first visit ever".
    await probeMeasureOpen(page, {
      path: `/projects?pageSize=${String(PROJECT_PAGE_SIZE)}&page=1`,
      testId: 'page-projects',
      selector: ROW_SELECTOR,
      count: PROJECT_PAGE_SIZE,
    });

    const warmRows = await probeCount(page, ROW_SELECTOR);
    expect(
      warmRows,
      `the synthetic tenant must render a full page of ${String(PROJECT_PAGE_SIZE)} rows. If it did not, ` +
        'this budget is measuring a short table and would pass for the wrong reason.',
    ).toBe(PROJECT_PAGE_SIZE);

    const result = await runBudget({
      testInfo,
      context: session.visual.context,
      budget: BUDGET,
      measure: async (run) => {
        // A different page number is a different query key, so the projects
        // query really re-runs and the render path really re-executes. The tenant
        // has 200 projects over two pages, so pages 1 and 2 are both real.
        const pageNumber = (run % 2) + 1;
        const measured = await probeMeasureOpen(page, {
          path: `/projects?pageSize=${String(PROJECT_PAGE_SIZE)}&page=${String(pageNumber)}`,
          testId: 'page-projects',
          selector: ROW_SELECTOR,
          count: PROJECT_PAGE_SIZE,
        });
        const rows = await probeCount(page, ROW_SELECTOR);
        expect(rows, `run ${String(run + 1)} rendered ${String(rows)} rows`).toBe(PROJECT_PAGE_SIZE);
        return {
          valueMs: measured.ms,
          detail: {
            run: run + 1,
            page: pageNumber,
            rows,
            tenantProjects: SYNTHETIC_PROJECT_COUNT,
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

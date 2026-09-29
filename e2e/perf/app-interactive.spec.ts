// Task 041D, R1/R2/R3: the app-interactive budget.
//
//   app-interactive <= 3s (desktop broadband)
//
// What is measured is in `e2e/perf/budgets.ts`; this file is the rig side of
// it. Two properties of this application shape the whole spec:
//
//   1. The session is memory-only, so a document load is a logout. The cold
//      route that can be measured without a sign-in is `/login` reached from
//      `/`, and "interactive" is the sign-in form being usable - not a
//      skeleton, not a mounted shell.
//   2. A cold load is only a cold load once. `Network.setCacheDisabled` is set
//      for the whole context, so runs two and three download the bundle again
//      instead of measuring a warm HTTP cache, and each run starts from a fresh
//      document.
//
// The three runs share one browser context, so Chromium's in-process code cache
// is warm after the first. That is recorded as a limitation on the budget
// rather than fixed by paying three browser launches, which would measure
// process start-up rather than the application.

import { expect, test } from '@playwright/test';

import { DESKTOP_BROADBAND, budgetById, formatVerdictLine } from './budgets.js';
import { runBudget } from './support/gate.js';
import { installProbesForContext, measureAppInteractive } from './support/probes.js';
import { hostProfileOneLine, openColdContext } from './support/session.js';

const BUDGET = budgetById('app-interactive');

/** The submit control: the form is usable, not merely mounted. */
const INTERACTIVE_READY_TEST_ID = 'auth-submit';

test(`@perf ${BUDGET.title} <= ${BUDGET.limitMs}ms [${BUDGET.fixture}]`, async ({ browser }, testInfo) => {
  const cold = await openColdContext(browser, DESKTOP_BROADBAND);
  await installProbesForContext(cold.context);

  try {
    const result = await runBudget({
      testInfo,
      context: cold.context,
      budget: BUDGET,
      measure: async (run) => {
        // `commit` rather than `load`: the budget is the app becoming
        // interactive, and waiting for `load` would silently fold in requests
        // the application makes after first paint.
        await cold.page.goto('/', { waitUntil: 'commit' });
        await cold.page
          .getByTestId(INTERACTIVE_READY_TEST_ID)
          .waitFor({ state: 'visible', timeout: 60_000 });
        const interactiveMs = await measureAppInteractive(cold.page);
        return {
          valueMs: interactiveMs,
          detail: { run: run + 1, profile: DESKTOP_BROADBAND.id, host: hostProfileOneLine() },
        };
      },
    });

    expect(
      result.verdict.verdict,
      `${result.verdict.explanation}\n${formatVerdictLine(BUDGET, result.verdict.comparedMs)}\n` +
        `Trace: ${result.trace?.summary ?? 'not captured'}\n` +
        `Host: ${hostProfileOneLine()}`,
    ).not.toBe('breach');
  } finally {
    await cold.close();
  }
});

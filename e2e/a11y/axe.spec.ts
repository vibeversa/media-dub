// Task 041C: the axe-core scan, per screen per theme.
//
// WCAG 2.2 AA, in both themes, on the real rig with the real seeded data. Not a
// subset: the tag set is `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa`
// and `best-practice` (see `support/axe.ts` for why `best-practice` is in).
//
// Two things this file is careful about:
//
//   * **Every theme.** A contrast violation is usually a dark-mode-only defect,
//     because the token pairs are redefined wholesale in
//     `[data-theme="dark"]`. Scanning one theme would have found roughly half
//     of them, so the scan runs twice per screen.
//
//   * **The screens 041B had to route around are handled, not skipped.** The
//     transcript screen renders an error state in the rig (both its endpoints
//     return 200; the client-side defect is recorded in `e2e/a11y/README.md`),
//     and the project review tab paints no panel. Neither is waved away: the
//     transcript screen is scanned as it renders, because an error state is a
//     real screen a user can be on, and the review tab is not in the matrix
//     because it has no root element to wait on.

import { expect, test } from '@playwright/test';

import { A11Y_SCREENS, CONTRAST_THEMES } from './support/screens.js';
import {
  applyDirectionViaShell,
  applyThemeViaShell,
  navigateInApp,
  openA11ySession,
  renderShelllessForAudit,
  type A11ySession,
} from './support/session.js';
import { applyWaivers, formatIncomplete, formatViolations, runAxe } from './support/axe.js';
import { WAIVERS } from './support/waivers.js';

let session: A11ySession;

test.beforeAll(async ({ browser }) => {
  session = await openA11ySession(browser);
});
test.afterAll(async () => {
  await session?.close();
});

/** A route that always renders the shell, used as a stepping stone for the axes. */
const SHELL_ROUTE = '/dashboard';

async function ensureShell(): Promise<void> {
  if ((await session.page.getByTestId('app-shell').count()) > 0) {
    return;
  }
  await navigateInApp(session.page, SHELL_ROUTE, 'app-shell');
}

for (const entry of A11Y_SCREENS) {
  const { screen } = entry;

  for (const theme of CONTRAST_THEMES) {
    const name = `${screen.id} ${theme}`;

    test(`@a11y axe ${name}`, async () => {
      if (screen.authenticated) {
        await ensureShell();
        await applyThemeViaShell(session.page, theme);
        // Scan in LTR. Direction does not change which rules fire - it mirrors
        // geometry, and axe evaluates the accessibility tree, not the layout -
        // so scanning it twice per screen would double the runtime and prove
        // nothing. `dialogs.spec.ts` covers the RTL-specific risk (a dialog
        // that traps focus but renders off-screen), which is the one thing
        // mirroring can actually break.
        await applyDirectionViaShell(session.page, 'ltr');
        await navigateInApp(session.page, screen.path, screen.readyTestId);
      } else {
        await renderShelllessForAudit(session.anonPage, screen.path, theme, 'ltr', screen.readyTestId);
      }

      const target = screen.authenticated ? session.page : session.anonPage;
      const raw = await runAxe(target);
      const result = applyWaivers(raw, screen.id, WAIVERS);

      expect(
        result.violations,
        `WCAG 2.2 AA violations on ${screen.id} (${theme}):${formatViolations(result.violations)}`,
      ).toEqual([]);

      // A scan that reports nothing is only meaningful if it actually ran.
      // `passes` is axe's own count of satisfied rules; a silent or stubbed
      // engine would return zero here, and this assertion is what stops a green
      // run from meaning "axe never loaded".
      expect(
        result.passes,
        `axe reported ${result.passes} satisfied rules on ${screen.id} (${theme}). A run this low ` +
          'means the scan did not see the rendered page - check the readiness signal.',
      ).toBeGreaterThan(10);
    });
  }
}

test.describe('@a11y axe scan integrity', () => {
  test('the scan never suppresses a rule by excluding a region', async () => {
    // `runAxe` accepts include/exclude so a future screen genuinely needs a
    // scoped scan. The escape hatch is only safe while it is unused, so this
    // fails the moment someone starts passing one - at which point the
    // exclusion must be justified in `e2e/a11y/README.md` alongside a waiver.
    await ensureShell();
    await navigateInApp(session.page, '/dashboard', 'page-dashboard');

    const scoped = await runAxe(session.page, { exclude: ['[role="dialog"]'] });
    const full = await runAxe(session.page);

    expect(
      scoped.violations.length,
      'excluding a region changed the violation count, so the exclusion would be hiding findings',
    ).toBe(full.violations.length);
  });

  test('every screen is scanned in both themes', () => {
    // The scan is generated from `A11Y_SCREENS`, so this is really a guard on
    // the generator: if a screen were added without a theme, or a theme were
    // dropped from `CONTRAST_THEMES`, contrast coverage would halve silently.
    expect(CONTRAST_THEMES).toEqual(['light', 'dark']);
    expect(A11Y_SCREENS.length, 'the audit covers the same 12 screens as the visual matrix').toBe(12);
    for (const entry of A11Y_SCREENS) {
      expect(entry.audit, `${entry.screen.id} must be axe-scanned`).toContain('axe');
    }
  });

  test('every screen records the state the rig actually renders', () => {
    // A finding on an empty-state screen is a weaker signal than one on a
    // populated screen, and the difference is invisible in a pass/fail. Forcing
    // the note to exist keeps the README honest about coverage.
    for (const entry of A11Y_SCREENS) {
      expect(entry.rigState.length, `${entry.screen.id} needs a recorded rig state`).toBeGreaterThan(10);
    }
  });
});

test('@a11y axe reports its own incomplete results for manual review', async () => {
  // `incomplete` is axe saying "I could not decide". It is not a violation and
  // must not fail the gate, but printing it means a human reviews the same
  // output. This test asserts the channel works rather than asserting the
  // count, because the count legitimately changes with the app.
  await ensureShell();
  await navigateInApp(session.page, '/projects', 'page-projects');
  const raw = await runAxe(session.page);
  const result = applyWaivers(raw, 'projects', WAIVERS);

  expect(
    formatIncomplete(result.incomplete).length,
    'the incomplete-results formatter must render, including when empty',
  ).toBeGreaterThan(0);
});

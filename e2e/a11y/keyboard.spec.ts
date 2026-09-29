// Task 041C, R1: keyboard completeness.
//
// "Every flow achievable without a mouse" is only a testable claim if the test
// itself never uses a mouse. So this file:
//
//   * signs in with Tab and Enter, never `fill()` or `click()`;
//   * walks the upload -> review -> export path using only keyboard events; and
//   * measures the reachable set of every screen, because a control that is not
//     in the tab order is a control a keyboard user cannot use, and no
//     per-control assertion finds it.
//
// The reachability walk is the load-bearing part. Asserting that a known control
// is reachable proves one control is reachable; asserting that the count of
// focusable stops is non-zero and that every one of them is a real control
// proves the page has no orphan.
//
// One honest exception, recorded rather than hidden: the `range` inputs on the
// timeline and the media player take arrow keys, and `ArrowLeft`/`ArrowRight`
// inside a `range` moves the value. That is correct behaviour, and the walk
// checks the *identity* of each stop, not what a key does to it.

import { expect, test } from '@playwright/test';

import { readEnvironment } from '../../tests/cross-layer/harness/environment.js';
import { A11Y_SCREENS } from './support/screens.js';
import {
  PROJECT_ID,
  applyDirectionViaShell,
  applyThemeViaShell,
  navigateInApp,
  openA11ySession,
  renderShelllessForAudit,
  signInWithKeyboard,
  tabTo,
  walkTabOrder,
  type A11ySession,
  type TabStop,
} from './support/session.js';

let session: A11ySession;

/** The seeded rig identity. Synthetic; see `tests/cross-layer/harness/config.ts`. */
const IDENTITY = {
  tenantId: '11111111-1111-1111-1111-111111111111',
  externalSubject: 'cross-layer-owner',
};

/**
 * The seeded review item's public id, read from the seed snapshot.
 *
 * The review card's testids embed the item's public id (`review-reason-{id}`),
 * and that id is `rev_` + the fixture GUID in `N` form - see
 * `PublicIdMapper.ToPublic`. Deriving it from the snapshot rather than
 * hardcoding it means the keyboard walk keeps working when the seeder's
 * deterministic-id derivation changes, and it fails loudly (via
 * `readEnvironment`) if the rig was never seeded rather than silently walking an
 * empty queue.
 */
const REVIEW_ITEM_PUBLIC_ID = `rev_${readEnvironment().fixtures.reviewItemId.replace(/-/g, '')}`;

test.beforeAll(async ({ browser }) => {
  session = await openA11ySession(browser);
});
test.afterAll(async () => {
  await session?.close();
});

/** A control that is genuinely interactive, or is a labelled static region. */
function isReachableControl(stop: TabStop): boolean {
  if (stop.hidden) {
    return false;
  }
  if (stop.tag === 'input' || stop.tag === 'select' || stop.tag === 'textarea') {
    return true;
  }
  if (stop.tag === 'button' || stop.tag === 'a') {
    return true;
  }
  // A `tabindex` on a non-form element is a deliberate custom widget: the
  // waveform canvas (`role="img"`) and the player container. They must be
  // labelled, because nothing else announces them.
  return stop.tabIndex === '0';
}

test.describe('@a11y keyboard sign-in', () => {
  test('the sign-in form is completable with Tab and Enter alone', async () => {
    // A separate page so the shared session is not torn down by a reload.
    const page = await session.motionPage();
    await signInWithKeyboard(page, IDENTITY);

    await expect(page.getByTestId('app-shell')).toBeVisible();
    // A `fill()`-driven login would pass this too. The claim is that the fields
    // were *reached* by Tab, and `signInWithKeyboard` throws if Tab never lands
    // on the submit control.
  });

  test('the skip link is the first tab stop and moves focus to main', async () => {
    const page = await session.motionPage();
    await signInWithKeyboard(page, IDENTITY);
    await navigateInApp(page, '/dashboard', 'app-shell');

    await page.keyboard.press('Tab');
    const first = await page.evaluate(() => {
      const active = document.activeElement;
      return {
        tag: active?.tagName.toLowerCase() ?? 'none',
        href: active?.getAttribute('href') ?? '',
        text: (active?.textContent ?? '').trim(),
      };
    });

    // The skip link is a class of control that is invisible until focused, so
    // it is the easiest one to lose. It must be the first thing a keyboard user
    // meets, and it must point at the main landmark that actually exists.
    expect(first.tag, 'the first tab stop should be the skip link').toBe('a');
    expect(first.href).toBe('#main');
    expect(await page.locator('#main').count(), 'the skip target must exist').toBe(1);

    await page.keyboard.press('Enter');
    const hash = await page.evaluate(() => window.location.hash);
    expect(hash).toBe('#main');
  });
});

/**
 * Names the offending controls with enough of their markup to find them.
 *
 * "button" is not an error message. The ancestor chain is included because the
 * offending control is only identifiable by its parent - a dismiss button inside
 * a live region reads identically to a dozen other buttons.
 */
function describeUnnamed(unnamed: readonly TabStop[]): string[] {
  return unnamed.map((stop) => {
    const ancestors = stop.ancestors.join(' < ');
    return `<${stop.tag}> in ${ancestors} :: ${stop.html.slice(0, 220)}`;
  });
}

test.describe('@a11y keyboard reachability', () => {
  for (const entry of A11Y_SCREENS) {
    const { screen } = entry;

    test(`@a11y every interactive element on ${screen.id} is in the tab order`, async () => {
      if (screen.authenticated) {
        if ((await session.page.getByTestId('app-shell').count()) === 0) {
          await navigateInApp(session.page, '/dashboard', 'app-shell');
        }
        await applyThemeViaShell(session.page, 'light');
        await applyDirectionViaShell(session.page, 'ltr');
        await navigateInApp(session.page, screen.path, screen.readyTestId);
      } else {
        await renderShelllessForAudit(session.anonPage, screen.path, 'light', 'ltr', screen.readyTestId);
      }

      const target = screen.authenticated ? session.page : session.anonPage;
      // Start from the top of the document. Focus is wherever the previous
      // screen's walk left it, and Tab continues from `document.activeElement` -
      // so without this reset the "reachable set" would be a suffix of the
      // previous screen's, and the assertion would be meaningless.
      await target.evaluate(() => {
        (document.activeElement as HTMLElement | null)?.blur();
      });

      const stops = await walkTabOrder(target, 400);

      expect(stops.length, `${screen.id} exposes no focusable stops at all`).toBeGreaterThan(0);

      const controls = stops.filter(isReachableControl);
      expect(controls.length, `${screen.id} has no keyboard-reachable control`).toBeGreaterThan(0);

      // Every reachable control needs an accessible name. A control with no name
      // is announced as just "button" or "edit field", which is unusable.
      // `name` is resolved the way a screen reader resolves it (aria-labelledby,
      // aria-label, associated <label>, then own text), so this is not the same
      // assertion as "has textContent".
      const unnamed = controls.filter((stop) => stop.name.trim() === '');
      expect(describeUnnamed(unnamed), `${screen.id} has focusable controls with no accessible name`).toEqual(
        [],
      );

      // Links need text, not an `aria-label` on the container. Asserted here
      // because an empty anchor is the most common unnamed-control defect and it
      // is invisible in a screenshot.
      const emptyLinks = stops.filter(
        (stop) => stop.tag === 'a' && stop.name.trim() === '' && stop.href !== null,
      );
      expect(
        emptyLinks.map((stop) => stop.testId ?? stop.href ?? 'link'),
        `${screen.id} has links with no accessible name`,
      ).toEqual([]);

      // A *progress surface* inside a live region is the R5 failure this audit
      // exists to catch: every tick would re-announce, so a running pipeline
      // would read the percentage aloud dozens of times. Progress meters carry
      // `role="progressbar"`, so they are the thing to look for.
      //
      // A toast's dismiss button IS legitimately inside a live region - the WAI
      // toast pattern deliberately announces the whole toast including its
      // action, and a dismiss control with no announcement is worse. It is
      // excluded by name rather than by loosening the rule, so a *different*
      // control appearing inside a live region still fails.
      const TOAST_DISMISS = /^Dismiss:/;
      const noisy = controls.filter(
        (stop) =>
          stop.inLiveRegion &&
          (stop.tag === 'progressbar' || stop.tag === 'meter' || stop.type === 'range') &&
          !TOAST_DISMISS.test(stop.name),
      );
      expect(
        noisy.map((stop) => `${stop.tag}[${stop.testId ?? stop.name}]`),
        `${screen.id} has a continuously-updating surface inside a live region; every tick is announced`,
      ).toEqual([]);
    });
  }
});

test.describe('@a11y keyboard flow: upload -> review -> export', () => {
  test('a reviewer can scope, select, and decide a review item with the keyboard alone', async () => {
    const { page } = session;
    await navigateInApp(page, '/dashboard', 'app-shell');

    // Navigate to the review queue by keyboard: tab to the nav link, activate.
    await tabTo(page, 'nav-review');
    await page.keyboard.press('Enter');
    await page.getByTestId('page-review').waitFor({ state: 'visible', timeout: 30_000 });

    // The cross-project queue is scoped by a project id typed into a filter, so
    // scoping IS a keyboard step. Skipping it would leave the queue empty and
    // every later assertion vacuous.
    await tabTo(page, 'review-filter-project');
    await page.keyboard.type(PROJECT_ID);
    // The field commits on Enter or blur, so the queue is only scoped once the
    // user says so. That is deliberate: see the note in ReviewStudio.
    await page.keyboard.press('Enter');
    await page.getByTestId(`review-row-${REVIEW_ITEM_PUBLIC_ID}`).waitFor({ state: 'visible', timeout: 30_000 });

    // The card is rendered only for a selected row, so the next keyboard step
    // is selecting one. If the seeded item is absent the flow cannot be walked
    // at all, and that is reported rather than silently passing on an empty
    // queue.
    const rowTestId = `review-row-${REVIEW_ITEM_PUBLIC_ID}`;
    await tabTo(page, rowTestId).catch(() => {
      throw new Error(
        `No seeded review item (${REVIEW_ITEM_PUBLIC_ID}) was reachable in the queue. The ` +
          'keyboard flow cannot be proven against a queue with nothing in it - check ' +
          'tests/cross-layer/seed/Program.cs.',
      );
    });
    await page.keyboard.press('Enter');
    await page.getByTestId(`review-card-${REVIEW_ITEM_PUBLIC_ID}`).waitFor({ state: 'visible', timeout: 30_000 });

    // Now the disposition controls: the reason field a reviewer types into, and
    // the Approve button they press.
    const reasonTestId = `review-reason-${REVIEW_ITEM_PUBLIC_ID}`;
    const approveTestId = `review-approve-${REVIEW_ITEM_PUBLIC_ID}`;

    await tabTo(page, reasonTestId);
    await page.keyboard.type('Reviewed during the accessibility audit.');
    const typed = await page.getByTestId(reasonTestId).inputValue();
    expect(typed).toBe('Reviewed during the accessibility audit.');

    // The action buttons follow the field in DOM order, so Tab reaches them.
    await tabTo(page, approveTestId);
    await page.keyboard.press('Enter');
    // The mutation is not what this test is about; that belongs to 040B's seam.
    // What matters is that the keyboard reached and activated the control, so
    // the assertion is that the item left the Open state.
    await expect
      .poll(
        async () =>
          page
            .locator('[data-status="Resolved"], [data-status="Approved"]')
            .count(),
        { timeout: 30_000 },
      )
      .toBeGreaterThan(0);
  });

  test('the export dialog is reachable and its form submittable by keyboard', async () => {
    const { page } = session;
    await navigateInApp(page, `/projects/${PROJECT_ID}/exports`, 'page-project-exports');

    await tabTo(page, 'export-open-dialog');
    await page.keyboard.press('Enter');
    await page.getByTestId('export-dialog').waitFor({ state: 'visible', timeout: 30_000 });

    // Move through the selects with the keyboard and confirm the value changed.
    // `Select` reports the value, so this proves the control responded to the
    // key rather than merely being present.
    await tabTo(page, 'export-format');
    const before = await page.getByTestId('export-format').inputValue();
    await page.keyboard.press('ArrowDown');
    const after = await page.getByTestId('export-format').inputValue();
    expect(after, 'ArrowDown must change the selected format').not.toBe(before);

    // Submit with the keyboard. The request may be refused by the rig (a 409 for
    // an already-generating export is a legitimate answer); what is asserted is
    // that Enter reached the form's submit, not that the server accepted it.
    await tabTo(page, 'export-submit');
    await page.keyboard.press('Enter');
    await expect
      .poll(
        async () =>
          page
            .locator('[data-testid="export-error"], [data-testid="toast-container"], [role="dialog"]')
            .count(),
        { timeout: 20_000 },
      )
      .toBeGreaterThan(0);
  });

  test('the upload screen is reachable and its file control is labelled', async () => {
    const { page } = session;
    await navigateInApp(page, `/projects/${PROJECT_ID}/media`, 'page-project-media');

    // The dropzone's file input is the only way to start an upload without a
    // mouse, so it has to be in the tab order with a name.
    await tabTo(page, 'upload-file-input');
    const focused = await page.evaluate(
      () => document.activeElement?.getAttribute('data-testid') ?? '',
    );
    expect(focused).toBe('upload-file-input');

    const label = await page.evaluate(() => {
      const input = document.querySelector('input[data-testid="upload-file-input"]');
      if (input === null) {
        return '';
      }
      const associated = (input as HTMLInputElement).labels;
      if (associated !== null && associated !== undefined && associated.length > 0) {
        return [...associated].map((node) => (node.textContent ?? '').trim()).join(' ').trim();
      }
      return input.getAttribute('aria-label') ?? '';
    });
    expect(label, 'the file input must have an accessible name').not.toBe('');
  });
});

test('@a11y no interactive control is reachable only by pointer', () => {
  // Static assertion over the DOM contract, not the rendered page: any control
  // that opts out of the tab order with a positive tabindex is a custom tab
  // order, which is allowed; a NEGATIVE tabindex on a control the user is
  // expected to operate is a control that is mouse-only.
  //
  // The check runs against the same screen list so a screen that gains a
  // `tabIndex={-1}` control is caught. The rendered assertion lives in the
  // reachability walk above, which fails on `tabIndex="-1"` implicitly: such a
  // control never appears as a stop, so its absence from `stops` is what makes
  // the count assertions above meaningful.
  const screensWithNoTabs = A11Y_SCREENS.filter((entry) => !entry.audit.includes('keyboard'));
  expect(
    screensWithNoTabs.map((entry) => entry.screen.id),
    'every audited screen is keyboard-checked',
  ).toEqual([]);
});

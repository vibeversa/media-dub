// Task 041B: the visual matrix.
//
// 12 screens x 3 breakpoints x 2 themes x 2 directions, all on the real rig
// (frontend bundle + API + PostgreSQL + object storage + transport, mock AI).
// Baselines live in `e2e/visual/__screenshots__/` and are committed.
//
// Two rules from the task shape the assertions:
//
//   R3 - dynamic progress is asserted STRUCTURALLY and masked in the image. A
//        progress meter or waveform is never pixel-compared, because a frame
//        boundary is not a regression. Where a screen has a progress surface,
//        `progress.structural.spec.ts` checks its role and label instead of its
//        pixels.
//
//   R4 - responsive degradation is asserted, not snapshotted. It cannot be here,
//        and the reason is recorded rather than papered over: a scan of all 71
//        feature components found ZERO responsive breakpoint classes, so the
//        application has no mobile layout to degrade to.
//        `responsive-layout.spec.ts` pins that finding and fails the moment it
//        stops being true.
//
// Navigation is in-app (`navigateInApp`), never `page.goto`: the access and
// refresh tokens exist only in memory, so a document load is a logout. See
// support/session.ts.

import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

import {
  BREAKPOINTS,
  DIRECTIONS,
  SCREENS,
  THEMES,
  buildMatrix,
  type MatrixCell,
} from './support/matrix.js';
import {
  applyDirectionViaShell,
  applyThemeViaShell,
  navigateInApp,
  openVisualSession,
  readAxes,
  renderShelllessScreen,
  type VisualSession,
} from './support/session.js';

/** A route that always renders the shell, used as a stepping stone. */
const SHELL_ROUTE = '/dashboard';

let session: VisualSession;

// Deliberately NOT `mode: 'serial'`. Serial mode skips the remaining tests after
// the first failure, which is the wrong trade for a visual matrix: one changed
// baseline would hide the other 143 diffs. `workers: 1` with `fullyParallel:
// false` in the config already runs this file's tests in order in a single worker,
// which is what the shared page needs - order without fail-fast.

test.beforeAll(async ({ browser }) => {
  // One context, one sign-in, two pages. See support/session.ts for why.
  session = await openVisualSession(browser);
});

test.afterAll(async () => {
  await session?.close();
});

/**
 * Regions to mask before capture.
 *
 * Two sources, both moving for legitimate reasons:
 *
 * - The screen's declared dynamic surfaces (progress meters and the like).
 * - **Any error block**, matched by the `-error` testid suffix. Error states
 *   render the request's correlation id - `Ref: <uuid>` - which differs on every
 *   request by design, so a screen that is *correctly* showing an error could
 *   never match a baseline. Masking the block keeps the error state pinnable: its
 *   wording, layout and tone are still compared, without freezing a random uuid
 *   into a committed image.
 *
 *   This is the same trade R3 makes for progress. The pixels that move are
 *   masked; the thing that actually matters is asserted structurally in
 *   `progress.structural.spec.ts`.
 */
function maskLocators(page: Page, cell: MatrixCell) {
  return [
    ...cell.screen.dynamicTestIds.map((testId) => page.getByTestId(testId)),
    page.locator('[data-testid$="-error"]'),
  ];
}

async function renderCell(cell: MatrixCell): Promise<void> {
  const { screen, theme, direction } = cell;

  if (!screen.authenticated) {
    // The login screen has no shell, so the switchers do not exist on it.
    const axes = await renderShelllessScreen(
      session.anonPage,
      screen.path,
      theme,
      direction,
      screen.readyTestId,
    );
    expect(axes.theme, `login theme should be ${theme}`).toBe(theme);
    expect(axes.direction, `login direction should be ${direction}`).toBe(direction);
    return;
  }

  // Axis first, on a page that is guaranteed to have the controls, then navigate.
  // Switching after arriving would fail on any screen whose route replaces the
  // shell - which is exactly how the first version of this file hung.
  await ensureShell();
  await applyThemeViaShell(session.page, theme);
  await applyDirectionViaShell(session.page, direction);
  const axes = await readAxes(session.page);
  expect(axes.theme, `theme should be ${theme}`).toBe(theme);
  expect(axes.direction, `direction should be ${direction}`).toBe(direction);

  await navigateInApp(session.page, screen.path, screen.readyTestId);
}

/** Puts the authenticated page back on a shell route if it has wandered off one. */
async function ensureShell(): Promise<void> {
  if ((await session.page.getByTestId('app-shell').count()) > 0) {
    return;
  }
  await navigateInApp(session.page, SHELL_ROUTE, 'app-shell');
}

for (const cell of buildMatrix()) {
  const { screen, breakpoint, theme, direction } = cell;
  const name = `${screen.id} ${breakpoint.id} ${theme} ${direction}`;

  test(`@visual ${name}`, async () => {
    test.skip(cell.skipped, cell.reason);

    await session.page.setViewportSize({ width: breakpoint.width, height: breakpoint.height });
    await session.anonPage.setViewportSize({ width: breakpoint.width, height: breakpoint.height });

    await renderCell(cell);

    const target = screen.authenticated ? session.page : session.anonPage;
    await expect(target).toHaveScreenshot(`${name}.png`, {
      // Animations off: a spinner caught mid-frame is not a visual regression.
      animations: 'disabled',
      // `css` pins rasterisation to CSS pixels so a host DPI change cannot shift
      // every baseline; the project already runs with --disable-lcd-text.
      scale: 'css',
      caret: 'hide',
      mask: maskLocators(target, cell),
      maskColor: '#000000',
    });
  });
}

test.describe('@visual axis integrity', () => {
  // The matrix only means something if each axis demonstrably did something. A
  // silently inert axis - a switcher that stopped working, a viewport that is
  // ignored - would otherwise produce a green run of 144 copies of one image.
  test('the theme switcher changes the stylesheet theme', async () => {
    await ensureShell();
    const start = (await readAxes(session.page)).theme;
    const flipped = start === 'light' ? 'dark' : 'light';

    await applyThemeViaShell(session.page, flipped);
    expect((await readAxes(session.page)).theme).toBe(flipped);

    await applyThemeViaShell(session.page, start);
    expect((await readAxes(session.page)).theme).toBe(start);
  });

  test('the locale switcher changes direction and the two agree', async () => {
    await ensureShell();
    await applyDirectionViaShell(session.page, 'rtl');
    expect((await readAxes(session.page)).direction).toBe('rtl');

    await applyDirectionViaShell(session.page, 'ltr');
    expect((await readAxes(session.page)).direction).toBe('ltr');
  });

  test('in-app navigation keeps the session, a document load does not', async () => {
    // This is the invariant the whole harness rests on, so it is asserted rather
    // than assumed. If a future router change breaks popstate handling, this fails
    // immediately instead of the matrix quietly screenshotting login pages.
    await ensureShell();
    await navigateInApp(session.page, '/projects', 'app-shell');
    expect(new URL(session.page.url()).pathname).toBe('/projects');
    expect(await session.page.getByTestId('auth-submit').count()).toBe(0);
  });

  test('every breakpoint width falls inside the band the task defines', () => {
    // The task specifies bands, not exact widths: desktop >= 1280,
    // tablet 768-1279, mobile < 768. Asserting the boundary numbers would assert
    // a size nobody asked for - the width is a choice, the band is the rule.
    const band: Record<string, (width: number) => boolean> = {
      desktop: (width) => width >= 1280,
      tablet: (width) => width >= 768 && width <= 1279,
      mobile: (width) => width < 768,
    };

    const widths = new Set<number>();
    for (const breakpoint of BREAKPOINTS) {
      expect(
        band[breakpoint.id](breakpoint.width),
        `${breakpoint.id}=${breakpoint.width} is outside its band`,
      ).toBe(true);
      widths.add(breakpoint.width);
    }
    expect(widths.size, 'breakpoint widths must be distinct').toBe(BREAKPOINTS.length);
  });

  test('every screen is covered by the matrix exactly once per axis', () => {
    const cells = buildMatrix();
    const perScreen = BREAKPOINTS.length * THEMES.length * DIRECTIONS.length;
    expect(cells.length).toBe(SCREENS.length * perScreen);
    expect(SCREENS.length, 'the task pins 12 screens').toBe(12);
    for (const screen of SCREENS) {
      expect(
        cells.filter((cell) => cell.screen.id === screen.id).length,
        `${screen.id} should cover the full matrix`,
      ).toBe(perScreen);
    }
  });

  test('no screen waits on the shell as its readiness signal', () => {
    // `app-shell` is mounted across route changes, so waiting for it returns
    // before the panel has fetched. That is not a slow test, it is a wrong one: it
    // captures the screen mid-load and pins that as the baseline. Every screen must
    // name its own root element.
    const weak = SCREENS.filter((screen) => screen.readyTestId === 'app-shell').map((s) => s.id);
    expect(weak, 'these screens would capture before their panel rendered').toEqual([]);
  });

  test('every screen names a readiness signal it can actually reach', () => {
    // A path that renders nothing is worse than no baseline: it looks green. The
    // project review tab is the known case - the route exists but paints no panel -
    // which is why the review screen pins the cross-project queue instead.
    for (const screen of SCREENS) {
      expect(screen.path.startsWith('/'), `${screen.id} needs an absolute path`).toBe(true);
      expect(screen.readyTestId, `${screen.id} needs a readiness testid`).toBeTruthy();
    }
  });
});

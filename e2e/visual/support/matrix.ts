// Task 041B: the visual matrix definition.
//
// One place that states what is pinned, so a screen added later has an obvious
// home and an N/A has to carry a reason rather than quietly disappearing.

/** Breakpoints. The task fixes the boundaries; these are the sizes inside them. */
export const BREAKPOINTS = [
  { id: 'desktop', width: 1440, height: 900 },
  { id: 'tablet', width: 1024, height: 768 },
  { id: 'mobile', width: 390, height: 844 },
] as const;

export const THEMES = ['light', 'dark'] as const;
export const DIRECTIONS = ['ltr', 'rtl'] as const;

/**
 * Locales that drive direction.
 *
 * There is no `?dir=` query parameter in the app - Task 045's override was never
 * built. Direction is a function of the store locale (`i18n/format.ts`
 * `applyDirection` sets `document.dir` from `isRtlLocale`), and the shell exposes
 * a `LocaleSwitcher` over `['en', 'ar']`. So RTL is driven by switching to `ar`,
 * which is the honest way a user reaches that layout.
 */
export const DIRECTION_LOCALE: Readonly<Record<(typeof DIRECTIONS)[number], string>> = {
  ltr: 'en',
  rtl: 'ar',
};

/** Accessible names of the two `LocaleSwitcher` buttons (from `nav.json`). */
export const LOCALE_BUTTON_NAME: Readonly<Record<(typeof DIRECTIONS)[number], string>> = {
  ltr: 'English',
  rtl: 'العربية',
};

/** The seeded project's public id. Fixed by `harness/config.ts` `SEED.projectId`. */
export const PROJECT_ID = 'prj_33333333333333333333333333333333';

export interface VisualScreen {
  /** Stable slug. Part of the baseline filename, so never rename casually. */
  readonly id: string;
  /** Human name, also the test title prefix. */
  readonly title: string;
  /** Route, relative to the app root. */
  readonly path: string;
  /** False only for the login screen, which has no shell to switch theme from. */
  readonly authenticated: boolean;
  /**
   * Testid that proves the screen finished rendering. Waited on before every
   * screenshot, because a screenshot of a half-painted screen is a flake.
   */
  readonly readyTestId: string;
  /**
   * Locators whose contents change between otherwise identical renders and must
   * therefore be masked rather than pixel-compared (R3).
   */
  readonly dynamicTestIds: readonly string[];
  /**
   * Matrix entries that are not applicable, with the reason kept in code (R5 of
   * the task's edge cases: "marked N/A in code with reason, not deleted").
   */
  readonly notApplicable?: ReadonlyArray<{
    readonly axis: string;
    readonly reason: string;
  }>;
}

/**
 * The 12 screens.
 *
 * `dynamicTestIds` covers anything that legitimately moves: progress meters,
 * relative timestamps, and the live clock in the shell. Masking is what keeps a
 * render from flaking; R3 forbids pixel-comparing them, so each one is also
 * asserted structurally in `screens.spec.ts` rather than trusted to the mask.
 */
export const SCREENS: readonly VisualScreen[] = [
  {
    id: 'login',
    title: 'login',
    path: '/login',
    authenticated: false,
    readyTestId: 'page-login',
    dynamicTestIds: [],
  },
  {
    id: 'dashboard',
    title: 'dashboard',
    path: '/dashboard',
    authenticated: true,
    readyTestId: 'page-dashboard',
    dynamicTestIds: [],
  },
  {
    id: 'projects',
    title: 'project list',
    path: '/projects',
    authenticated: true,
    readyTestId: 'page-projects',
    dynamicTestIds: [],
  },
  {
    id: 'wizard',
    title: 'project create wizard',
    path: '/projects/new',
    authenticated: true,
    readyTestId: 'page-project-create',
    dynamicTestIds: [],
  },
  {
    id: 'upload',
    title: 'upload',
    path: `/projects/${PROJECT_ID}/media`,
    authenticated: true,
    readyTestId: 'page-project-media',
    dynamicTestIds: ['upload-overall'],
  },
  {
    id: 'workspace',
    title: 'workspace',
    // The workspace is the project Overview at `/projects/{id}`. There is no
    // `/projects/{id}/workspace` child route, so that path matches nothing and
    // renders a bare tab bar - which is how the first baseline set came to pin
    // twelve empty screens. `ProjectDetailsPage` is the workspace.
    path: `/projects/${PROJECT_ID}`,
    authenticated: true,
    readyTestId: 'page-project-details',
    dynamicTestIds: [],
  },
  {
    id: 'transcript',
    title: 'transcript',
    path: `/projects/${PROJECT_ID}/transcript`,
    authenticated: true,
    readyTestId: 'page-project-transcript',
    dynamicTestIds: [],
  },
  {
    id: 'translation',
    title: 'translation',
    path: `/projects/${PROJECT_ID}/translation`,
    authenticated: true,
    readyTestId: 'page-project-translation',
    dynamicTestIds: [],
  },
  {
    id: 'voices',
    title: 'voices',
    path: `/projects/${PROJECT_ID}/voices`,
    authenticated: true,
    readyTestId: 'page-project-voices',
    dynamicTestIds: [],
  },
  {
    id: 'review',
    title: 'review',
    // The cross-project review QUEUE at `/review`, not the project tab. The tab
    // route `/projects/{id}/review` is registered but renders no panel on a
    // seeded project - only the tab bar - so pinning it would have committed
    // twelve baselines of an empty region. `page-review` is the queue's root.
    path: '/review',
    authenticated: true,
    readyTestId: 'page-review',
    dynamicTestIds: [],
  },
  {
    id: 'quality',
    title: 'quality control',
    path: `/projects/${PROJECT_ID}/quality`,
    authenticated: true,
    readyTestId: 'page-project-quality',
    dynamicTestIds: [],
  },
  {
    id: 'exports',
    title: 'outputs and exports',
    path: `/projects/${PROJECT_ID}/exports`,
    authenticated: true,
    readyTestId: 'page-project-exports',
    dynamicTestIds: [],
  },
];

/**
 * The fixed instant every visual render sees.
 *
 * The seeder stamps rows with this same instant, so a relative time rendered
 * from it is stable. `page.clock.setFixedTime` pins `Date.now()` while leaving
 * real timers running - `clock.install()` would also freeze the app's own
 * scheduling and can leave a debounced fetch permanently unfired, which looks
 * like a rendering bug and is not one.
 */
export const FIXED_INSTANT = '2026-01-15T12:00:00.000Z';

export type BreakpointId = (typeof BREAKPOINTS)[number]['id'];
export type ThemeName = (typeof THEMES)[number];
export type DirectionName = (typeof DIRECTIONS)[number];

export interface MatrixCell {
  readonly screen: VisualScreen;
  readonly breakpoint: (typeof BREAKPOINTS)[number];
  readonly theme: ThemeName;
  readonly direction: DirectionName;
  /** True when the screen's `notApplicable` list covers this cell. */
  readonly skipped: boolean;
  /** The reason, when skipped. */
  readonly reason: string | undefined;
}

/** Expands the full matrix, carrying N/A reasons through instead of dropping cells. */
export function buildMatrix(): MatrixCell[] {
  const cells: MatrixCell[] = [];
  for (const screen of SCREENS) {
    for (const breakpoint of BREAKPOINTS) {
      for (const theme of THEMES) {
        for (const direction of DIRECTIONS) {
          const na = screen.notApplicable?.find(
            (entry) =>
              entry.axis === `${breakpoint.id}/${theme}/${direction}` ||
              entry.axis === `${breakpoint.id}` ||
              entry.axis === `${theme}` ||
              entry.axis === `${direction}`,
          );
          cells.push({
            screen,
            breakpoint,
            theme,
            direction,
            skipped: na !== undefined,
            reason: na?.reason,
          });
        }
      }
    }
  }
  return cells;
}

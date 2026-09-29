// Task 041C: which screens the audit covers, and how each one is reached.
//
// The list is derived from 041B's visual matrix rather than restated, because two
// lists of "the screens" is exactly how an audit silently stops covering one.
// `SCREENS` is the single source of truth; this module only adds what an
// accessibility audit needs and a pixel comparison does not.
//
// The additions are:
//
//   `audit`     which checks apply. A screen that has no dialog is not
//               "exempt" from the dialog check - it simply has nothing to
//               check, and the check asserts that absence honestly.
//   `audience`  `anonymous` renders outside the shell (login); everything else
//               needs a session.
//   `note`      why a screen is in a particular state under the rig, so a
//               finding on an empty-state screen is not mistaken for a finding
//               on the populated one.

import { SCREENS, THEMES, type ThemeName, type VisualScreen } from '../../visual/support/matrix.js';

export type { ThemeName, VisualScreen };
export { THEMES };

/** The criteria families this task defines, which map onto R1-R5. */
export type AuditCheck =
  /** R2: axe scan, names, and contrast. */
  | 'axe'
  /** R1: reachable and operable from the keyboard. */
  | 'keyboard'
  /** R2: a visible focus indicator on every stop. */
  | 'focus'
  /** R3: no non-essential animation under `prefers-reduced-motion`. */
  | 'motion'
  /** R4: dialogs trap focus and restore it. */
  | 'dialog'
  /** R4: media transport is keyboard-operable. */
  | 'media'
  /** R5: live regions announce without spamming. */
  | 'live';

export interface A11yScreen {
  readonly screen: VisualScreen;
  readonly audience: 'anonymous' | 'authenticated';
  /** The criteria families that have something to assert here. */
  readonly audit: readonly AuditCheck[];
  /**
   * What the rig can actually render on this screen. Recorded per screen
   * because it differs, and because a finding on a screen that only ever shows
   * its empty state is a weaker signal than one on a populated screen.
   */
  readonly rigState: string;
}

const ALL_CHECKS: readonly AuditCheck[] = [
  'axe',
  'keyboard',
  'focus',
  'motion',
  'dialog',
  'media',
  'live',
];

/**
 * The rig cannot run the pipeline: the media workers are FFmpeg-backed and are
 * not in the compose file, so a run sits `Pending` at `MediaValidation` and the
 * pipeline stages never execute. Project-scoped screens therefore render seeded
 * or empty states. That is a true observation of what a user sees on a fresh
 * project, and it is recorded per screen rather than assumed.
 */
const RIG_STATE_BY_SCREEN: Readonly<Record<string, string>> = {
  login: 'unauthenticated form',
  dashboard: 'one tenant, one user, seeded usage rows',
  projects: 'two seeded projects (pilot + pipeline), no live run',
  wizard: 'empty create form, step 1',
  upload: 'dropzone only; no upload in flight and no session',
  workspace: 'MediaReady, one completed anchor run, no active run',
  transcript: 'one seeded segment; the editor renders its error state (see README)',
  translation: 'one seeded segment with a selected translation',
  voices: 'one seeded speaker with two stock voices',
  review: 'cross-project queue with one seeded open review item',
  quality: 'no QC issues; the summary and empty state render',
  exports: 'one completed seeded export job and one seeded content object',
};

/** Screens whose dialog surfaces the audit can reach and exercise. */
const WITH_DIALOG: ReadonlySet<string> = new Set(['projects', 'upload', 'workspace', 'exports']);

/** Screens that host a media player. */
const WITH_MEDIA: ReadonlySet<string> = new Set(['review', 'quality', 'workspace']);

/** Screens that render a live region of their own. */
const WITH_LIVE: ReadonlySet<string> = new Set([
  'dashboard',
  'projects',
  'workspace',
  'review',
  'exports',
  'login',
]);

/**
 * Screens whose surfaces are expected to animate, and therefore are the ones
 * R3 has something to measure. The reduced-motion rule is global, but a screen
 * with no animation at all cannot demonstrate that the rule works.
 */
const WITH_MOTION: ReadonlySet<string> = new Set(['workspace', 'upload', 'review', 'quality']);

export const A11Y_SCREENS: readonly A11yScreen[] = SCREENS.map((screen) => ({
  screen,
  audience: screen.authenticated ? 'authenticated' : 'anonymous',
  audit: [
    'axe',
    'keyboard',
    'focus',
    ...(WITH_MOTION.has(screen.id) ? (['motion'] as const) : []),
    ...(WITH_DIALOG.has(screen.id) ? (['dialog'] as const) : []),
    ...(WITH_MEDIA.has(screen.id) ? (['media'] as const) : []),
    ...(WITH_LIVE.has(screen.id) ? (['live'] as const) : []),
  ] as readonly AuditCheck[],
  rigState: RIG_STATE_BY_SCREEN[screen.id] ?? 'as the rig renders it',
}));

/** Every screen id, for the matrix guard in `waivers.spec.ts`. */
export const AUDITED_SCREEN_IDS: readonly string[] = A11Y_SCREENS.map((entry) => entry.screen.id);

export function screenById(id: string): A11yScreen {
  const found = A11Y_SCREENS.find((entry) => entry.screen.id === id);
  if (found === undefined) {
    throw new Error(`No audited screen '${id}'. Add it to SCREENS in e2e/visual/support/matrix.ts.`);
  }
  return found;
}

/** The theme both audits scan. Contrast is checked in light *and* dark (R2). */
export const CONTRAST_THEMES: readonly ThemeName[] = [...THEMES];

/** The full set of checks, for the coverage guard. */
export { ALL_CHECKS };

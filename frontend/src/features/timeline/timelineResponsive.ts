/**
 * Responsive breakpoint contract (GAP-023).
 *
 * One source of truth shared by the components and the Playwright spec so the
 * CSS-only and JS-driven paths can never disagree. The boundary is Tailwind's
 * `md` (768px): at 768px and above the workspace renders the canvas waveform
 * plus the five-lane timeline, below that it degrades to list mode, because a
 * horizontally-scrolling pixel grid is unreadable on a phone.
 *
 * Timing stays display-only: no function here moves a segment boundary.
 */

/** Widest viewport (exclusive) that still renders timeline list mode. */
export const TIMELINE_LIST_MAX_WIDTH_PX = 767;

/** Layout mode for a viewport width. Pure. */
export type TimelineLayoutMode = 'list' | 'canvas';

/**
 * Resolves the timeline layout mode for a viewport width. Pure: unknown
 * widths (NaN, Infinity, negatives) fall back to the wide layout so a
 * measurement failure never hides the timeline.
 */
export function timelineModeForWidth(width: number): TimelineLayoutMode {
  if (!Number.isFinite(width)) {
    return 'canvas';
  }

  return width <= TIMELINE_LIST_MAX_WIDTH_PX ? 'list' : 'canvas';
}

/**
 * CSS media query matching the same boundary, for the Playwright spec and for
 * components that prefer a CSS-only branch over a resize listener.
 */
export const TIMELINE_LIST_MEDIA_QUERY = `(max-width: ${String(TIMELINE_LIST_MAX_WIDTH_PX)}px)`;
/** Maps an arbitrary review status onto a frozen i18n key, never an empty key. */
export function reviewStateKey(status: string | undefined): string {
  const normalized = (status ?? '').trim().toLowerCase();
  switch (normalized) {
    case 'open':
      return 'open';
    case 'approved':
      return 'approved';
    case 'rejected':
      return 'rejected';
    case 'pending':
      return 'pending';
    default:
      return 'pending';
  }
}

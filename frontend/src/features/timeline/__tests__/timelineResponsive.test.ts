// GAP-023: responsive mobile/tablet layout. Below the tablet breakpoint the
// timeline degrades to list mode (a canvas waveform plus a five-lane timeline
// cannot be read on a phone), and the shell swaps the sidebar for a stacked
// mobile nav. The pure mode function is the single source of truth for both
// the component and the Playwright spec.
import { describe, expect, it } from 'vitest';
import { TIMELINE_LIST_MAX_WIDTH_PX, timelineModeForWidth } from '../timelineResponsive.js';

describe('timeline responsive mode', () => {
  it('degrades to list mode below the tablet breakpoint', () => {
    expect(timelineModeForWidth(320)).toBe('list');
    expect(timelineModeForWidth(375)).toBe('list');
    expect(timelineModeForWidth(TIMELINE_LIST_MAX_WIDTH_PX - 1)).toBe('list');
  });

  it('renders the canvas timeline from the tablet breakpoint up', () => {
    // The boundary is Tailwind `md` (768px): 767 is the last list-mode width.
    expect(timelineModeForWidth(TIMELINE_LIST_MAX_WIDTH_PX)).toBe('list');
    expect(timelineModeForWidth(768)).toBe('canvas');
    expect(timelineModeForWidth(1024)).toBe('canvas');
    expect(timelineModeForWidth(1440)).toBe('canvas');
  });

  it('treats unknown widths as the wide layout', () => {
    expect(timelineModeForWidth(Number.NaN)).toBe('canvas');
    expect(timelineModeForWidth(Number.POSITIVE_INFINITY)).toBe('canvas');
  });

  it('pins the breakpoint to the documented tablet value', () => {
    // Plan B 15.8 / 12.12: the degradation boundary is 768px, the Tailwind
    // `md` breakpoint, so CSS and JS never disagree.
    expect(TIMELINE_LIST_MAX_WIDTH_PX).toBe(767);
  });
});
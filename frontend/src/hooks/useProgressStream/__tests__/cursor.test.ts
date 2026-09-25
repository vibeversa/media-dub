import { describe, expect, it } from 'vitest';
import { SseCursorTracker, createCursorTracker } from '../../useProgressStream.js';

describe('SseCursorTracker dedupe and stale tolerance', () => {
  it('accepts new ids and rejects duplicates', () => {
    const tracker = createCursorTracker();
    expect(tracker.shouldProcess('evt_1')).toBe(true);
    tracker.markSeen('evt_1');
    expect(tracker.shouldProcess('evt_1')).toBe(false);
    expect(tracker.hasSeen('evt_1')).toBe(true);
    expect(tracker.lastCursor).toBe('evt_1');
  });

  it('rejects empty cursors', () => {
    const tracker = createCursorTracker();
    expect(tracker.shouldProcess('')).toBe(false);
    expect(tracker.shouldProcess(undefined)).toBe(false);
  });

  it('tracks arrival order without timestamps', () => {
    const tracker = new SseCursorTracker();
    tracker.markSeen('aaa');
    tracker.markSeen('zzz');
    expect(tracker.lastCursor).toBe('zzz');
    expect(tracker.seenCount).toBe(2);
    // Replayed duplicates stay stale even though lexically older/newer.
    expect(tracker.shouldProcess('aaa')).toBe(false);
    expect(tracker.shouldProcess('zzz')).toBe(false);
    expect(tracker.shouldProcess('mmm')).toBe(true);
  });

  it('resets for tests', () => {
    const tracker = createCursorTracker();
    tracker.markSeen('evt_1');
    tracker.reset();
    expect(tracker.lastCursor).toBeUndefined();
    expect(tracker.shouldProcess('evt_1')).toBe(true);
  });

  it('tolerates GUID-shaped opaque cursors', () => {
    const tracker = createCursorTracker();
    const first = 'a3f1c9e2b4d6478aa1b2c3d4e5f6071';
    const second = '09be28aa4c1d4e9fa2b7c8d0e1f2a3b';
    tracker.markSeen(first);
    expect(tracker.shouldProcess(second)).toBe(true);
    tracker.markSeen(second);
    expect(tracker.shouldProcess(first)).toBe(false);
  });
});

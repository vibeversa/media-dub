import { create } from 'zustand';

export interface TranscriptPlaybackState {
  /** Last reported playback position (ms). Task 030 drives this live. */
  readonly positionMs: number;
  /** Latest seek request target (ms); the player slot consumes it. */
  readonly seekTargetMs: number | undefined;
  /** Monotonic seek counter so identical targets still retrigger. */
  readonly seekVersion: number;
  readonly requestSeek: (ms: number) => void;
  readonly reportPosition: (ms: number) => void;
  readonly resetForTests: () => void;
}

/**
 * Shared transcript player store (Task 027).
 *
 * Minimal seek/position contract that Task 030 `MediaPlayer` adopts in
 * compact mode: selecting a row calls `requestSeek(segment.startMs)` and the
 * player slot seeks ms-accurately; playback reports back via
 * `reportPosition` so the active row highlights. Holds positions only —
 * never media bytes, URLs, or transcript content beyond timing.
 */
export const useTranscriptPlaybackStore = create<TranscriptPlaybackState>()((set) => ({
  positionMs: 0,
  seekTargetMs: undefined,
  seekVersion: 0,
  requestSeek: (ms: number) => {
    const clamped = Number.isFinite(ms) ? Math.max(0, Math.round(ms)) : 0;
    set((state) => ({
      positionMs: clamped,
      seekTargetMs: clamped,
      seekVersion: state.seekVersion + 1,
    }));
  },
  reportPosition: (ms: number) => {
    if (!Number.isFinite(ms) || ms < 0) {
      return;
    }
    set({ positionMs: Math.round(ms) });
  },
  resetForTests: () => {
    set({ positionMs: 0, seekTargetMs: undefined, seekVersion: 0 });
  },
}));

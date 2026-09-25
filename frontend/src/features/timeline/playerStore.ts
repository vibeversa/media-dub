import { create } from 'zustand';
import { useTranscriptPlaybackStore } from '../transcript/playerStore.js';
import { TIMELINE_ZOOM_MAX, TIMELINE_ZOOM_MIN } from './types.js';

export interface PlayRegion {
  readonly startMs: number;
  readonly endMs: number;
}

export interface TimelinePlayerState {
  /** Last reported playback position (ms). */
  readonly positionMs: number;
  /** Media duration (ms) once metadata loads; 0 until known. */
  readonly durationMs: number;
  readonly isPlaying: boolean;
  readonly volume: number;
  readonly rate: number;
  readonly muted: boolean;
  /** Latest seek request target (ms); the media element consumes it. */
  readonly seekTargetMs: number | undefined;
  /** Monotonic seek counter so identical targets still retrigger. */
  readonly seekVersion: number;
  readonly selectedSegmentId: string | undefined;
  readonly playRegion: PlayRegion | undefined;
  readonly loopRegion: boolean;
  /** Pixels per millisecond for the shared timeline viewport. */
  readonly pxPerMs: number;
  readonly viewportStartMs: number;
  readonly requestSeek: (ms: number) => void;
  readonly reportPosition: (ms: number) => void;
  readonly setDuration: (ms: number) => void;
  readonly setPlaying: (playing: boolean) => void;
  readonly setVolume: (volume: number) => void;
  readonly setRate: (rate: number) => void;
  readonly setMuted: (muted: boolean) => void;
  readonly selectSegment: (segmentId: string | undefined) => void;
  readonly setPlayRegion: (region: PlayRegion | undefined) => void;
  readonly setLoopRegion: (loop: boolean) => void;
  readonly setViewport: (startMs: number, pxPerMs: number) => void;
  readonly zoomViewport: (factor: number, anchorMs?: number) => void;
  readonly panViewport: (deltaMs: number) => void;
  readonly jumpToIssue: (atMs: number | undefined) => void;
  readonly resetForTests: () => void;
}

function clampMs(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }
  return Math.max(0, Math.round(value));
}

function clampViewport(startMs: number, pxPerMs: number): { startMs: number; pxPerMs: number } {
  const zoom = Number.isFinite(pxPerMs) ? Math.min(TIMELINE_ZOOM_MAX, Math.max(TIMELINE_ZOOM_MIN / 4, pxPerMs)) : 0.1;
  return { startMs: clampMs(startMs), pxPerMs: zoom };
}

/**
 * Shared timeline player store (Task 030).
 *
 * Single playback + viewport instance reused by `MediaPlayer` (full and
 * compact modes), `Waveform`, and `Timeline` so Task 027 can embed the
 * compact player without a second media element: mounting
 * `<MediaPlayer compact>` reuses this position/seek state and the same
 * `timeline.media` query cache. Seek/position also mirror into the
 * transcript playback store so the Task 027 active row stays in sync.
 * Holds positions only — never media bytes or signed URLs.
 */
export const useTimelinePlayerStore = create<TimelinePlayerState>()((set) => ({
  positionMs: 0,
  durationMs: 0,
  isPlaying: false,
  volume: 1,
  rate: 1,
  muted: false,
  seekTargetMs: undefined,
  seekVersion: 0,
  selectedSegmentId: undefined,
  playRegion: undefined,
  loopRegion: false,
  pxPerMs: 0.1,
  viewportStartMs: 0,
  requestSeek: (ms: number) => {
    const clamped = clampMs(ms);
    set((state) => ({
      positionMs: clamped,
      seekTargetMs: clamped,
      seekVersion: state.seekVersion + 1,
    }));
    useTranscriptPlaybackStore.getState().requestSeek(clamped);
  },
  reportPosition: (ms: number) => {
    if (!Number.isFinite(ms) || ms < 0) {
      return;
    }
    const clamped = Math.round(ms);
    set({ positionMs: clamped });
    useTranscriptPlaybackStore.getState().reportPosition(clamped);
  },
  setDuration: (ms: number) => {
    set({ durationMs: clampMs(ms) });
  },
  setPlaying: (playing: boolean) => {
    set({ isPlaying: playing });
  },
  setVolume: (volume: number) => {
    const clamped = Number.isFinite(volume) ? Math.min(1, Math.max(0, volume)) : 1;
    set({ volume: clamped, muted: clamped === 0 ? true : false });
  },
  setRate: (rate: number) => {
    const clamped = Number.isFinite(rate) && rate > 0 ? rate : 1;
    set({ rate: clamped });
  },
  setMuted: (muted: boolean) => {
    set({ muted });
  },
  selectSegment: (segmentId: string | undefined) => {
    set({ selectedSegmentId: segmentId });
  },
  setPlayRegion: (region: PlayRegion | undefined) => {
    if (region === undefined) {
      set({ playRegion: undefined });
      return;
    }
    const start = clampMs(region.startMs);
    const end = clampMs(region.endMs);
    if (end <= start) {
      set({ playRegion: undefined });
      return;
    }
    set({ playRegion: { startMs: start, endMs: end } });
  },
  setLoopRegion: (loop: boolean) => {
    set({ loopRegion: loop });
  },
  setViewport: (startMs: number, pxPerMs: number) => {
    const clamped = clampViewport(startMs, pxPerMs);
    set({ viewportStartMs: clamped.startMs, pxPerMs: clamped.pxPerMs });
  },
  zoomViewport: (factor: number, anchorMs?: number) => {
    if (!Number.isFinite(factor) || factor <= 0) {
      return;
    }
    set((state) => {
      const next = Math.min(TIMELINE_ZOOM_MAX, Math.max(TIMELINE_ZOOM_MIN / 4, state.pxPerMs * factor));
      if (anchorMs === undefined || !Number.isFinite(anchorMs)) {
        return { pxPerMs: next };
      }
      const anchor = clampMs(anchorMs);
      const shift = (anchor - state.viewportStartMs) * (1 - state.pxPerMs / next);
      return { pxPerMs: next, viewportStartMs: clampMs(state.viewportStartMs + shift) };
    });
  },
  panViewport: (deltaMs: number) => {
    if (!Number.isFinite(deltaMs) || deltaMs === 0) {
      return;
    }
    set((state) => ({ viewportStartMs: clampMs(state.viewportStartMs + deltaMs) }));
  },
  jumpToIssue: (atMs: number | undefined) => {
    if (atMs === undefined || !Number.isFinite(atMs)) {
      return;
    }
    const clamped = clampMs(atMs);
    set((state) => ({
      positionMs: clamped,
      seekTargetMs: clamped,
      seekVersion: state.seekVersion + 1,
    }));
    useTranscriptPlaybackStore.getState().requestSeek(clamped);
  },
  resetForTests: () => {
    set({
      positionMs: 0,
      durationMs: 0,
      isPlaying: false,
      volume: 1,
      rate: 1,
      muted: false,
      seekTargetMs: undefined,
      seekVersion: 0,
      selectedSegmentId: undefined,
      playRegion: undefined,
      loopRegion: false,
      pxPerMs: 0.1,
      viewportStartMs: 0,
    });
  },
}));

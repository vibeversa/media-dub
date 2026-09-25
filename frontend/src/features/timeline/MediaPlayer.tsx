import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent, ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { isExpiredError } from './types.js';
import { FRAME_MS, SEEK_STEP_MS } from './types.js';
import { invalidateTimelineMedia, usePreviewMedia } from './useTimelineMedia.js';
import { useTimelinePlayerStore } from './playerStore.js';

export interface MediaPlayerSegment {
  readonly id: string;
  readonly startMs: number;
  readonly endMs: number;
}

export interface MediaPlayerProps {
  readonly projectId: string;
  readonly segments?: readonly MediaPlayerSegment[];
  /** Compact slot for the transcript workspace; shares the same store. */
  readonly compact?: boolean;
}

const RATE_OPTIONS: readonly number[] = [0.5, 1, 1.25, 1.5, 2];

/**
 * Shared media player (Task 030).
 *
 * Signed playback URL in `<video>` only (referrer suppressed, never logged).
 * Full controls: play/pause, ±5s seek, ±1-frame step, scrubber, volume,
 * speed, fullscreen, PiP, prev/next segment. Keyboard: Space/K toggle,
 * ArrowLeft/Right ±5s, J/L ∓10s. Expired links refetch once and resume;
 * a second expiry shows the error state with retry. PiP/fullscreen buttons
 * hide when the platform lacks support. Compact mode renders the minimal
 * slot the transcript editor embeds; both modes share
 * `useTimelinePlayerStore`, so no second media element is needed.
 */
export function MediaPlayer({ projectId, segments = [], compact = false }: MediaPlayerProps): ReactNode {
  const queryClient = useQueryClient();
  const mediaQuery = usePreviewMedia(projectId);
  const videoRef = useRef<HTMLVideoElement>(null);
  const containerRef = useRef<HTMLElement>(null);

  const positionMs = useTimelinePlayerStore((s) => s.positionMs);
  const durationMs = useTimelinePlayerStore((s) => s.durationMs);
  const isPlaying = useTimelinePlayerStore((s) => s.isPlaying);
  const volume = useTimelinePlayerStore((s) => s.volume);
  const rate = useTimelinePlayerStore((s) => s.rate);
  const muted = useTimelinePlayerStore((s) => s.muted);
  const seekTargetMs = useTimelinePlayerStore((s) => s.seekTargetMs);
  const seekVersion = useTimelinePlayerStore((s) => s.seekVersion);
  const playRegion = useTimelinePlayerStore((s) => s.playRegion);
  const loopRegion = useTimelinePlayerStore((s) => s.loopRegion);

  const [refetchedOnce, setRefetchedOnce] = useState(false);
  const [resumeNote, setResumeNote] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);
  const resumePositionRef = useRef(0);
  const lastSeekVersionRef = useRef(0);

  const sortedSegments = useMemo(
    () => [...segments].sort((a, b) => a.startMs - b.startMs),
    [segments],
  );

  const mediaUrl = mediaQuery.data?.url;
  const expired = isExpiredError(mediaQuery.error ?? null);

  // Keep the signed URL out of Referer headers.
  useEffect(() => {
    if (mediaUrl !== undefined && videoRef.current !== null) {
      videoRef.current.setAttribute('referrerpolicy', 'no-referrer');
    }
  }, [mediaUrl]);

  // Apply volume/rate/mute to the element.
  useEffect(() => {
    const video = videoRef.current;
    if (video === null) {
      return;
    }
    try {
      video.volume = volume;
      video.muted = muted;
      video.playbackRate = rate;
    } catch {
      // Element may be detached in tests; store stays authoritative.
    }
  }, [volume, muted, rate, mediaUrl]);

  // Consume seek requests from the shared store (timeline, waveform,
  // transcript rows, issue jumps).
  useEffect(() => {
    if (seekTargetMs === undefined || seekVersion === lastSeekVersionRef.current) {
      return;
    }
    lastSeekVersionRef.current = seekVersion;
    const video = videoRef.current;
    if (video === null) {
      return;
    }
    try {
      if (Number.isFinite(video.duration) && video.duration > 0) {
        video.currentTime = Math.min(Math.max(0, seekTargetMs / 1000), video.duration);
      } else {
        video.currentTime = Math.max(0, seekTargetMs / 1000);
      }
    } catch {
      // Detached element in tests; position store already updated.
    }
  }, [seekTargetMs, seekVersion]);

  const seekTo = useCallback(
    (ms: number) => {
      const clamped = Number.isFinite(ms) ? Math.max(0, Math.round(ms)) : 0;
      const bounded = durationMs > 0 ? Math.min(clamped, durationMs) : clamped;
      useTimelinePlayerStore.getState().requestSeek(bounded);
      const video = videoRef.current;
      if (video !== null) {
        try {
          video.currentTime = bounded / 1000;
        } catch {
          // Store is authoritative when the element is unavailable.
        }
      }
    },
    [durationMs],
  );

  const seekBy = useCallback((deltaMs: number) => {
    const current = useTimelinePlayerStore.getState().positionMs;
    const video = videoRef.current;
    const base =
      video !== null && Number.isFinite(video.currentTime) && video.currentTime > 0
        ? Math.round(video.currentTime * 1000)
        : current;
    const duration = useTimelinePlayerStore.getState().durationMs;
    const next = Math.max(0, base + Math.round(deltaMs));
    const bounded = duration > 0 ? Math.min(next, duration) : next;
    useTimelinePlayerStore.getState().requestSeek(bounded);
    if (video !== null) {
      try {
        video.currentTime = bounded / 1000;
      } catch {
        // Store already updated.
      }
    }
  }, []);

  const togglePlay = useCallback(() => {
    const video = videoRef.current;
    const playing = useTimelinePlayerStore.getState().isPlaying;
    if (video === null) {
      useTimelinePlayerStore.getState().setPlaying(!playing);
      return;
    }
    try {
      if (playing) {
        const result = video.pause();
        void result;
        useTimelinePlayerStore.getState().setPlaying(false);
      } else {
        const result = video.play();
        if (result !== undefined && typeof (result as Promise<void>).then === 'function') {
          (result as Promise<void>).then(
            () => {
              useTimelinePlayerStore.getState().setPlaying(true);
            },
            () => {
              useTimelinePlayerStore.getState().setPlaying(false);
            },
          );
        } else {
          useTimelinePlayerStore.getState().setPlaying(true);
        }
      }
    } catch {
      useTimelinePlayerStore.getState().setPlaying(!playing);
    }
  }, []);

  const stepSegment = useCallback(
    (direction: 1 | -1) => {
      if (sortedSegments.length === 0) {
        return;
      }
      const current = useTimelinePlayerStore.getState().positionMs;
      if (direction === 1) {
        const next = sortedSegments.find((segment) => segment.startMs > current + 1);
        if (next !== undefined) {
          useTimelinePlayerStore.getState().selectSegment(next.id);
          seekTo(next.startMs);
        } else {
          const last = sortedSegments[sortedSegments.length - 1];
          if (last !== undefined) {
            useTimelinePlayerStore.getState().selectSegment(last.id);
            seekTo(last.endMs);
          }
        }
      } else {
        const reversed = [...sortedSegments].reverse();
        const prev = reversed.find((segment) => segment.startMs < current - 1);
        if (prev !== undefined) {
          useTimelinePlayerStore.getState().selectSegment(prev.id);
          seekTo(prev.startMs);
        } else {
          const first = sortedSegments[0];
          if (first !== undefined) {
            useTimelinePlayerStore.getState().selectSegment(first.id);
            seekTo(first.startMs);
          }
        }
      }
    },
    [sortedSegments, seekTo],
  );

  async function handleExpiredRefetch(): Promise<void> {
    if (refetchedOnce) {
      return;
    }
    resumePositionRef.current = useTimelinePlayerStore.getState().positionMs;
    setRefetchedOnce(true);
    setLocalError(null);
    await invalidateTimelineMedia(queryClient, projectId);
    try {
      const refreshed = await mediaQuery.refetch();
      if (refreshed.data?.url !== undefined) {
        setResumeNote(true);
        seekTo(resumePositionRef.current);
      }
    } catch {
      // Error state below renders from the query error.
    }
  }

  useEffect(() => {
    if (expired && !refetchedOnce) {
      void handleExpiredRefetch();
    }
    // Intentionally single-shot per mount; further expiries need explicit retry.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [expired]);

  function handleTimeUpdate(): void {
    const video = videoRef.current;
    if (video === null) {
      return;
    }
    const ms = Math.max(0, Math.round(video.currentTime * 1000));
    const region = useTimelinePlayerStore.getState().playRegion;
    const looping = useTimelinePlayerStore.getState().loopRegion;
    if (region !== undefined && looping && ms >= region.endMs) {
      seekTo(region.startMs);
      return;
    }
    if (region !== undefined && !looping && ms >= region.endMs) {
      try {
        video.pause();
      } catch {
        // Playback state is store-driven.
      }
      useTimelinePlayerStore.getState().setPlaying(false);
    }
    useTimelinePlayerStore.getState().reportPosition(ms);
  }

  function handleLoadedMetadata(): void {
    const video = videoRef.current;
    if (video === null) {
      return;
    }
    const seconds = video.duration;
    if (Number.isFinite(seconds) && seconds > 0) {
      useTimelinePlayerStore.getState().setDuration(Math.round(seconds * 1000));
    }
  }

  function handleMediaError(): void {
    if (!refetchedOnce) {
      resumePositionRef.current = useTimelinePlayerStore.getState().positionMs;
      setRefetchedOnce(true);
      void (async () => {
        await invalidateTimelineMedia(queryClient, projectId);
        try {
          const refreshed = await mediaQuery.refetch();
          if (refreshed.data?.url !== undefined) {
            setResumeNote(true);
            seekTo(resumePositionRef.current);
          }
        } catch {
          setLocalError('Media link expired. Request a fresh link to continue.');
        }
      })();
    } else {
      setLocalError('Media link expired. Request a fresh link to continue.');
    }
  }

  function handleKeyDown(event: KeyboardEvent<HTMLElement>): void {
    if (event.defaultPrevented) {
      return;
    }
    const target = event.target as HTMLElement | null;
    const tag = target?.tagName.toLowerCase() ?? '';
    if (tag === 'input' || tag === 'select' || tag === 'textarea') {
      return;
    }
    switch (event.key) {
      case ' ':
      case 'k':
      case 'K':
        event.preventDefault();
        togglePlay();
        break;
      case 'ArrowLeft':
        event.preventDefault();
        seekBy(-SEEK_STEP_MS);
        break;
      case 'ArrowRight':
        event.preventDefault();
        seekBy(SEEK_STEP_MS);
        break;
      case 'j':
      case 'J':
        event.preventDefault();
        seekBy(-2 * SEEK_STEP_MS);
        break;
      case 'l':
      case 'L':
        event.preventDefault();
        seekBy(2 * SEEK_STEP_MS);
        break;
      case 'Home':
        event.preventDefault();
        seekTo(0);
        break;
      case 'End':
        event.preventDefault();
        seekTo(durationMs);
        break;
      default:
        break;
    }
  }

  const fullscreenSupported =
    typeof document !== 'undefined' && typeof document.fullscreenEnabled === 'boolean'
      ? document.fullscreenEnabled
      : true;
  const pipSupported =
    typeof document !== 'undefined'
      ? (document as Document & { pictureInPictureEnabled?: boolean }).pictureInPictureEnabled !== false
      : true;

  function handleFullscreen(): void {
    const container = containerRef.current;
    if (container === null || typeof container.requestFullscreen !== 'function') {
      return;
    }
    try {
      const result = container.requestFullscreen() as unknown as Promise<void> | void;
      if (result !== undefined && typeof (result as Promise<void>).catch === 'function') {
        (result as Promise<void>).catch(() => undefined);
      }
    } catch {
      // Fullscreen is progressive enhancement; controls stay usable.
    }
  }

  function handlePictureInPicture(): void {
    const video = videoRef.current as (HTMLVideoElement & {
      requestPictureInPicture?: () => Promise<void>;
    }) | null;
    if (video === null || typeof video.requestPictureInPicture !== 'function') {
      return;
    }
    try {
      const result = video.requestPictureInPicture();
      if (result !== undefined && typeof result.catch === 'function') {
        result.catch(() => undefined);
      }
    } catch {
      // PiP is progressive enhancement.
    }
  }

  const scrubMax = Math.max(1, durationMs > 0 ? durationMs : 60_000);
  const doubleExpired = (expired && refetchedOnce) || localError !== null;

  if (mediaQuery.isPending) {
    return (
      <section data-testid="timeline-player" aria-label="Media player" data-compact={compact ? 'true' : 'false'}>
        <div data-testid="timeline-loading">
          <Skeleton lines={3} />
        </div>
      </section>
    );
  }

  if (doubleExpired || (mediaQuery.isError && refetchedOnce)) {
    return (
      <section data-testid="timeline-player" aria-label="Media player" data-compact={compact ? 'true' : 'false'}>
        <div data-testid="timeline-error">
          <Alert
            tone="warning"
            title="Media link expired"
            details={mediaQuery.error?.correlationId !== undefined ? `Ref: ${mediaQuery.error.correlationId}` : undefined}
          >
            <p>{localError ?? 'Media link expired. Request a fresh link to continue.'}</p>
            <button
              type="button"
              data-testid="timeline-retry"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                setRefetchedOnce(false);
                setLocalError(null);
                setResumeNote(false);
                void (async () => {
                  await invalidateTimelineMedia(queryClient, projectId);
                  await mediaQuery.refetch();
                })();
              }}
            >
              Retry media link
            </button>
          </Alert>
        </div>
      </section>
    );
  }

  if (mediaQuery.isError || mediaUrl === undefined) {
    return (
      <section data-testid="timeline-player" aria-label="Media player" data-compact={compact ? 'true' : 'false'}>
        <div data-testid="timeline-error">
          <Alert
            tone="error"
            title="Media unavailable"
            details={mediaQuery.error?.correlationId !== undefined ? `Ref: ${mediaQuery.error.correlationId}` : undefined}
          >
            <p>{mediaQuery.error?.message ?? 'Media could not be loaded. No data was changed.'}</p>
            <button
              type="button"
              data-testid="timeline-retry"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                void mediaQuery.refetch();
              }}
            >
              Retry media link
            </button>
          </Alert>
        </div>
      </section>
    );
  }

  return (
    <section
      ref={containerRef}
      data-testid="timeline-player"
      aria-label="Media player"
      data-compact={compact ? 'true' : 'false'}
      tabIndex={0}
      onKeyDown={handleKeyDown}
    >
      <video
        ref={videoRef}
        data-testid="timeline-media"
        src={mediaUrl}
        preload="metadata"
        playsInline
        onTimeUpdate={handleTimeUpdate}
        onLoadedMetadata={handleLoadedMetadata}
        onPlay={() => {
          useTimelinePlayerStore.getState().setPlaying(true);
        }}
        onPause={() => {
          useTimelinePlayerStore.getState().setPlaying(false);
        }}
        onError={handleMediaError}
        style={compact ? { width: '100%', maxHeight: '160px' } : { width: '100%', maxHeight: '320px' }}
      />
      <div data-testid="timeline-controls">
        <button
          type="button"
          data-testid="timeline-play-toggle"
          aria-label={isPlaying ? 'Pause' : 'Play'}
          aria-pressed={isPlaying}
          className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
          onClick={togglePlay}
        >
          {isPlaying ? 'Pause' : 'Play'}
        </button>
        <button
          type="button"
          data-testid="timeline-seek-back"
          aria-label="Back 5 seconds"
          title="Back 5 seconds (ArrowLeft)"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          onClick={() => {
            seekBy(-SEEK_STEP_MS);
          }}
        >
          −5s
        </button>
        <button
          type="button"
          data-testid="timeline-seek-forward"
          aria-label="Forward 5 seconds"
          title="Forward 5 seconds (ArrowRight)"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          onClick={() => {
            seekBy(SEEK_STEP_MS);
          }}
        >
          +5s
        </button>
        {!compact ? (
          <>
            <button
              type="button"
              data-testid="timeline-frame-back"
              aria-label="Back one frame"
              title="Back one frame"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                seekBy(-FRAME_MS);
              }}
            >
              −1f
            </button>
            <button
              type="button"
              data-testid="timeline-frame-forward"
              aria-label="Forward one frame"
              title="Forward one frame"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                seekBy(FRAME_MS);
              }}
            >
              +1f
            </button>
            <button
              type="button"
              data-testid="timeline-prev-segment"
              aria-label="Previous segment"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                stepSegment(-1);
              }}
            >
              Prev
            </button>
            <button
              type="button"
              data-testid="timeline-next-segment"
              aria-label="Next segment"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                stepSegment(1);
              }}
            >
              Next
            </button>
          </>
        ) : null}
        <label htmlFor={compact ? 'timeline-scrub-compact' : 'timeline-scrub'}>
          Playback position
        </label>
        <input
          id={compact ? 'timeline-scrub-compact' : 'timeline-scrub'}
          data-testid="timeline-scrub"
          type="range"
          min={0}
          max={scrubMax}
          step={1}
          value={Math.min(positionMs, scrubMax)}
          onChange={(event) => {
            seekTo(Number(event.target.value));
          }}
          aria-valuetext={`${String(positionMs)} of ${String(scrubMax)} milliseconds`}
        />
        <p data-testid="timeline-position" className="dp-muted">
          {`${String(positionMs)} ms`}
        </p>
        <p data-testid="timeline-duration" className="dp-muted">
          {`${String(durationMs)} ms`}
        </p>
        {!compact ? (
          <>
            <label htmlFor="timeline-volume">Volume</label>
            <input
              id="timeline-volume"
              data-testid="timeline-volume"
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={muted ? 0 : volume}
              onChange={(event) => {
                const next = Number(event.target.value);
                useTimelinePlayerStore.getState().setVolume(next);
                useTimelinePlayerStore.getState().setMuted(next === 0);
              }}
            />
            <button
              type="button"
              data-testid="timeline-mute"
              aria-label={muted ? 'Unmute' : 'Mute'}
              aria-pressed={muted}
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                useTimelinePlayerStore.getState().setMuted(!muted);
              }}
            >
              {muted ? 'Unmute' : 'Mute'}
            </button>
            <label htmlFor="timeline-rate">Speed</label>
            <select
              id="timeline-rate"
              data-testid="timeline-rate"
              value={String(rate)}
              onChange={(event) => {
                useTimelinePlayerStore.getState().setRate(Number(event.target.value));
              }}
            >
              {RATE_OPTIONS.map((option) => (
                <option key={option} value={String(option)}>
                  {`${String(option)}x`}
                </option>
              ))}
            </select>
            {fullscreenSupported ? (
              <button
                type="button"
                data-testid="timeline-fullscreen"
                aria-label="Fullscreen"
                title="Fullscreen"
                className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                onClick={handleFullscreen}
              >
                Fullscreen
              </button>
            ) : (
              <span data-testid="timeline-fullscreen" title="Fullscreen is not supported on this device.">
                Fullscreen unavailable
              </span>
            )}
            {pipSupported ? (
              <button
                type="button"
                data-testid="timeline-pip"
                aria-label="Picture in picture"
                title="Picture in picture"
                className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                onClick={handlePictureInPicture}
              >
                PiP
              </button>
            ) : (
              <span data-testid="timeline-pip" title="Picture in picture is not supported on this device.">
                PiP unavailable
              </span>
            )}
            {playRegion !== undefined ? (
              <p data-testid="timeline-region-note" className="dp-muted">
                {`Region ${String(playRegion.startMs)}–${String(playRegion.endMs)} ms${loopRegion ? ' (looping)' : ''}`}
              </p>
            ) : null}
          </>
        ) : null}
        {resumeNote ? (
          <p data-testid="timeline-resume-note" className="dp-muted">
            Media link refreshed — playback resumed.
          </p>
        ) : null}
        {expired && !refetchedOnce ? (
          <p data-testid="timeline-refreshing" className="dp-muted">
            Refreshing expired media link…
          </p>
        ) : null}
      </div>
      <div hidden>
        <span data-testid="timeline-query-key">{JSON.stringify(mediaQuery.data !== undefined ? 'timeline.media.ready' : 'timeline.media.pending')}</span>
      </div>
    </section>
  );
}

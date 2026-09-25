import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useTimelinePlayerStore } from './playerStore.js';
import { TIMELINE_DEBOUNCE_MS, clampPeak, isPeaksMissing, selectPeaksForWidth } from './types.js';
import { useWaveformPeaks } from './useTimelineMedia.js';

export interface WaveformProps {
  readonly projectId: string;
  readonly onSeek?: (ms: number) => void;
}

function drawWaveform(
  canvas: HTMLCanvasElement,
  peaks: readonly number[],
  positionMs: number,
  durationMs: number,
): void {
  const context = canvas.getContext('2d');
  if (context === null) {
    return;
  }
  const rect = canvas.getBoundingClientRect();
  const cssWidth = Math.max(1, Math.round(rect.width === 0 ? 320 : rect.width));
  const cssHeight = Math.max(1, Math.round(rect.height === 0 ? 96 : rect.height));
  const ratio = typeof window !== 'undefined' && Number.isFinite(window.devicePixelRatio) ? window.devicePixelRatio : 1;
  canvas.width = Math.round(cssWidth * ratio);
  canvas.height = Math.round(cssHeight * ratio);
  context.save();
  context.scale(ratio, ratio);
  const styles = typeof window !== 'undefined' ? window.getComputedStyle(document.body) : undefined;
  const track = styles?.getPropertyValue('--color-neutral-status-bg').replace(/^\s+|\s+$/g, '') ?? '';
  const played = styles?.getPropertyValue('--color-brand').replace(/^\s+|\s+$/g, '') ?? '';
  const fallbackTrack = 'var(--color-neutral-status-bg)';
  const fallbackPlayed = 'var(--color-brand)';
  context.clearRect(0, 0, cssWidth, cssHeight);
  const count = peaks.length;
  if (count === 0) {
    context.restore();
    return;
  }
  const barWidth = Math.max(1, cssWidth / count);
  const progress = durationMs > 0 ? Math.min(1, Math.max(0, positionMs / durationMs)) : 0;
  for (let index = 0; index < count; index += 1) {
    const sample = clampPeak(peaks[index]);
    const barHeight = Math.max(1, Math.round(sample * cssHeight));
    const x = Math.floor(index * barWidth);
    const y = Math.floor((cssHeight - barHeight) / 2);
    const isPlayed = index / count <= progress;
    if (isPlayed && played !== '') {
      context.fillStyle = played;
    } else if (track !== '') {
      context.fillStyle = track;
    } else if (isPlayed) {
      context.fillStyle = fallbackPlayed;
    } else {
      context.fillStyle = fallbackTrack;
    }
    context.fillRect(x, y, Math.max(1, Math.floor(barWidth) - 1), barHeight);
  }
  const playheadX = Math.floor(progress * cssWidth);
  context.fillRect(playheadX, 0, 2, cssHeight);
  context.restore();
}

/**
 * Canvas waveform from `WaveformPeaks` only (Task 030, R1).
 *
 * The data layer is `useWaveformPeaks` (peaks endpoint); this component
 * never requests archival media for visualization. Canvas draws are
 * memoized on peaks/size/position, resize redraws via ResizeObserver, and
 * scrub seeks are debounced. Missing peaks render a skeleton plus a
 * progress link while the player stays functional.
 */
export function Waveform({ projectId, onSeek }: WaveformProps): ReactNode {
  const peaksQuery = useWaveformPeaks(projectId);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const wrapRef = useRef<HTMLDivElement>(null);
  const [canvasWidth, setCanvasWidth] = useState(320);
  const scrubTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const positionMs = useTimelinePlayerStore((s) => s.positionMs);
  const durationMsStore = useTimelinePlayerStore((s) => s.durationMs);

  const peaks = peaksQuery.data;
  const missing = peaks === undefined ? false : isPeaksMissing(peaks);
  const durationMs = peaks?.durationMs ?? durationMsStore;

  const series = useMemo(
    () => (peaks === undefined ? [] : selectPeaksForWidth(peaks, canvasWidth)),
    [peaks, canvasWidth],
  );

  const renderWaveform = useCallback(() => {
    const canvas = canvasRef.current;
    if (canvas === null) {
      return;
    }
    drawWaveform(canvas, series, positionMs, durationMs > 0 ? durationMs : (peaks?.durationMs ?? 0));
  }, [series, positionMs, durationMs, peaks]);

  useEffect(() => {
    renderWaveform();
  }, [renderWaveform]);

  useEffect(
    () => () => {
      if (scrubTimerRef.current !== undefined) {
        clearTimeout(scrubTimerRef.current);
      }
    },
    [],
  );

  // Resize-observer redraw: canvas follows its container width.
  useEffect(() => {
    const wrap = wrapRef.current;
    if (wrap === null || typeof ResizeObserver === 'undefined') {
      return;
    }
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      const width = entry !== undefined ? Math.round(entry.contentRect.width) : 320;
      setCanvasWidth(Math.max(64, width));
    });
    observer.observe(wrap);
    return () => {
      observer.disconnect();
    };
  }, []);

  function queueSeek(ms: number): void {
    if (scrubTimerRef.current !== undefined) {
      clearTimeout(scrubTimerRef.current);
    }
    scrubTimerRef.current = setTimeout(() => {
      if (onSeek !== undefined) {
        onSeek(ms);
      } else {
        useTimelinePlayerStore.getState().requestSeek(ms);
      }
    }, TIMELINE_DEBOUNCE_MS);
  }

  function seekFromClientX(clientX: number): void {
    const canvas = canvasRef.current;
    if (canvas === null) {
      return;
    }
    const rect = canvas.getBoundingClientRect();
    const ratio = rect.width > 0 ? (clientX - rect.left) / rect.width : 0;
    const clamped = Math.min(1, Math.max(0, ratio));
    const total = durationMs > 0 ? durationMs : (peaks?.durationMs ?? 0);
    queueSeek(Math.round(clamped * total));
  }

  if (peaksQuery.isPending) {
    return (
      <section data-testid="timeline-waveform" aria-label="Waveform">
        <div data-testid="timeline-waveform-skeleton">
          <Skeleton lines={3} />
        </div>
      </section>
    );
  }

  if (peaksQuery.isError) {
    return (
      <section data-testid="timeline-waveform" aria-label="Waveform">
        <div data-testid="timeline-waveform-error">
          <Alert
            tone="warning"
            title="Waveform unavailable"
            details={peaksQuery.error?.correlationId !== undefined ? `Ref: ${peaksQuery.error.correlationId}` : undefined}
          >
            <p>Waveform peaks could not be loaded. Playback remains available.</p>
            <button
              type="button"
              data-testid="timeline-waveform-retry"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                void peaksQuery.refetch();
              }}
            >
              Retry waveform
            </button>
          </Alert>
        </div>
      </section>
    );
  }

  if (peaks === undefined || missing || series.length === 0) {
    return (
      <section data-testid="timeline-waveform" aria-label="Waveform">
        <div data-testid="timeline-waveform-skeleton">
          <Skeleton lines={3} />
        </div>
        <p data-testid="timeline-waveform-missing" className="dp-muted">
          Waveform is still being prepared — playback remains available.
        </p>
        <Link data-testid="timeline-waveform-progress-link" to={`/projects/${projectId}`}>
          View processing progress
        </Link>
      </section>
    );
  }

  const scrubMax = Math.max(1, durationMs > 0 ? durationMs : (peaks.durationMs ?? 1));

  return (
    <section data-testid="timeline-waveform" aria-label="Waveform">
      <div ref={wrapRef} data-testid="timeline-waveform-wrap">
        <canvas
          ref={canvasRef}
          data-testid="timeline-waveform-canvas"
          data-peaks={String(series.length)}
          data-position={String(positionMs)}
          role="img"
          aria-label={`Waveform with ${String(series.length)} peaks`}
          tabIndex={0}
          onClick={(event) => {
            seekFromClientX(event.clientX);
          }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowLeft') {
              event.preventDefault();
              queueSeek(Math.max(0, positionMs - 5000));
            } else if (event.key === 'ArrowRight') {
              event.preventDefault();
              queueSeek(positionMs + 5000);
            }
          }}
          style={{ width: '100%', height: '96px', display: 'block' }}
        />
      </div>
      <label htmlFor="timeline-waveform-scrub">Waveform position</label>
      <input
        id="timeline-waveform-scrub"
        data-testid="timeline-waveform-scrub"
        type="range"
        min={0}
        max={scrubMax}
        step={1}
        value={Math.min(positionMs, scrubMax)}
        onChange={(event) => {
          queueSeek(Number(event.target.value));
        }}
      />
    </section>
  );
}

export const MemoizedWaveform = memo(Waveform);

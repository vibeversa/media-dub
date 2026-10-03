import { memo, useEffect, useMemo, useRef } from 'react';
import type { ReactNode, WheelEvent } from 'react';
import { Link } from 'react-router-dom';
import { useTimelinePlayerStore } from './playerStore.js';
import {
  TIMELINE_DEBOUNCE_MS,
  debounce,
  deriveTimelineGaps,
  deriveTimelineMarkers,
  markerOwnerLink,
  resolveIssueTarget,
} from './types.js';
import type { TimelineIssue, TimelineMarker, TimelineSegmentView } from './types.js';
import { useTranslation } from 'react-i18next';

export interface TimelineProps {
  readonly projectId: string;
  readonly segments: readonly TimelineSegmentView[];
  readonly issues?: readonly TimelineIssue[];
  readonly selectedId?: string;
  readonly onSelect?: (segmentId: string) => void;
}

const LANE_WIDTH_PX = 800;

function toMsLabel(ms: number): string {
  return `${String(Math.max(0, Math.round(ms)))} ms`;
}

interface LaneProps {
  readonly testId: string;
  readonly label: string;
  readonly children?: ReactNode;
}

function Lane({ testId, label, children }: LaneProps): ReactNode {
  return (
    <div data-testid={testId} aria-label={label}>
      <p className="dp-muted" aria-hidden="true">
        {label}
      </p>
      {children}
    </div>
  );
}

const MemoVideoLane = memo(function MemoVideoLane({ durationMs }: { readonly durationMs: number }): ReactNode {
    const { t } = useTranslation();
return (
    <Lane testId="timeline-lane-video" label={t('timeline:timeline.video-lane')}>
      <div data-testid="timeline-video-track" data-duration={String(durationMs)}>
        <span className="dp-muted">{t('timeline:timeline.video-preview-track-read-only-timing')}</span>
      </div>
    </Lane>
  );
});

const MemoSourceAudioLane = memo(function MemoSourceAudioLane({
  durationMs,
}: {
  readonly durationMs: number;
}): ReactNode {
    const { t } = useTranslation();
return (
    <Lane testId="timeline-lane-source-audio" label={t('timeline:timeline.source-audio-lane')}>
      <div data-testid="timeline-source-audio-track" data-duration={String(durationMs)}>
        <span className="dp-muted">{t('timeline:timeline.source-audio-track-read-only-timing')}</span>
      </div>
    </Lane>
  );
});

const MemoGeneratedAudioLane = memo(function MemoGeneratedAudioLane({
  durationMs,
}: {
  readonly durationMs: number;
}): ReactNode {
    const { t } = useTranslation();
return (
    <Lane testId="timeline-lane-generated-audio" label={t('timeline:timeline.generated-audio-lane')}>
      <div data-testid="timeline-generated-audio-track" data-duration={String(durationMs)}>
        <span className="dp-muted">{t('timeline:timeline.generated-audio-track-read-only-timing')}</span>
      </div>
    </Lane>
  );
});

/**
 * Five-lane timeline with read-only timing (Task 030, R2–R4).
 *
 * Lanes: video, source-audio, dialogue (transcript segments),
 * generated-audio, markers. Interactions: zoom (wheel/ctrl+wheel + buttons),
 * pan (drag), click-to-select segment, click/drag seek, play-region loop,
 * issue-jump. Markers carry text + pattern encoding with tooltips and
 * owning-surface links. Segment boundaries come from the aggregate only;
 * this surface exposes no timing adjustments. Lanes are memoized, markers
 * virtualized to the viewport, and zoom/pan/seek handlers debounced.
 */
export function Timeline({ projectId, segments, issues = [], selectedId, onSelect }: TimelineProps): ReactNode {
    const { t } = useTranslation();
const positionMs = useTimelinePlayerStore((s) => s.positionMs);
  const viewportStartMs = useTimelinePlayerStore((s) => s.viewportStartMs);
  const pxPerMs = useTimelinePlayerStore((s) => s.pxPerMs);
  const playRegion = useTimelinePlayerStore((s) => s.playRegion);
  const loopRegion = useTimelinePlayerStore((s) => s.loopRegion);
  const storeSelectedId = useTimelinePlayerStore((s) => s.selectedSegmentId);

  const activeSelectedId = selectedId ?? storeSelectedId;

  const rulerRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<{ startX: number; startViewport: number; seeking: boolean } | null>(null);

  const durationMs = useMemo(() => {
    let max = 0;
    for (const segment of segments) {
      if (segment.endMs > max) {
        max = segment.endMs;
      }
    }
    return max;
  }, [segments]);

  const markers = useMemo(() => deriveTimelineMarkers(segments, { projectId }), [segments, projectId]);
  const gaps = useMemo(() => deriveTimelineGaps(segments), [segments]);

  const viewportEndMs = viewportStartMs + Math.round(LANE_WIDTH_PX / Math.max(0.01, pxPerMs));

  const visibleMarkers = useMemo(() => {
    const overscan = 2000;
    const filtered = markers.filter(
      (marker) => marker.atMs >= viewportStartMs - overscan && marker.atMs <= viewportEndMs + overscan,
    );
    // Cap the rendered window so long media stays virtualized.
    return filtered.slice(0, 120);
  }, [markers, viewportStartMs, viewportEndMs]);

  const debouncedSeekRef = useRef<((ms: number) => void) | null>(null);
  const debouncedZoomRef = useRef<((factor: number, anchor?: number) => void) | null>(null);
  const debouncedPanRef = useRef<((delta: number) => void) | null>(null);

  if (debouncedSeekRef.current === null) {
    debouncedSeekRef.current = debounce((ms: number) => {
      useTimelinePlayerStore.getState().requestSeek(ms);
    }, TIMELINE_DEBOUNCE_MS);
  }
  if (debouncedZoomRef.current === null) {
    debouncedZoomRef.current = debounce((factor: number, anchor?: number) => {
      useTimelinePlayerStore.getState().zoomViewport(factor, anchor);
    }, TIMELINE_DEBOUNCE_MS);
  }
  if (debouncedPanRef.current === null) {
    debouncedPanRef.current = debounce((delta: number) => {
      useTimelinePlayerStore.getState().panViewport(delta);
    }, TIMELINE_DEBOUNCE_MS);
  }

  useEffect(
    () => () => {
      (debouncedSeekRef.current as unknown as { cancel?: () => void })?.cancel?.();
      (debouncedZoomRef.current as unknown as { cancel?: () => void })?.cancel?.();
      (debouncedPanRef.current as unknown as { cancel?: () => void })?.cancel?.();
    },
    [],
  );

  function handleSelect(segmentId: string): void {
    useTimelinePlayerStore.getState().selectSegment(segmentId);
    const segment = segments.find((entry) => entry.id === segmentId);
    if (segment !== undefined) {
      debouncedSeekRef.current?.(segment.startMs);
    }
    onSelect?.(segmentId);
  }

  function handleRulerSeek(clientX: number): void {
    const ruler = rulerRef.current;
    if (ruler === null) {
      return;
    }
    const rect = ruler.getBoundingClientRect();
    const ratio = rect.width > 0 ? (clientX - rect.left) / rect.width : 0;
    const clamped = Math.min(1, Math.max(0, ratio));
    const total = durationMs > 0 ? durationMs : 60_000;
    const viewportSpan = viewportEndMs - viewportStartMs;
    const target = viewportSpan > 0 && viewportSpan < total ? viewportStartMs + Math.round(clamped * viewportSpan) : Math.round(clamped * total);
    debouncedSeekRef.current?.(Math.max(0, target));
  }

  function handleWheel(event: WheelEvent<HTMLDivElement>): void {
    const ruler = rulerRef.current;
    const rect = ruler?.getBoundingClientRect();
    const anchor =
      rect !== undefined && rect.width > 0
        ? viewportStartMs + Math.round(((event.clientX - rect.left) / rect.width) * (viewportEndMs - viewportStartMs))
        : undefined;
    if (event.ctrlKey || event.metaKey) {
      const factor = event.deltaY < 0 ? 1.25 : 0.8;
      debouncedZoomRef.current?.(factor, anchor);
    } else {
      const delta = Math.round(event.deltaX + event.deltaY);
      if (delta !== 0) {
        debouncedPanRef.current?.(delta);
      }
    }
  }

  function handleRegionFromSelection(): void {
    const id = activeSelectedId;
    const segment = id !== undefined ? segments.find((entry) => entry.id === id) : undefined;
    if (segment !== undefined) {
      useTimelinePlayerStore.getState().setPlayRegion({ startMs: segment.startMs, endMs: segment.endMs });
      useTimelinePlayerStore.getState().setLoopRegion(true);
    } else if (segments.length > 0) {
      const first = segments[0];
      const last = segments[segments.length - 1];
      if (first !== undefined && last !== undefined) {
        useTimelinePlayerStore.getState().setPlayRegion({ startMs: first.startMs, endMs: last.endMs });
        useTimelinePlayerStore.getState().setLoopRegion(true);
      }
    }
  }

  function renderMarker(marker: TimelineMarker): ReactNode {
    const link = markerOwnerLink(marker, projectId);
    return (
      <div
        key={marker.id}
        data-testid={`timeline-marker-${marker.kind}-${marker.id}`}
        data-kind={marker.kind}
        data-pattern={marker.pattern}
        title={t('timeline:timeline.owner', { v0: marker.cause, v1: marker.ownerSurface })}
        className={`dp-timeline-marker dp-timeline-marker-${marker.kind} dp-pattern-${marker.pattern}`}
      >
        <span data-testid={`timeline-marker-label-${marker.id}`}>{marker.label}</span>
        <span data-testid={`timeline-marker-pattern-${marker.id}`} aria-hidden="true">
          {`pattern: ${marker.pattern}`}
        </span>
        <Link data-testid={`timeline-marker-link-${marker.id}`} to={link}>
          {`Open in ${marker.ownerSurface}`}
        </Link>
      </div>
    );
  }

  return (
    <section
      data-testid="timeline"
      aria-label={t('timeline:timeline.timeline')}
      data-position={String(positionMs)}
      data-viewport-start={String(viewportStartMs)}
      data-viewport-end={String(viewportEndMs)}
      data-duration={String(durationMs)}
    >
      <div data-testid="timeline-toolbar">
        <button
          type="button"
          data-testid="timeline-zoom-out"
          aria-label={t('timeline:timeline.zoom-out')}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            debouncedZoomRef.current?.(0.8);
          }}
        >
          {t('timeline:timeline.zoom-out2')}
        </button>
        <button
          type="button"
          data-testid="timeline-zoom-in"
          aria-label={t('timeline:timeline.zoom-in')}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            debouncedZoomRef.current?.(1.25);
          }}
        >
          {t('timeline:timeline.zoom-in2')}
        </button>
        <span data-testid="timeline-zoom-label" className="dp-muted">
          {`Zoom ${pxPerMs.toFixed(2)} px/ms`}
        </span>
        <button
          type="button"
          data-testid="timeline-region-set"
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={handleRegionFromSelection}
        >
          {t('timeline:timeline.loop-selection')}
        </button>
        <button
          type="button"
          data-testid="timeline-region-clear"
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            useTimelinePlayerStore.getState().setPlayRegion(undefined);
            useTimelinePlayerStore.getState().setLoopRegion(false);
          }}
        >
          {t('timeline:timeline.clear-region')}
        </button>
        <button
          type="button"
          data-testid="timeline-region-loop"
          aria-pressed={loopRegion}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            useTimelinePlayerStore.getState().setLoopRegion(!loopRegion);
          }}
        >
          {loopRegion ? 'Loop on' : 'Loop off'}
        </button>
        {playRegion !== undefined ? (
          <span data-testid="timeline-region-note" className="dp-muted">
            {`Region ${toMsLabel(playRegion.startMs)}–${toMsLabel(playRegion.endMs)}`}
          </span>
        ) : null}
      </div>
      <div
        ref={rulerRef}
        data-testid="timeline-ruler"
        role="slider"
        aria-label={t('timeline:timeline.timeline-ruler')}
        aria-valuemin={0}
        aria-valuemax={Math.max(1, durationMs)}
        aria-valuenow={Math.min(positionMs, Math.max(1, durationMs))}
        tabIndex={0}
        onClick={(event) => {
          handleRulerSeek(event.clientX);
        }}
        onWheel={handleWheel}
        onKeyDown={(event) => {
          if (event.key === 'ArrowLeft') {
            event.preventDefault();
            debouncedSeekRef.current?.(Math.max(0, positionMs - 5000));
          } else if (event.key === 'ArrowRight') {
            event.preventDefault();
            debouncedSeekRef.current?.(positionMs + 5000);
          } else if (event.key === 'Home') {
            event.preventDefault();
            debouncedSeekRef.current?.(0);
          } else if (event.key === 'End') {
            event.preventDefault();
            debouncedSeekRef.current?.(durationMs);
          }
        }}
        onPointerDown={(event) => {
          (event.target as HTMLElement).setPointerCapture?.(event.pointerId);
          dragRef.current = { startX: event.clientX, startViewport: viewportStartMs, seeking: event.shiftKey };
        }}
        onPointerMove={(event) => {
          const drag = dragRef.current;
          if (drag === null || event.buttons === 0) {
            return;
          }
          const deltaPx = event.clientX - drag.startX;
          if (drag.seeking) {
            const deltaMs = Math.round(deltaPx / Math.max(0.01, pxPerMs));
            debouncedSeekRef.current?.(Math.max(0, drag.startViewport + deltaMs));
          } else {
            const deltaMs = Math.round(-deltaPx / Math.max(0.01, pxPerMs));
            debouncedPanRef.current?.(deltaMs);
          }
        }}
        onPointerUp={() => {
          dragRef.current = null;
        }}
      >
        <span className="dp-muted">{`Position ${toMsLabel(positionMs)} of ${toMsLabel(durationMs)}`}</span>
        <span data-testid="timeline-playhead" data-position={String(positionMs)}>
          {`Playhead ${toMsLabel(positionMs)}`}
        </span>
      </div>
      <MemoVideoLane durationMs={durationMs} />
      <MemoSourceAudioLane durationMs={durationMs} />
      <div data-testid="timeline-lane-dialogue" aria-label={t('timeline:timeline.dialogue-lane')}>
        <p className="dp-muted" aria-hidden="true">
          {t('timeline:timeline.dialogue-lane2')}
        </p>
        <div data-testid="timeline-dialogue-track" data-total={String(segments.length)} data-rendered={String(segments.length)}>
          {segments.map((segment) => (
            <button
              key={segment.id}
              type="button"
              data-testid={`timeline-segment-${segment.id}`}
              data-selected={activeSelectedId === segment.id ? 'true' : 'false'}
              data-start={String(segment.startMs)}
              data-end={String(segment.endMs)}
              title={t('timeline:timeline.read-only-timing', { v0: segment.id, v1: toMsLabel(segment.startMs), v2: toMsLabel(segment.endMs), v3: segment.speakerLabel })}
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                handleSelect(segment.id);
              }}
            >
              {`${segment.id} · ${segment.speakerLabel}`}
            </button>
          ))}
        </div>
        <div data-testid="timeline-gaps" data-total={String(gaps.length)}>
          {gaps.map((gap) => (
            <div
              key={gap.id}
              data-testid={`timeline-gap-${gap.id}`}
              data-start={String(gap.startMs)}
              data-end={String(gap.endMs)}
              title={t('timeline:timeline.gap-explicit-empty-region-never-collapsed', { v0: toMsLabel(gap.startMs), v1: toMsLabel(gap.endMs) })}
              className="dp-timeline-gap"
            >
              <span>{`Gap ${toMsLabel(gap.startMs)}–${toMsLabel(gap.endMs)}`}</span>
            </div>
          ))}
        </div>
      </div>
      <MemoGeneratedAudioLane durationMs={durationMs} />
      <div data-testid="timeline-lane-markers" aria-label={t('timeline:timeline.markers-lane')}>
        <p className="dp-muted" aria-hidden="true">
          {t('timeline:timeline.markers-lane2')}
        </p>
        <div
          data-testid="timeline-markers-track"
          data-total={String(markers.length)}
          data-rendered={String(visibleMarkers.length)}
        >
          {visibleMarkers.map(renderMarker)}
        </div>
      </div>
      {issues.length > 0 ? (
        <div data-testid="timeline-issues" aria-label={t('timeline:timeline.issues')}>
          <p className="dp-muted" aria-hidden="true">
            {t('timeline:timeline.issues-selecting-an-issue-jumps-the')}
          </p>
          {issues.map((issue) => {
            const target = resolveIssueTarget(issue, segments);
            return (
              <button
                key={issue.id}
                type="button"
                data-testid={`timeline-issue-${issue.id}`}
                data-target={target !== undefined ? String(target) : ''}
                title={target !== undefined ? `Jump to ${toMsLabel(target)}` : 'No timing available for this issue.'}
                className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                disabled={target === undefined}
                onClick={() => {
                  useTimelinePlayerStore.getState().jumpToIssue(target);
                }}
              >
                {issue.label}
              </button>
            );
          })}
        </div>
      ) : null}
      <div hidden>
        <span data-testid="timeline-query-key">{JSON.stringify(['timeline', projectId])}</span>
      </div>
    </section>
  );
}

export const MemoizedTimeline = memo(Timeline);

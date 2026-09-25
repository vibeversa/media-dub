import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { Inspector } from './Inspector.js';
import { VirtualizedSegmentList } from './VirtualizedSegmentList.js';
import { useTranscriptPlaybackStore } from './playerStore.js';
import { findActiveSegmentId } from './types.js';
import type { TranscriptFilter } from './types.js';
import { invalidateTranscript, useTranscript } from './useTranscript.js';

export interface TranscriptEditorProps {
  readonly projectId: string;
  /** Task 030 embeds its compact `MediaPlayer` here; defaults to the timing-only slot. */
  readonly playerSlot?: ReactNode;
}

const SEARCH_DEBOUNCE_MS = 200;

/**
 * Transcript editing workspace (Task 027).
 *
 * Three-pane layout: left player+waveform slot, center virtualized segment
 * list, right inspector. Fed by `useTranscript(projectId)` on
 * `queryKeys.transcript`; Task 026 progress events invalidate the same key
 * (see `queryKeyRegistry`). Selecting a row seeks the shared player to the
 * segment start ms-accurately; the active row highlights during playback
 * with autoscroll that pauses on manual scroll and resumes on re-enable.
 * All writes are versioned POSTs with `expectedVersion` (409 → stale banner,
 * refetch, draft preserved, never silent overwrite). Candidates stay
 * immutable snapshots — edits create manual versions only.
 */
export function TranscriptEditor({ projectId, playerSlot }: TranscriptEditorProps): ReactNode {
  const { push } = useToast();
  const queryClient = useQueryClient();
  const transcriptQuery = useTranscript(projectId);
  const positionMs = useTranscriptPlaybackStore((s) => s.positionMs);
  const seekTargetMs = useTranscriptPlaybackStore((s) => s.seekTargetMs);
  const seekVersion = useTranscriptPlaybackStore((s) => s.seekVersion);
  const requestSeek = useTranscriptPlaybackStore((s) => s.requestSeek);
  const reportPosition = useTranscriptPlaybackStore((s) => s.reportPosition);

  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);
  const [autoScroll, setAutoScroll] = useState(true);
  const [searchInput, setSearchInput] = useState('');
  const [debouncedQuery, setDebouncedQuery] = useState('');
  const [speakerFilter, setSpeakerFilter] = useState('');
  const [reviewOnly, setReviewOnly] = useState(false);
  const [staleMessage, setStaleMessage] = useState<string | null>(null);

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedQuery(searchInput);
    }, SEARCH_DEBOUNCE_MS);
    return () => {
      clearTimeout(timer);
    };
  }, [searchInput]);

  const filter: TranscriptFilter = useMemo(
    () => ({ query: debouncedQuery, speaker: speakerFilter, reviewOnly }),
    [debouncedQuery, speakerFilter, reviewOnly],
  );

  const segments = useMemo(() => transcriptQuery.data ?? [], [transcriptQuery.data]);
  const activeId = useMemo(() => findActiveSegmentId(segments, positionMs), [segments, positionMs]);

  useEffect(() => {
    if (segments.length > 0 && selectedId === undefined) {
      setSelectedId(segments[0]?.id);
    }
  }, [segments, selectedId]);

  useEffect(() => {
    if (segments.length === 0 || selectedId === undefined) {
      return;
    }
    const stillExists = segments.some((segment) => segment.id === selectedId);
    if (!stillExists) {
      const fallback = segments[0]?.id;
      setSelectedId(fallback);
      if (fallback !== undefined) {
        push('error', 'Segment was removed. Selection moved to the nearest surviving segment.');
      }
    }
  }, [segments, selectedId, push]);

  function handleSelect(segmentId: string): void {
    setSelectedId(segmentId);
    const segment = segments.find((s) => s.id === segmentId);
    if (segment !== undefined) {
      requestSeek(segment.startMs);
    }
  }

  function handleSeek(segmentId: string): void {
    setSelectedId(segmentId);
    const segment = segments.find((s) => s.id === segmentId);
    if (segment !== undefined) {
      requestSeek(segment.startMs);
    }
  }

  function handleManualScroll(): void {
    if (autoScroll) {
      setAutoScroll(false);
    }
  }

  async function handleStaleRefresh(): Promise<void> {
    setStaleMessage(null);
    await invalidateTranscript(queryClient, projectId, selectedId);
  }

  if (transcriptQuery.isPending && segments.length === 0) {
    return (
      <section data-testid="transcript-editor" aria-label="Transcript editor">
        <div data-testid="transcript-loading">
          <Skeleton lines={8} />
        </div>
      </section>
    );
  }

  if (transcriptQuery.isError) {
    return (
      <section data-testid="transcript-editor" aria-label="Transcript editor">
        <div data-testid="transcript-error">
          <ErrorState
            title="Transcript unavailable"
            message={transcriptQuery.error?.message ?? 'The transcript could not be loaded. No data was changed.'}
            correlationId={transcriptQuery.error?.correlationId}
            onRetry={() => {
              void transcriptQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  if (segments.length === 0) {
    return (
      <section data-testid="transcript-editor" aria-label="Transcript editor">
        <div data-testid="transcript-empty">
          <EmptyState
            title="No transcript yet"
            description="Start processing to generate the transcript. Editing appears here once segments exist."
          />
          <Link data-testid="transcript-empty-processing-link" to={`/projects/${projectId}`}>
            Go to processing
          </Link>
        </div>
      </section>
    );
  }

  const selected = selectedId !== undefined ? segments.find((s) => s.id === selectedId) : undefined;
  const speakerOptions = [...new Set(segments.map((s) => s.speakerLabel))].sort();

  return (
    <section data-testid="transcript-editor" aria-label="Transcript editor">
      {staleMessage !== null ? (
        <div data-testid="transcript-stale-banner">
          <Alert tone="warning" title="Segment changed elsewhere">
            <p>{staleMessage}</p>
            <button
              type="button"
              data-testid="transcript-stale-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void handleStaleRefresh();
              }}
            >
              Refresh transcript
            </button>
          </Alert>
        </div>
      ) : null}
      <div data-testid="transcript-filters">
        <label htmlFor="transcript-search">Search transcript</label>
        <input
          id="transcript-search"
          data-testid="transcript-search"
          type="search"
          value={searchInput}
          placeholder="Search text or speaker…"
          onChange={(event) => {
            setSearchInput(event.target.value);
          }}
        />
        <label htmlFor="transcript-speaker-filter">Speaker</label>
        <select
          id="transcript-speaker-filter"
          data-testid="transcript-speaker-filter"
          value={speakerFilter}
          onChange={(event) => {
            setSpeakerFilter(event.target.value);
          }}
        >
          <option value="">All speakers</option>
          {speakerOptions.map((speaker) => (
            <option key={speaker} value={speaker}>
              {speaker}
            </option>
          ))}
        </select>
        <label htmlFor="transcript-review-only">
          <input
            id="transcript-review-only"
            data-testid="transcript-review-only"
            type="checkbox"
            checked={reviewOnly}
            onChange={(event) => {
              setReviewOnly(event.target.checked);
            }}
          />
          Needs review only
        </label>
        <label htmlFor="transcript-autoscroll-toggle">
          <input
            id="transcript-autoscroll-toggle"
            data-testid="transcript-autoscroll-toggle"
            type="checkbox"
            checked={autoScroll}
            onChange={(event) => {
              setAutoScroll(event.target.checked);
            }}
          />
          Autoscroll
        </label>
      </div>
      <div style={{ display: 'flex', gap: '1rem', alignItems: 'flex-start' }}>
        <div data-testid="transcript-pane-player" style={{ flex: '0 0 280px' }}>
          {playerSlot ?? (
            <div
              data-testid="transcript-player"
              data-seek-target={seekTargetMs !== undefined ? String(seekTargetMs) : ''}
              data-position={String(positionMs)}
              data-seek-version={String(seekVersion)}
              data-active-segment={activeId ?? ''}
            >
              <p data-testid="transcript-player-position">{`Position ${String(positionMs)} ms`}</p>
              {seekTargetMs !== undefined ? (
                <p data-testid="transcript-player-seek">{`Seek → ${String(seekTargetMs)} ms`}</p>
              ) : null}
              <label htmlFor="transcript-player-scrub">Playback position</label>
              <input
                id="transcript-player-scrub"
                data-testid="transcript-player-scrub"
                type="range"
                min={0}
                max={Math.max(1, segments[segments.length - 1]?.endMs ?? 1)}
                value={positionMs}
                onChange={(event) => {
                  reportPosition(Number(event.target.value));
                }}
              />
              <p className="dp-muted" data-testid="transcript-player-note">
                Compact player slot — Task 030 MediaPlayer mounts here.
              </p>
            </div>
          )}
        </div>
        <div data-testid="transcript-pane-list" style={{ flexGrow: 1, minWidth: 0 }}>
          <VirtualizedSegmentList
            segments={segments}
            selectedId={selectedId}
            positionMs={positionMs}
            autoScroll={autoScroll}
            filter={filter}
            onSelect={handleSelect}
            onSeek={handleSeek}
            onManualScroll={handleManualScroll}
          />
        </div>
        <div data-testid="transcript-pane-inspector" style={{ flex: '0 0 320px' }}>
          <Inspector
            projectId={projectId}
            segment={selected}
            onStale={(message) => {
              setStaleMessage(message);
            }}
            staleVersion={selected?.selectionVersion}
          />
        </div>
      </div>
      <div hidden>
        <span data-testid="transcript-query-key">{JSON.stringify(queryKeys.transcript.list(projectId))}</span>
      </div>
    </section>
  );
}

// Re-exported filter type lives in types.ts; this module only exports the editor component.

import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useTranscript } from '../transcript/useTranscript.js';
import { MediaPlayer } from './MediaPlayer.js';
import { Timeline } from './Timeline.js';
import { Waveform } from './Waveform.js';
import type { TimelineIssue } from './types.js';

export interface TimelineWorkspaceProps {
  readonly projectId: string;
}

/**
 * Timeline workspace (Task 030).
 *
 * Composes the shared `MediaPlayer`, peaks-only `Waveform`, and five-lane
 * `Timeline` over aggregate segment timing. Issues for issue-jump derive
 * from review-open / flagged segments (Task 032 owns the review queue; this
 * surface only jumps the playhead). Timing stays display-only throughout.
 */
export function TimelineWorkspace({ projectId }: TimelineWorkspaceProps): ReactNode {
  const transcriptQuery = useTranscript(projectId);

  const segments = useMemo(() => transcriptQuery.data ?? [], [transcriptQuery.data]);

  const issues = useMemo<readonly TimelineIssue[]>(() => {
    const out: TimelineIssue[] = [];
    for (const segment of segments) {
      const open = (segment.reviewStatus ?? '').toLowerCase() === 'open';
      if (open || segment.qualityCodes.length > 0) {
        out.push({
          id: `issue-${segment.id}`,
          segmentId: segment.id,
          atMs: segment.startMs,
          label: `Issue ${segment.id}`,
        });
      }
    }
    return out.slice(0, 50);
  }, [segments]);

  if (transcriptQuery.isPending) {
    return (
      <section data-testid="timeline-workspace" aria-label="Timeline workspace">
        <div data-testid="timeline-workspace-loading">
          <Skeleton lines={6} />
        </div>
      </section>
    );
  }

  if (transcriptQuery.isError) {
    return (
      <section data-testid="timeline-workspace" aria-label="Timeline workspace">
        <div data-testid="timeline-workspace-error">
          <ErrorState
            title="Timeline unavailable"
            message={transcriptQuery.error?.message ?? 'The timeline could not be loaded. No data was changed.'}
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
      <section data-testid="timeline-workspace" aria-label="Timeline workspace">
        <div data-testid="timeline-workspace-empty">
          <Alert tone="info" title="No timeline yet">
            <p>No segments exist for this project yet. Start processing to populate the timeline.</p>
            <Link data-testid="timeline-workspace-empty-link" to={`/projects/${projectId}`}>
              Go to processing
            </Link>
          </Alert>
        </div>
      </section>
    );
  }

  return (
    <section data-testid="timeline-workspace" aria-label="Timeline workspace">
      <div data-testid="timeline-workspace-player">
        <MediaPlayer projectId={projectId} segments={segments} />
      </div>
      <div data-testid="timeline-workspace-waveform">
        <Waveform projectId={projectId} />
      </div>
      <div data-testid="timeline-workspace-timeline">
        <Timeline projectId={projectId} segments={segments} issues={issues} />
      </div>
      <div hidden>
        <span data-testid="timeline-workspace-query-key">{JSON.stringify(queryKeys.timeline.all(projectId))}</span>
      </div>
    </section>
  );
}

import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useMediaQuery } from '../../app/useMediaQuery.js';
import { useTranscript } from '../transcript/useTranscript.js';
import { MediaPlayer } from './MediaPlayer.js';
import { Timeline } from './Timeline.js';
import { Waveform } from './Waveform.js';
import { TIMELINE_LIST_MEDIA_QUERY } from './timelineResponsive.js';
import { TimelineListMode } from './TimelineListMode.js';
import type { TimelineIssue } from './types.js';
import { useTranslation } from 'react-i18next';

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
    const { t } = useTranslation();
const transcriptQuery = useTranscript(projectId);

  // GAP-023: below the tablet breakpoint the canvas waveform plus the
  // five-lane timeline degrade to a stacked list of the same segments.
  const compact = useMediaQuery(TIMELINE_LIST_MEDIA_QUERY);

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
      <section data-testid="timeline-workspace" aria-label={t('timeline:timelineWorkspace.timeline-workspace')}>
        <div data-testid="timeline-workspace-loading">
          <Skeleton lines={6} />
        </div>
      </section>
    );
  }

  if (transcriptQuery.isError) {
    return (
      <section data-testid="timeline-workspace" aria-label={t('timeline:timelineWorkspace.timeline-workspace2')}>
        <div data-testid="timeline-workspace-error">
          <ErrorState
            title={t('timeline:timelineWorkspace.timeline-unavailable')}
            message={transcriptQuery.error?.message ?? t('timeline:timelineWorkspace.the-timeline-could-not-be-loaded')}
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
      <section data-testid="timeline-workspace" aria-label={t('timeline:timelineWorkspace.timeline-workspace3')}>
        <div data-testid="timeline-workspace-empty">
          <Alert tone="info" title={t('timeline:timelineWorkspace.no-timeline-yet')}>
            <p>{t('timeline:timelineWorkspace.no-segments-exist-for-this-project')}</p>
            <Link data-testid="timeline-workspace-empty-link" to={`/projects/${projectId}`}>
              {t('timeline:timelineWorkspace.go-to-processing')}
            </Link>
          </Alert>
        </div>
      </section>
    );
  }

  return (
    <section data-testid="timeline-workspace" aria-label={t('timeline:timelineWorkspace.timeline-workspace4')}>
      <div data-testid="timeline-workspace-player">
        <MediaPlayer projectId={projectId} segments={segments} />
      </div>
      {compact ? (
        // GAP-023: below the tablet breakpoint the canvas waveform plus the
        // five-lane timeline degrade to a stacked list of the same segments.
        <TimelineListMode projectId={projectId} segments={segments} issues={issues} />
      ) : (
        <>
          <div data-testid="timeline-workspace-waveform">
            <Waveform projectId={projectId} />
          </div>
          <div data-testid="timeline-workspace-timeline">
            <Timeline projectId={projectId} segments={segments} issues={issues} />
          </div>
        </>
      )}
      {/* Layout-mode probe for tests: an attribute, never a rendered string. */}
      <div hidden data-testid="timeline-workspace-layout-mode" data-mode={compact ? 'list' : 'canvas'} />
      <div hidden>
        <span data-testid="timeline-workspace-query-key">{JSON.stringify(queryKeys.timeline.all(projectId))}</span>
      </div>
    </section>
  );
}

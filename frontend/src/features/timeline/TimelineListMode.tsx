import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import type { TranscriptSegmentView } from '../transcript/types.js';
import { reviewStateKey } from './timelineResponsive.js';
import { formatPlayerTime, type TimelineIssue } from './types.js';

export interface TimelineListModeProps {
  readonly projectId: string;
  readonly segments: readonly TranscriptSegmentView[];
  readonly issues: readonly TimelineIssue[];
}

/**
 * Small-screen timeline degradation (GAP-023, Plan B 12.12 / 15.8 R4).
 *
 * Below the tablet breakpoint the canvas waveform plus the five-lane timeline
 * are unreadable, so the workspace renders this stacked list instead: one row
 * per segment with speaker, time range, review state, and a jump link. Same
 * data, same display-only timing — no segment boundary is computed or moved
 * here, and nothing is hidden that the wide layout would show.
 */
export function TimelineListMode({ projectId, segments, issues }: TimelineListModeProps): ReactNode {
  const { t } = useTranslation();
  const issueCountBySegment = new Map<string, number>();
  for (const issue of issues) {
    if (issue.segmentId === undefined) {
      continue;
    }

    issueCountBySegment.set(issue.segmentId, (issueCountBySegment.get(issue.segmentId) ?? 0) + 1);
  }

  return (
    <div data-testid="timeline-list-mode" className="flex flex-col gap-2">
      <ol data-testid="timeline-list" className="flex flex-col gap-2">
        {segments.map((segment) => (
          <li
            key={segment.id}
            data-testid={`timeline-list-row-${segment.id}`}
            className="dp-surface rounded border p-3"
          >
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <span data-testid={`timeline-list-speaker-${segment.id}`} className="dp-text font-medium">
                {segment.speakerLabel}
              </span>
              <span data-testid={`timeline-list-time-${segment.id}`} className="dp-muted text-sm">
                {`${formatPlayerTime(segment.startMs)} – ${formatPlayerTime(segment.endMs)}`}
              </span>
            </div>
            <p data-testid={`timeline-list-text-${segment.id}`} className="mt-1 text-sm">
              {segment.text}
            </p>
            <div className="mt-1 flex flex-wrap items-center gap-2">
              {segment.reviewStatus !== undefined && segment.reviewStatus !== '' ? (
                <span data-testid={`timeline-list-review-${segment.id}`} className="dp-muted text-xs">
                  {t(`timeline:list.review.${reviewStateKey(segment.reviewStatus)}`)}
                </span>
              ) : null}
              {issueCountBySegment.get(segment.id) ?? 0 > 0 ? (
                <span data-testid={`timeline-list-issues-${segment.id}`} className="dp-warning text-xs">
                  {t('timeline:list.issues')}
                </span>
              ) : null}
              <Link
                data-testid={`timeline-list-review-link-${segment.id}`}
                to={`/projects/${projectId}/review`}
                className="dp-muted text-xs underline"
              >
                {t('timeline:list.openReview')}
              </Link>
            </div>
          </li>
        ))}
      </ol>
    </div>
  );
}
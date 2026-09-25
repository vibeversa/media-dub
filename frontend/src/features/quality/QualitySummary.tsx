import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { iconForQualityStatus, labelForQualityStatus, patternForQualityStatus } from './types.js';
import type { QualityBackendStatus, QualitySummaryView } from './types.js';

export interface QualitySummaryProps {
  readonly projectId: string;
  readonly summary: QualitySummaryView | undefined;
  readonly isPending: boolean;
  readonly isQcPending: boolean;
}

const STATE_ORDER: readonly QualityBackendStatus[] = [
  'Pass',
  'PassWithWarnings',
  'RetryRequired',
  'ManualReviewRequired',
  'Blocked',
];

function countFor(summary: QualitySummaryView, status: QualityBackendStatus): number {
  switch (status) {
    case 'Pass':
      return summary.passed;
    case 'PassWithWarnings':
      return summary.warning;
    case 'RetryRequired':
      return summary.retry;
    case 'ManualReviewRequired':
      return summary.review;
    case 'Blocked':
      return summary.blocked;
  }
}

/**
 * QC summary (Task 032, R1 + R3).
 *
 * Five counts (passed/warning/retry/review/blocked) derived from the
 * evidence-backed issue list, so the summary always matches the list. Each
 * count links to the filtered issue list. Every badge carries icon + text
 * label + pattern in addition to color (never color alone); the blocked
 * state is announced with icon + text + pattern and a prominent banner owned
 * by the workspace.
 */
export function QualitySummary({ projectId, summary, isPending, isQcPending }: QualitySummaryProps): ReactNode {
  if (isPending || isQcPending || summary === undefined) {
    return (
      <section data-testid="quality-summary" aria-label="Quality summary">
        <div data-testid="quality-summary-loading">
          <Skeleton lines={3} />
        </div>
        <p data-testid="quality-pending" className="dp-muted">
          Quality checks are still running — live updates apply automatically.
        </p>
      </section>
    );
  }

  return (
    <section data-testid="quality-summary" aria-label="Quality summary">
      <h3>Quality summary</h3>
      <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexWrap: 'wrap', gap: 'var(--space-2)' }}>
        {STATE_ORDER.map((status) => {
          const count = countFor(summary, status);
          const label = labelForQualityStatus(status);
          const icon = iconForQualityStatus(status);
          const pattern = patternForQualityStatus(status);
          return (
            <li key={status} data-testid={`quality-summary-${label}`} data-count={String(count)} data-pattern={pattern}>
              <span data-testid={`quality-badge-${label}`} data-pattern={pattern} title={`Pattern: ${pattern}`}>
                <span data-testid={`quality-badge-icon-${label}`} aria-hidden="true">
                  {icon}
                </span>{' '}
                <span data-testid={`quality-badge-label-${label}`}>{label}</span>{' '}
                <span data-testid={`quality-badge-pattern-${label}`}>{pattern}</span>
              </span>{' '}
              <span data-testid={`quality-count-${label}`}>{String(count)}</span>{' '}
              <Link
                data-testid={`quality-summary-link-${label}`}
                to={`/projects/${projectId}/quality?status=${encodeURIComponent(status)}`}
              >
                View {label} issues
              </Link>
            </li>
          );
        })}
      </ul>
      <p data-testid="quality-summary-total" className="dp-muted">
        {`${String(summary.totalIssues)} issue(s) across ${String(summary.totalSegments)} segment(s); ${String(summary.cleanSegments)} clean.`}
      </p>
      <div hidden>
        <span data-testid="quality-summary-key">{JSON.stringify(queryKeys.quality.detail(projectId))}</span>
      </div>
    </section>
  );
}

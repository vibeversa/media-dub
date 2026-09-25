import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { EMPTY_REVIEW_FILTERS, REVIEW_QUEUE_RENDER_LIMIT, hasActiveReviewFilters } from './types.js';
import type { ReviewFilters, ReviewQueueItemView } from './types.js';
import { useReviewQueue } from './useReviewQueue.js';

export interface ReviewQueueProps {
  readonly projectId: string;
  readonly filters: ReviewFilters;
  readonly onFiltersChange: (next: ReviewFilters) => void;
  readonly selectedReviewId: string | undefined;
  readonly onSelect: (reviewId: string | undefined) => void;
}

/**
 * Review queue (Task 031).
 *
 * Fed by `useReviewQueue(projectId, filters)` on `queryKeys.review`. Every
 * active filter narrows server-side (see `toReviewListQuery`); the parent
 * owns filter state in URL search params (shareable). The list is windowed
 * (`data-total`/`data-rendered`, initial 50 rows + show-more) so large
 * backlogs never mount thousands of rows. Empty-filter results render
 * `EmptyState` with a clear-filters action, distinct from the true-empty
 * state.
 */
export function ReviewQueue({
  projectId,
  filters,
  onFiltersChange,
  selectedReviewId,
  onSelect,
}: ReviewQueueProps): ReactNode {
  const queueQuery = useReviewQueue(projectId, filters);
  const [renderLimit, setRenderLimit] = useState(REVIEW_QUEUE_RENDER_LIMIT);
  const [draft, setDraft] = useState<ReviewFilters>(filters);

  useEffect(() => {
    setDraft(filters);
  }, [filters]);

  const items = useMemo(() => queueQuery.data ?? [], [queueQuery.data]);
  const visible = useMemo(() => items.slice(0, renderLimit), [items, renderLimit]);

  function clearFilters(): void {
    const cleared: ReviewFilters = { ...EMPTY_REVIEW_FILTERS, project: filters.project };
    setDraft(cleared);
    setRenderLimit(REVIEW_QUEUE_RENDER_LIMIT);
    onFiltersChange(cleared);
  }

  function applyDraft(): void {
    setRenderLimit(REVIEW_QUEUE_RENDER_LIMIT);
    onFiltersChange(draft);
  }

  let body: ReactNode;
  if (projectId === '') {
    body = (
      <div data-testid="review-queue-needs-project">
        <EmptyState
          title="Select a project"
          description="Enter a project id to load its review queue."
        />
      </div>
    );
  } else if (queueQuery.isPending && queueQuery.data === undefined) {
    body = (
      <div data-testid="review-queue-loading">
        <Skeleton lines={6} />
      </div>
    );
  } else if (queueQuery.isError && queueQuery.data === undefined) {
    body = (
      <div data-testid="review-queue-error">
        <ErrorState
          title="Review queue unavailable"
          message={queueQuery.error?.message ?? 'The queue could not be loaded. No data was changed.'}
          correlationId={queueQuery.error?.correlationId}
          onRetry={() => {
            void queueQuery.refetch();
          }}
        />
      </div>
    );
  } else if (items.length === 0 && hasActiveReviewFilters({ ...filters, project: '' })) {
    body = (
      <div data-testid="review-queue-empty-filtered">
        <EmptyState
          title="No reviews match these filters"
          description="Filters exclude everything in this queue. Clear them to see all open items."
          action={
            <button
              type="button"
              data-testid="review-clear-filters"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={clearFilters}
            >
              Clear filters
            </button>
          }
        />
      </div>
    );
  } else if (items.length === 0) {
    body = (
      <div data-testid="review-queue-empty">
        <EmptyState
          title="No reviews pending"
          description="This project has no open review items. New flags appear here after processing."
        />
      </div>
    );
  } else {
    body = (
      <div data-testid="review-queue-list" data-total={items.length} data-rendered={visible.length}>
        <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 'var(--space-2)' }}>
          {visible.map((item) => (
            <QueueRow
              key={item.id}
              item={item}
              selected={item.id === selectedReviewId}
              onSelect={() => {
                onSelect(item.id === selectedReviewId ? undefined : item.id);
              }}
            />
          ))}
        </ul>
        {visible.length < items.length ? (
          <button
            type="button"
            data-testid="review-queue-more"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={() => {
              setRenderLimit((prev) => prev + REVIEW_QUEUE_RENDER_LIMIT);
            }}
          >
            {`Show more (${String(items.length - visible.length)} remaining)`}
          </button>
        ) : null}
      </div>
    );
  }

  return (
    <section data-testid="review-queue" aria-label="Review queue">
      <form
        data-testid="review-filters"
        onSubmit={(event) => {
          event.preventDefault();
          applyDraft();
        }}
      >
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 'var(--space-2)' }}>
          <label>
            Severity
            <input
              data-testid="review-filter-severity"
              value={draft.severity}
              autoComplete="off"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, severity: next }));
              }}
              onBlur={applyDraft}
            />
          </label>
          <label>
            Status
            <input
              data-testid="review-filter-status"
              value={draft.status}
              autoComplete="off"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, status: next }));
              }}
              onBlur={applyDraft}
            />
          </label>
          <label>
            Type
            <input
              data-testid="review-filter-type"
              value={draft.type}
              autoComplete="off"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, type: next }));
              }}
              onBlur={applyDraft}
            />
          </label>
          <label>
            Speaker
            <input
              data-testid="review-filter-speaker"
              value={draft.speaker}
              autoComplete="off"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, speaker: next }));
              }}
              onBlur={applyDraft}
            />
          </label>
          <label>
            Language
            <input
              data-testid="review-filter-language"
              value={draft.language}
              autoComplete="off"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, language: next }));
              }}
              onBlur={applyDraft}
            />
          </label>
          <label>
            Age
            <input
              data-testid="review-filter-age"
              value={draft.age}
              autoComplete="off"
              placeholder="e.g. older-than-7d"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, age: next }));
              }}
              onBlur={applyDraft}
            />
          </label>
          <button
            type="submit"
            data-testid="review-apply-filters"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          >
            Apply filters
          </button>
          <button
            type="button"
            data-testid="review-clear-filters-top"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={clearFilters}
          >
            Clear filters
          </button>
        </div>
      </form>
      {queueQuery.isError && queueQuery.data !== undefined ? (
        <div data-testid="review-queue-stale">
          <Alert tone="warning" title="Queue refresh failed" details={queueQuery.error?.correlationId}>
            <p>Showing the last loaded queue. New flags may be missing.</p>
          </Alert>
        </div>
      ) : null}
      {body}
      <div hidden>
        <span data-testid="review-queue-key">{JSON.stringify(queryKeys.review.list(projectId))}</span>
      </div>
    </section>
  );
}

function QueueRow({ item, selected, onSelect }: { readonly item: ReviewQueueItemView; readonly selected: boolean; readonly onSelect: () => void }): ReactNode {
  return (
    <li>
      <button
        type="button"
        data-testid={`review-row-${item.id}`}
        data-selected={selected ? 'true' : 'false'}
        data-status={item.status}
        aria-pressed={selected}
        className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
        style={{ width: '100%', textAlign: 'start' }}
        onClick={onSelect}
      >
        <span data-testid={`review-row-id-${item.id}`}>{item.id}</span>
        {' · '}
        <span data-testid={`review-row-status-${item.id}`}>{item.status}</span>
        {item.reason !== '' ? (
          <>
            {' · '}
            <span data-testid={`review-row-reason-${item.id}`}>{item.reason}</span>
          </>
        ) : null}
      </button>
    </li>
  );
}

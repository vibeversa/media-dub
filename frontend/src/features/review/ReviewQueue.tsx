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
import { useTranslation } from 'react-i18next';

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
    const { t } = useTranslation();
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
          title={t('review:reviewQueue.select-a-project')}
          description={t('review:reviewQueue.enter-a-project-id-to-load')}
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
          title={t('review:reviewQueue.review-queue-unavailable')}
          message={queueQuery.error?.message ?? t('review:reviewQueue.the-queue-could-not-be-loaded')}
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
          title={t('review:reviewQueue.no-reviews-match-these-filters')}
          description={t('review:reviewQueue.filters-exclude-everything-in-this-queue')}
          action={
            <button
              type="button"
              data-testid="review-clear-filters"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={clearFilters}
            >
              {t('review:reviewQueue.clear-filters')}
            </button>
          }
        />
      </div>
    );
  } else if (items.length === 0) {
    body = (
      <div data-testid="review-queue-empty">
        <EmptyState
          title={t('review:reviewQueue.no-reviews-pending')}
          description={t('review:reviewQueue.this-project-has-no-open-review')}
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
    <section data-testid="review-queue" aria-label={t('review:reviewQueue.review-queue')}>
      <form
        data-testid="review-filters"
        onSubmit={(event) => {
          event.preventDefault();
          applyDraft();
        }}
      >
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 'var(--space-2)' }}>
          <label>
            {t('review:reviewQueue.severity')}
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
            {t('review:reviewQueue.status')}
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
            {t('review:reviewQueue.type')}
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
            {t('review:reviewQueue.speaker')}
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
            {t('review:reviewQueue.language')}
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
            {t('review:reviewQueue.age')}
            <input
              data-testid="review-filter-age"
              value={draft.age}
              autoComplete="off"
              placeholder={t('review:reviewQueue.e-g-older-than-7d')}
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
            {t('review:reviewQueue.apply-filters')}
          </button>
          <button
            type="button"
            data-testid="review-clear-filters-top"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={clearFilters}
          >
            {t('review:reviewQueue.clear-filters2')}
          </button>
        </div>
      </form>
      {queueQuery.isError && queueQuery.data !== undefined ? (
        <div data-testid="review-queue-stale">
          <Alert tone="warning" title={t('review:reviewQueue.queue-refresh-failed')} details={queueQuery.error?.correlationId}>
            <p>{t('review:reviewQueue.showing-the-last-loaded-queue-new')}</p>
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

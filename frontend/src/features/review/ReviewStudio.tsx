import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { ReviewCard } from './ReviewCard.js';
import { ReviewQueue } from './ReviewQueue.js';
import { EMPTY_REVIEW_FILTERS, reviewFiltersFromSearchParams, reviewFiltersToSearchParams } from './types.js';
import type { ReviewFilters } from './types.js';

export interface ReviewStudioProps {
  /** Fixed project scope (project tab). When absent, the `project` URL param owns the scope. */
  readonly projectId?: string;
}

/**
 * Manual review studio (Task 031).
 *
 * Queue + single-screen card. Filter state lives in URL search params
 * (shareable links); the queue fetches server-side per filter set and the
 * card fetches the eleven-section context for the selection. Selecting a row
 * opens its card in place — no navigation away.
 */
export function ReviewStudio({ projectId: fixedProjectId }: ReviewStudioProps): ReactNode {
  const [searchParams, setSearchParams] = useSearchParams();
  const urlFilters = useMemo(() => reviewFiltersFromSearchParams(searchParams), [searchParams]);
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

  const projectId = fixedProjectId ?? urlFilters.project;
  const filters: ReviewFilters = useMemo(
    () => ({ ...urlFilters, project: fixedProjectId ?? urlFilters.project }),
    [urlFilters, fixedProjectId],
  );

  useEffect(() => {
    setSelectedId(undefined);
  }, [projectId]);

  function writeFilters(next: ReviewFilters): void {
    const params = reviewFiltersToSearchParams(fixedProjectId === undefined ? next : { ...next, project: urlFilters.project });
    setSearchParams(params, { preventScrollReset: true });
  }

  function writeProject(nextProject: string): void {
    const params = reviewFiltersToSearchParams({ ...filters, project: nextProject });
    setSearchParams(params, { preventScrollReset: true });
    setSelectedId(undefined);
  }

  return (
    <section data-testid="review-studio" aria-label="Manual review studio" data-project={projectId}>
      {fixedProjectId === undefined ? (
        <div style={{ marginBlockEnd: 'var(--space-3)' }}>
          <label htmlFor="review-project">
            Project
            <input
              id="review-project"
              data-testid="review-filter-project"
              value={urlFilters.project}
              autoComplete="off"
              placeholder="prj_…"
              onChange={(event) => {
                writeProject(event.target.value);
              }}
            />
          </label>
        </div>
      ) : null}
      <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'flex-start', flexWrap: 'wrap' }}>
        <div style={{ flex: '0 0 360px', minWidth: 0 }}>
          <ReviewQueue
            projectId={projectId}
            filters={{ ...EMPTY_REVIEW_FILTERS, ...filters, project: '' }}
            onFiltersChange={(next) => {
              writeFilters({ ...next, project: filters.project });
            }}
            selectedReviewId={selectedId}
            onSelect={setSelectedId}
          />
        </div>
        <div style={{ flexGrow: 1, minWidth: 0 }} data-testid="review-studio-detail">
          {selectedId === undefined ? (
            <p data-testid="review-studio-empty" className="dp-muted">
              Select a review to see its full context.
            </p>
          ) : (
            <ReviewCard
              projectId={projectId}
              reviewId={selectedId}
              onSettled={() => {
                setSelectedId(undefined);
              }}
            />
          )}
        </div>
      </div>
    </section>
  );
}
